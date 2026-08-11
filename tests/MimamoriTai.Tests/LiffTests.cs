using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Line;
using MimamoriTai.Web.Services;

namespace MimamoriTai.Tests;

/// <summary>Returns a canned response and records what was posted to LINE.</summary>
internal sealed class StubVerifyHandler(HttpStatusCode status, string json) : HttpMessageHandler
{
    public string? Path { get; private set; }

    public string? Form { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Path = request.RequestUri!.AbsolutePath;
        Form = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>Hands back a fixed identity without touching the network.</summary>
internal sealed class StubIdTokenVerifier(LineIdentity? identity, bool canVerify = true) : ILineIdTokenVerifier
{
    public bool CanVerify { get; } = canVerify;

    public Task<LineIdentity?> VerifyAsync(string? idToken, CancellationToken ct = default) =>
        Task.FromResult(identity);
}

/// <summary>
/// A LIFF page decides which family's status to show, so everything here is really a
/// test of an access-control boundary: only an ID token LINE has re-validated may
/// resolve to a household, and an unverifiable one must show nothing at all.
/// </summary>
public class LineIdTokenVerifierTests
{
    private static (LineIdTokenVerifier Verifier, StubVerifyHandler Handler) Create(
        string channelId,
        HttpStatusCode status = HttpStatusCode.OK,
        string json = """{"sub":"U-line-user","name":"家族"}""")
    {
        var handler = new StubVerifyHandler(status, json);
        var options = Options.Create(new LineOptions { LiffChannelId = channelId });

        return (
            new LineIdTokenVerifier(
                new HttpClient(handler),
                options,
                NullLogger<LineIdTokenVerifier>.Instance),
            handler);
    }

    [Fact]
    public void CanVerify_Is_False_Without_A_Channel_Id()
    {
        var (verifier, _) = Create(channelId: "");

        Assert.False(verifier.CanVerify);
    }

    [Fact]
    public async Task An_Unconfigured_Channel_Never_Verifies()
    {
        var (verifier, handler) = Create(channelId: "");

        var identity = await verifier.VerifyAsync("some-token");

        Assert.Null(identity);

        // Nothing may be sent: without the channel id LINE cannot check the audience,
        // so a "valid" response would say nothing about which app minted the token.
        Assert.Null(handler.Path);
    }

    [Fact]
    public async Task A_Blank_Token_Is_Not_Sent_To_Line()
    {
        var (verifier, handler) = Create(channelId: "1234567890");

        Assert.Null(await verifier.VerifyAsync(null));
        Assert.Null(await verifier.VerifyAsync("   "));
        Assert.Null(handler.Path);
    }

    [Fact]
    public async Task A_Valid_Token_Yields_The_Line_User_Id()
    {
        var (verifier, handler) = Create(channelId: "1234567890");

        var identity = await verifier.VerifyAsync("good-token");

        Assert.NotNull(identity);
        Assert.Equal("U-line-user", identity.LineUserId);
        Assert.Equal("家族", identity.DisplayName);
        Assert.Equal("/oauth2/v2.1/verify", handler.Path);
        Assert.Contains("id_token=good-token", handler.Form);
        Assert.Contains("client_id=1234567890", handler.Form);
    }

    [Fact]
    public async Task A_Rejected_Token_Yields_No_Identity()
    {
        var (verifier, _) = Create(
            channelId: "1234567890",
            status: HttpStatusCode.BadRequest,
            json: """{"error":"invalid_request"}""");

        Assert.Null(await verifier.VerifyAsync("expired-token"));
    }

    [Fact]
    public async Task A_Response_Without_Sub_Yields_No_Identity()
    {
        var (verifier, _) = Create(channelId: "1234567890", json: """{"aud":"1234567890"}""");

        Assert.Null(await verifier.VerifyAsync("odd-token"));
    }

    [Fact]
    public void An_Empty_Sub_Is_Rejected()
    {
        using var document = JsonDocument.Parse("""{"sub":""}""");

        Assert.Null(LineIdTokenVerifier.ParseIdentity(document.RootElement));
    }

    [Fact]
    public void A_Missing_Name_Claim_Is_Allowed()
    {
        using var document = JsonDocument.Parse("""{"sub":"U-abc"}""");

        var identity = LineIdTokenVerifier.ParseIdentity(document.RootElement);

        Assert.NotNull(identity);
        Assert.Equal("U-abc", identity.LineUserId);
        Assert.Null(identity.DisplayName);
    }
}

public class LiffSessionServiceTests
{
    private const string LineUserId = "U-line-user";

    private static LiffSessionService Create(TestDb db, LineIdentity? identity) =>
        new(db.Context, new StubIdTokenVerifier(identity), new FakeTimeProvider(DateTimeOffset.UtcNow));

    private static async Task LinkAsync(TestDb db, string lineUserId, bool isActive = true, int minutesAgo = 0)
    {
        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = db.HouseholdId,
            LineUserId = lineUserId,
            IsActive = isActive,
            LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)
        });

        await db.Context.SaveChangesAsync();
    }

    [Fact]
    public async Task An_Unverified_Token_Resolves_To_Nothing()
    {
        using var db = await new TestDb().SeedAsync();
        await LinkAsync(db, LineUserId);

        var session = await Create(db, identity: null).ResolveAsync("forged-token");

        Assert.Equal(LiffSessionStatus.NotSignedIn, session.Status);

        // The household exists and is linked; only the missing verification stops it
        // from being shown. This is the case a spoofed userId would have exploited.
        Assert.Null(session.View);
    }

    [Fact]
    public async Task A_Verified_But_Unlinked_User_Sees_No_Household()
    {
        using var db = await new TestDb().SeedAsync();

        var session = await Create(db, new LineIdentity("U-stranger", "他人")).ResolveAsync("token");

        Assert.Equal(LiffSessionStatus.NotLinked, session.Status);
        Assert.Null(session.View);
        Assert.Equal("他人", session.DisplayName);
    }

    [Fact]
    public async Task An_Inactive_Recipient_Is_Not_Linked()
    {
        using var db = await new TestDb().SeedAsync();
        await LinkAsync(db, LineUserId, isActive: false);

        var session = await Create(db, new LineIdentity(LineUserId, null)).ResolveAsync("token");

        // The family blocked the account: their LIFF view must stop showing the resident.
        Assert.Equal(LiffSessionStatus.NotLinked, session.Status);
    }

    [Fact]
    public async Task A_Linked_User_Sees_Their_Household_Status()
    {
        using var db = await new TestDb().SeedAsync();
        await LinkAsync(db, LineUserId);

        var session = await Create(db, new LineIdentity(LineUserId, "家族")).ResolveAsync("token");

        Assert.Equal(LiffSessionStatus.Ready, session.Status);
        Assert.NotNull(session.View);
        Assert.Equal("テスト家族", session.View.HouseholdName);
        Assert.Equal("母", session.View.ResidentName);
        Assert.NotNull(session.View.Risk);
    }

    [Fact]
    public async Task The_Most_Recently_Seen_Link_Wins()
    {
        using var db = await new TestDb().SeedAsync();

        var otherHousehold = new Household { Name = "別の家族" };
        db.Context.Households.Add(otherHousehold);
        await db.Context.SaveChangesAsync();

        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = otherHousehold.Id,
            LineUserId = LineUserId,
            IsActive = true,
            LastSeenAt = DateTimeOffset.UtcNow.AddDays(-30)
        });
        await db.Context.SaveChangesAsync();
        await LinkAsync(db, LineUserId);

        var session = await Create(db, new LineIdentity(LineUserId, null)).ResolveAsync("token");

        Assert.Equal(LiffSessionStatus.Ready, session.Status);
        Assert.Equal("テスト家族", session.View!.HouseholdName);
    }

    [Fact]
    public async Task A_Household_Without_A_Resident_Still_Renders()
    {
        using var db = new TestDb();
        var household = new Household { Name = "登録途中の家族" };
        db.Context.Households.Add(household);
        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = household.Id,
            LineUserId = LineUserId,
            IsActive = true
        });
        await db.Context.SaveChangesAsync();

        var session = await Create(db, new LineIdentity(LineUserId, null)).ResolveAsync("token");

        // Mid-setup households must not throw inside a LINE WebView.
        Assert.Equal(LiffSessionStatus.Ready, session.Status);
        Assert.Equal("ご家族", session.View!.ResidentName);
    }

    [Theory]
    [InlineData(RiskLevel.Low, "okay")]
    [InlineData(RiskLevel.Medium, "concern")]
    [InlineData(RiskLevel.High, "emergency")]
    public void The_Greeting_Matches_The_Risk(RiskLevel level, string expected)
    {
        // These names must stay in the `reactions` map of mimamori-mascot-3d.js, or the
        // CG silently falls back to idling while the card says something is wrong.
        Assert.Equal(expected, LiffSessionService.MascotGreeting(level));
    }
}

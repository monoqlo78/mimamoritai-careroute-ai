using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>
/// Covers LineLinkCodeService's generate/redeem lifecycle: happy path, expiry,
/// single-use, wrong-code handling, system-wide attempt limiting, prior-code
/// invalidation, full-width digit normalization, and cross-household re-linking.
/// </summary>
public class LineLinkCodeServiceTests
{
    private const string FakeUserId = "Utestuser0000000000000000000000000";

    [Fact]
    public async Task GenerateCodeAsync_Returns_A_Six_Digit_Code_With_TenMinute_Expiry()
    {
        using var db = await new TestDb().SeedAsync();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = new LineLinkCodeService(db.Context, new FakeTimeProvider(now));

        var generated = await service.GenerateCodeAsync(db.HouseholdId);

        Assert.Equal(6, generated.Code.Length);
        Assert.All(generated.Code, c => Assert.True(char.IsAsciiDigit(c)));
        Assert.Equal(now + LineLinkCodeService.CodeLifetime, generated.ExpiresAtUtc);

        var stored = await db.Context.LineLinkCodes.SingleAsync();
        Assert.Equal(db.HouseholdId, stored.HouseholdId);
        Assert.NotEqual(generated.Code, stored.CodeHash);
        Assert.DoesNotContain(generated.Code, stored.CodeHash);
    }

    [Fact]
    public async Task RedeemCodeAsync_HappyPath_Creates_Active_LineRecipient_And_Marks_Code_Used()
    {
        using var db = await new TestDb().SeedAsync();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var service = new LineLinkCodeService(db.Context, clock);

        var generated = await service.GenerateCodeAsync(db.HouseholdId);
        clock.Advance(TimeSpan.FromMinutes(1));

        var result = await service.RedeemCodeAsync(generated.Code, FakeUserId, displayName: "母", CancellationToken.None);

        Assert.Equal(LineLinkCodeRedeemStatus.Success, result.Status);
        Assert.Equal(db.HouseholdId, result.HouseholdId);

        var recipient = await db.Context.LineRecipients.SingleAsync();
        Assert.Equal(db.HouseholdId, recipient.HouseholdId);
        Assert.Equal(FakeUserId, recipient.LineUserId);
        Assert.True(recipient.IsActive);
        Assert.Equal("母", recipient.DisplayName);

        var code = await db.Context.LineLinkCodes.SingleAsync();
        Assert.NotNull(code.UsedAtUtc);
        Assert.Equal(now.AddMinutes(1), code.UsedAtUtc);
    }

    [Fact]
    public async Task RedeemCodeAsync_SameCode_Twice_Fails_The_Second_Time_SingleUse()
    {
        using var db = await new TestDb().SeedAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new LineLinkCodeService(db.Context, clock);
        var generated = await service.GenerateCodeAsync(db.HouseholdId);

        var first = await service.RedeemCodeAsync(generated.Code, FakeUserId, null, CancellationToken.None);
        var second = await service.RedeemCodeAsync(generated.Code, "Utestuser0000000000000000000000001", null, CancellationToken.None);

        Assert.Equal(LineLinkCodeRedeemStatus.Success, first.Status);
        Assert.Equal(LineLinkCodeRedeemStatus.Failed, second.Status);
        Assert.Null(second.HouseholdId);

        // The second (failed) redemption must not have linked the other user id.
        Assert.Single(db.Context.LineRecipients);
    }

    [Fact]
    public async Task RedeemCodeAsync_ExpiredCode_Fails()
    {
        using var db = await new TestDb().SeedAsync();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var service = new LineLinkCodeService(db.Context, clock);
        var generated = await service.GenerateCodeAsync(db.HouseholdId);

        clock.Advance(LineLinkCodeService.CodeLifetime + TimeSpan.FromSeconds(1));

        var result = await service.RedeemCodeAsync(generated.Code, FakeUserId, null, CancellationToken.None);

        Assert.Equal(LineLinkCodeRedeemStatus.Failed, result.Status);
        Assert.Empty(db.Context.LineRecipients);
    }

    [Fact]
    public async Task RedeemCodeAsync_WrongCode_Fails_And_Never_Reveals_Why()
    {
        using var db = await new TestDb().SeedAsync();
        var service = new LineLinkCodeService(db.Context, new FakeTimeProvider(DateTimeOffset.UtcNow));
        await service.GenerateCodeAsync(db.HouseholdId);

        var result = await service.RedeemCodeAsync("000000", FakeUserId, null, CancellationToken.None);

        Assert.Equal(LineLinkCodeRedeemStatus.Failed, result.Status);
        Assert.Null(result.HouseholdId);
    }

    [Fact]
    public async Task RedeemCodeAsync_WrongCode_Increments_AttemptCount_On_Every_Active_Code()
    {
        using var db = await new TestDb().SeedAsync();
        var service = new LineLinkCodeService(db.Context, new FakeTimeProvider(DateTimeOffset.UtcNow));
        await service.GenerateCodeAsync(db.HouseholdId);

        await service.RedeemCodeAsync("000000", FakeUserId, null, CancellationToken.None);

        var code = await db.Context.LineLinkCodes.SingleAsync();
        Assert.Equal(1, code.AttemptCount);
    }

    [Fact]
    public async Task RedeemCodeAsync_ForcesExpiry_After_MaxAttempts_Reached()
    {
        using var db = await new TestDb().SeedAsync();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var service = new LineLinkCodeService(db.Context, clock);
        var generated = await service.GenerateCodeAsync(db.HouseholdId);

        // Exhaust the attempt limit with wrong guesses.
        for (var i = 0; i < LineLinkCodeService.MaxAttempts; i++)
        {
            await service.RedeemCodeAsync("000000", FakeUserId, null, CancellationToken.None);
        }

        var code = await db.Context.LineLinkCodes.SingleAsync();
        Assert.True(code.AttemptCount >= LineLinkCodeService.MaxAttempts);
        Assert.Equal(now, code.ExpiresAtUtc);

        // The originally-correct code must now also be rejected, since it was force-expired.
        var result = await service.RedeemCodeAsync(generated.Code, FakeUserId, null, CancellationToken.None);
        Assert.Equal(LineLinkCodeRedeemStatus.Failed, result.Status);
    }

    [Fact]
    public async Task GenerateCodeAsync_Invalidates_Prior_Unused_Code_For_The_Household()
    {
        using var db = await new TestDb().SeedAsync();
        var service = new LineLinkCodeService(db.Context, new FakeTimeProvider(DateTimeOffset.UtcNow));

        var first = await service.GenerateCodeAsync(db.HouseholdId);
        var second = await service.GenerateCodeAsync(db.HouseholdId);

        // Exactly one LineLinkCode row should remain for this household -- the new one.
        var codes = await db.Context.LineLinkCodes.Where(c => c.HouseholdId == db.HouseholdId).ToListAsync();
        Assert.Single(codes);

        var firstRedeem = await service.RedeemCodeAsync(first.Code, FakeUserId, null, CancellationToken.None);
        Assert.Equal(LineLinkCodeRedeemStatus.Failed, firstRedeem.Status);

        var secondRedeem = await service.RedeemCodeAsync(second.Code, FakeUserId, null, CancellationToken.None);
        Assert.Equal(LineLinkCodeRedeemStatus.Success, secondRedeem.Status);
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123456 ", "123456")]
    [InlineData("１２３４５６", "123456")]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12345a", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeCode_Handles_Whitespace_And_FullWidth_Digits(string? input, string? expected)
    {
        Assert.Equal(expected, LineLinkCodeService.NormalizeCode(input));
    }

    [Fact]
    public async Task RedeemCodeAsync_WithFullWidthDigits_Succeeds()
    {
        using var db = await new TestDb().SeedAsync();
        var service = new LineLinkCodeService(db.Context, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var generated = await service.GenerateCodeAsync(db.HouseholdId);

        // Build the full-width equivalent of the generated code.
        var fullWidthCode = new string(generated.Code.Select(c => (char)(c - '0' + '\uFF10')).ToArray());

        var result = await service.RedeemCodeAsync(fullWidthCode, FakeUserId, null, CancellationToken.None);

        Assert.Equal(LineLinkCodeRedeemStatus.Success, result.Status);
    }

    [Fact]
    public async Task RedeemCodeAsync_ReLinking_Deactivates_The_Recipient_Row_In_The_Other_Household()
    {
        using var db = await new TestDb().SeedAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new LineLinkCodeService(db.Context, clock);

        // Household A (the seeded one) already has this LINE user actively linked.
        var householdA = db.HouseholdId;
        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = householdA,
            LineUserId = FakeUserId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });

        var householdB = new Household { Name = "別の家族" };
        db.Context.Households.Add(householdB);
        await db.Context.SaveChangesAsync();

        var generated = await service.GenerateCodeAsync(householdB.Id);
        var result = await service.RedeemCodeAsync(generated.Code, FakeUserId, null, CancellationToken.None);

        Assert.Equal(LineLinkCodeRedeemStatus.Success, result.Status);
        Assert.Equal(householdB.Id, result.HouseholdId);

        var recipientA = await db.Context.LineRecipients.SingleAsync(r => r.HouseholdId == householdA);
        Assert.False(recipientA.IsActive);

        var recipientB = await db.Context.LineRecipients.SingleAsync(r => r.HouseholdId == householdB.Id);
        Assert.True(recipientB.IsActive);
    }

    [Fact]
    public async Task IsOwnerAsync_ReturnsTrue_OnlyForOwnerRole()
    {
        using var db = await new TestDb().SeedAsync();
        var owner = new AppUser { IdentityProvider = "dev", ExternalSubject = "owner-sub", DisplayName = "オーナー" };
        var member = new AppUser { IdentityProvider = "dev", ExternalSubject = "member-sub", DisplayName = "メンバー" };
        db.Context.AppUsers.AddRange(owner, member);
        db.Context.HouseholdMembers.AddRange(
            new HouseholdMember { HouseholdId = db.HouseholdId, AppUserId = owner.Id, Role = HouseholdMemberRole.Owner },
            new HouseholdMember { HouseholdId = db.HouseholdId, AppUserId = member.Id, Role = HouseholdMemberRole.Member });
        await db.Context.SaveChangesAsync();

        var service = new LineLinkCodeService(db.Context, new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.True(await service.IsOwnerAsync(db.HouseholdId, owner.Id));
        Assert.False(await service.IsOwnerAsync(db.HouseholdId, member.Id));
        Assert.False(await service.IsOwnerAsync(db.HouseholdId, Guid.NewGuid()));
    }
}

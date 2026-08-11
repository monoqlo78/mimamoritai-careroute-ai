using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Tests;

/// <summary>
/// Records the raw Authorization header sent on every outgoing request instead of
/// hitting the network, so tests can assert exactly which household's decrypted
/// token was used for a given call without needing reflection into SwitchBotClient's
/// private state.
/// </summary>
public sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    public List<string?> AuthorizationHeaders { get; } = [];
    public string ResponseJson { get; set; } = """{"statusCode":100,"body":{"deviceList":[]}}""";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AuthorizationHeaders.Add(
            request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null);

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(ResponseJson, System.Text.Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("https://api.switch-bot.com")
    };
}

public class HouseholdSwitchBotClientFactoryTests
{
    private static HouseholdSwitchBotClientFactory CreateFactory(
        TestDb db, CapturingHttpMessageHandler handler, SwitchBotOptions? globalOptions = null) =>
        new(
            db.Context,
            new FakeCredentialProtector(),
            new FakeHttpClientFactory(handler),
            Options.Create(globalOptions ?? new SwitchBotOptions()),
            NullLoggerFactory.Instance,
            NullLogger<HouseholdSwitchBotClientFactory>.Instance);

    [Fact]
    public async Task GetClientAsync_UsesThePerHouseholdConnection_WhenARowExists()
    {
        using var db = await new TestDb().SeedAsync();
        var protector = new FakeCredentialProtector();
        db.Context.SwitchBotConnections.Add(new SwitchBotConnection
        {
            HouseholdId = db.HouseholdId,
            EncryptedToken = protector.Protect("household-token"),
            EncryptedSecret = protector.Protect("household-secret"),
            Status = SwitchBotConnectionStatus.Connected
        });
        await db.Context.SaveChangesAsync();

        var handler = new CapturingHttpMessageHandler();
        var factory = CreateFactory(db, handler);

        var client = await factory.GetClientAsync(db.HouseholdId);
        Assert.True(client.IsConfigured);
        await client.GetDeviceListRawAsync();

        Assert.Single(handler.AuthorizationHeaders);
        Assert.Equal("household-token", handler.AuthorizationHeaders[0]);
    }

    [Fact]
    public async Task GetClientAsync_FallsBackToGlobalOptions_WhenNoRow_AndFallbackAllowed()
    {
        using var db = await new TestDb().SeedAsync();
        var handler = new CapturingHttpMessageHandler();
        var globalOptions = new SwitchBotOptions
        {
            Enabled = true,
            Token = "global-token",
            Secret = "global-secret",
            AllowGlobalFallback = true
        };
        var factory = CreateFactory(db, handler, globalOptions);

        var client = await factory.GetClientAsync(db.HouseholdId);
        Assert.True(client.IsConfigured);
        await client.GetDeviceListRawAsync();

        Assert.Equal("global-token", handler.AuthorizationHeaders[0]);
    }

    [Fact]
    public async Task GetClientAsync_ReturnsNotConfigured_WhenNoRow_AndFallbackDisabled()
    {
        using var db = await new TestDb().SeedAsync();
        var handler = new CapturingHttpMessageHandler();
        var globalOptions = new SwitchBotOptions
        {
            Enabled = true,
            Token = "global-token",
            Secret = "global-secret",
            AllowGlobalFallback = false // the production-safe default
        };
        var factory = CreateFactory(db, handler, globalOptions);

        var client = await factory.GetClientAsync(db.HouseholdId);

        Assert.False(client.IsConfigured);
        Assert.Empty(handler.AuthorizationHeaders); // never even attempted a call
    }

    [Fact]
    public async Task GetClientAsync_ReturnsNotConfigured_WhenNoRow_AndNoGlobalOptionsConfiguredAtAll()
    {
        using var db = await new TestDb().SeedAsync();
        var handler = new CapturingHttpMessageHandler();
        var factory = CreateFactory(db, handler); // default SwitchBotOptions: Enabled=false

        var client = await factory.GetClientAsync(db.HouseholdId);

        Assert.False(client.IsConfigured);
    }

    [Fact]
    public async Task GetClientAsync_ReturnsNotConfigured_WhenTheStoredBlobCannotBeDecrypted()
    {
        using var db = await new TestDb().SeedAsync();
        db.Context.SwitchBotConnections.Add(new SwitchBotConnection
        {
            HouseholdId = db.HouseholdId,
            EncryptedToken = "not-a-real-protected-blob",
            EncryptedSecret = "not-a-real-protected-blob-either",
            Status = SwitchBotConnectionStatus.Connected
        });
        await db.Context.SaveChangesAsync();

        var handler = new CapturingHttpMessageHandler();
        var factory = CreateFactory(db, handler);

        // Must degrade to "not configured" rather than throwing out of a poll loop.
        var client = await factory.GetClientAsync(db.HouseholdId);

        Assert.False(client.IsConfigured);
    }

    [Fact]
    public async Task GetClientAsync_NeverLeaksOneHouseholdsCredentialsIntoAnothers()
    {
        using var dbA = await new TestDb().SeedAsync();
        using var dbB = await new TestDb().SeedAsync();
        var protector = new FakeCredentialProtector();

        dbA.Context.SwitchBotConnections.Add(new SwitchBotConnection
        {
            HouseholdId = dbA.HouseholdId,
            EncryptedToken = protector.Protect("token-A"),
            EncryptedSecret = protector.Protect("secret-A"),
            Status = SwitchBotConnectionStatus.Connected
        });
        await dbA.Context.SaveChangesAsync();

        dbB.Context.SwitchBotConnections.Add(new SwitchBotConnection
        {
            HouseholdId = dbB.HouseholdId,
            EncryptedToken = protector.Protect("token-B"),
            EncryptedSecret = protector.Protect("secret-B"),
            Status = SwitchBotConnectionStatus.Connected
        });
        await dbB.Context.SaveChangesAsync();

        var handlerA = new CapturingHttpMessageHandler();
        var handlerB = new CapturingHttpMessageHandler();
        var factoryA = CreateFactory(dbA, handlerA);
        var factoryB = CreateFactory(dbB, handlerB);

        var clientA = await factoryA.GetClientAsync(dbA.HouseholdId);
        var clientB = await factoryB.GetClientAsync(dbB.HouseholdId);

        await clientA.GetDeviceListRawAsync();
        await clientB.GetDeviceListRawAsync();

        Assert.Equal("token-A", handlerA.AuthorizationHeaders[0]);
        Assert.Equal("token-B", handlerB.AuthorizationHeaders[0]);
        Assert.DoesNotContain("token-B", handlerA.AuthorizationHeaders);
        Assert.DoesNotContain("token-A", handlerB.AuthorizationHeaders);
    }

    [Fact]
    public void CreateAdHocClient_BuildsAClientBoundToTheGivenCredentials_WithoutTouchingTheDatabase()
    {
        using var db = new TestDb();
        var handler = new CapturingHttpMessageHandler();
        var factory = CreateFactory(db, handler);

        var client = factory.CreateAdHocClient("ad-hoc-token", "ad-hoc-secret");

        Assert.True(client.IsConfigured);
    }
}

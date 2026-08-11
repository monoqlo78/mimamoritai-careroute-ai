using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>
/// Deterministic, reversible test-only stand-in for the real Data Protection-backed
/// protector. NEVER model this pattern (a fixed prefix + reversible transform) as a
/// real ICredentialProtector implementation -- production must only ever use
/// DataProtectionCredentialProtector. This fake exists purely so tests can assert a
/// round trip and that the stored blob is never equal to the plaintext.
/// </summary>
public sealed class FakeCredentialProtector : ICredentialProtector
{
    private const string Prefix = "protected:";

    public string Protect(string plaintext) => Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new System.Security.Cryptography.CryptographicException("Not a recognized protected value.");
        }

        var payload = protectedValue[Prefix.Length..];
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
    }
}

/// <summary>
/// Fake IHouseholdSwitchBotClientFactory whose CreateAdHocClient always returns a
/// caller-supplied FakeSwitchBotClient, so ValidateAndSaveAsync tests can control
/// whether "validation" succeeds without any network access.
/// </summary>
public sealed class FakeHouseholdSwitchBotClientFactory : IHouseholdSwitchBotClientFactory
{
    public ISwitchBotClient AdHocClient { get; set; } = new FakeSwitchBotClient();
    public List<(string Token, string Secret)> AdHocCalls { get; } = [];

    public Task<ISwitchBotClient> GetClientAsync(Guid householdId, CancellationToken ct = default) =>
        throw new NotSupportedException("Not used by SwitchBotConnectionService tests.");

    public ISwitchBotClient CreateAdHocClient(string token, string secret)
    {
        AdHocCalls.Add((token, secret));
        return AdHocClient;
    }

    public Task<IDeviceProvider> GetDeviceProviderAsync(Guid householdId, CancellationToken ct = default) =>
        throw new NotSupportedException("Not used by SwitchBotConnectionService tests.");
}

public class SwitchBotConnectionServiceTests
{
    private static (SwitchBotConnectionService Service, FakeHouseholdSwitchBotClientFactory Factory, FakeTimeProvider Clock) CreateService(
        TestDb db, DateTimeOffset? now = null)
    {
        var clock = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var factory = new FakeHouseholdSwitchBotClientFactory();
        var service = new SwitchBotConnectionService(db.Context, new FakeCredentialProtector(), factory, clock);
        return (service, factory, clock);
    }

    [Fact]
    public async Task GetStatusAsync_Returns_NotConfigured_When_No_Row_Exists()
    {
        using var db = await new TestDb().SeedAsync();
        var (service, _, _) = CreateService(db);

        var status = await service.GetStatusAsync(db.HouseholdId);

        Assert.Equal(SwitchBotConnectionStatus.NotConfigured, status.Status);
        Assert.Null(status.LastValidatedAtUtc);
        Assert.Null(status.LastSyncAtUtc);
        Assert.Null(status.LastErrorMessage);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_HappyPath_Encrypts_And_Persists_A_New_Row()
    {
        using var db = await new TestDb().SeedAsync();
        var (service, factory, clock) = CreateService(db);
        factory.AdHocClient = new FakeSwitchBotClient { DeviceListResponse = """{"statusCode":100,"body":{"deviceList":[]}}""" };

        var outcome = await service.ValidateAndSaveAsync(db.HouseholdId, "raw-token-value", "raw-secret-value");

        Assert.Equal(SwitchBotConnectionSaveOutcome.Saved, outcome);

        var row = await db.Context.SwitchBotConnections.SingleAsync(c => c.HouseholdId == db.HouseholdId);
        Assert.Equal(SwitchBotConnectionStatus.Connected, row.Status);
        Assert.Equal(clock.GetUtcNow(), row.LastValidatedAtUtc);
        Assert.Null(row.LastErrorMessage);

        // The encrypted columns must never equal, or contain, the plaintext secret.
        Assert.NotEqual("raw-token-value", row.EncryptedToken);
        Assert.NotEqual("raw-secret-value", row.EncryptedSecret);
        Assert.DoesNotContain("raw-token-value", row.EncryptedToken, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret-value", row.EncryptedSecret, StringComparison.Ordinal);

        var status = await service.GetStatusAsync(db.HouseholdId);
        Assert.Equal(SwitchBotConnectionStatus.Connected, status.Status);
    }

    [Fact]
    public async Task ValidateAndSaveAsync_RoundTrips_Through_The_Protector()
    {
        using var db = await new TestDb().SeedAsync();
        var (service, _, _) = CreateService(db);
        var protector = new FakeCredentialProtector();

        await service.ValidateAndSaveAsync(db.HouseholdId, "raw-token-value", "raw-secret-value");

        var row = await db.Context.SwitchBotConnections.SingleAsync(c => c.HouseholdId == db.HouseholdId);
        Assert.Equal("raw-token-value", protector.Unprotect(row.EncryptedToken));
        Assert.Equal("raw-secret-value", protector.Unprotect(row.EncryptedSecret));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenClientReportsNotConfigured_Fails_Without_Saving_Anything()
    {
        using var db = await new TestDb().SeedAsync();
        var (service, factory, _) = CreateService(db);
        factory.AdHocClient = new FakeSwitchBotClient { IsConfigured = false };

        var outcome = await service.ValidateAndSaveAsync(db.HouseholdId, "", "");

        Assert.Equal(SwitchBotConnectionSaveOutcome.ValidationFailed, outcome);
        Assert.Null(await db.Context.SwitchBotConnections.FirstOrDefaultAsync(c => c.HouseholdId == db.HouseholdId));
    }

    [Fact]
    public async Task ValidateAndSaveAsync_WhenApiCallThrows_Fails_And_Records_A_Safe_Error_On_An_Existing_Row()
    {
        using var db = await new TestDb().SeedAsync();
        var (service, factory, _) = CreateService(db);

        // First save a good connection so a row exists to record the failure against.
        await service.ValidateAndSaveAsync(db.HouseholdId, "good-token", "good-secret");

        factory.AdHocClient = new ThrowingSwitchBotClient();
        var outcome = await service.ValidateAndSaveAsync(db.HouseholdId, "bad-token", "bad-secret");

        Assert.Equal(SwitchBotConnectionSaveOutcome.ValidationFailed, outcome);

        var row = await db.Context.SwitchBotConnections.SingleAsync(c => c.HouseholdId == db.HouseholdId);
        Assert.Equal(SwitchBotConnectionStatus.Error, row.Status);
        Assert.NotNull(row.LastErrorMessage);
        Assert.DoesNotContain("bad-token", row.LastErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("bad-secret", row.LastErrorMessage, StringComparison.Ordinal);

        // The previously-saved good credentials must remain untouched by the failed attempt.
        var protector = new FakeCredentialProtector();
        Assert.Equal("good-token", protector.Unprotect(row.EncryptedToken));
    }

    [Fact]
    public async Task DisconnectAsync_Removes_The_Row_Entirely()
    {
        using var db = await new TestDb().SeedAsync();
        var (service, _, _) = CreateService(db);
        await service.ValidateAndSaveAsync(db.HouseholdId, "token", "secret");

        await service.DisconnectAsync(db.HouseholdId);

        Assert.Null(await db.Context.SwitchBotConnections.FirstOrDefaultAsync(c => c.HouseholdId == db.HouseholdId));
        var status = await service.GetStatusAsync(db.HouseholdId);
        Assert.Equal(SwitchBotConnectionStatus.NotConfigured, status.Status);
    }

    [Fact]
    public async Task MarkSyncedAsync_Stamps_LastSyncAtUtc_On_An_Existing_Row()
    {
        using var db = await new TestDb().SeedAsync();
        var (service, _, clock) = CreateService(db);
        await service.ValidateAndSaveAsync(db.HouseholdId, "token", "secret");

        clock.Advance(TimeSpan.FromMinutes(10));
        await service.MarkSyncedAsync(db.HouseholdId);

        var row = await db.Context.SwitchBotConnections.SingleAsync(c => c.HouseholdId == db.HouseholdId);
        Assert.Equal(clock.GetUtcNow(), row.LastSyncAtUtc);
    }

    [Fact]
    public async Task MarkSyncedAsync_Does_Nothing_When_No_Row_Exists()
    {
        using var db = await new TestDb().SeedAsync();
        var (service, _, _) = CreateService(db);

        await service.MarkSyncedAsync(db.HouseholdId); // should not throw

        Assert.Null(await db.Context.SwitchBotConnections.FirstOrDefaultAsync(c => c.HouseholdId == db.HouseholdId));
    }

    [Fact]
    public async Task IsOwnerAsync_ReturnsTrue_Only_For_The_Household_Owner()
    {
        using var db = await new TestDb().SeedAsync();
        var (service, _, _) = CreateService(db);

        var owner = new AppUser { DisplayName = "オーナー", IdentityProvider = "dev", ExternalSubject = "owner-sub" };
        var member = new AppUser { DisplayName = "メンバー", IdentityProvider = "dev", ExternalSubject = "member-sub" };
        db.Context.AppUsers.AddRange(owner, member);
        db.Context.HouseholdMembers.AddRange(
            new HouseholdMember { HouseholdId = db.HouseholdId, AppUserId = owner.Id, Role = HouseholdMemberRole.Owner },
            new HouseholdMember { HouseholdId = db.HouseholdId, AppUserId = member.Id, Role = HouseholdMemberRole.Member });
        await db.Context.SaveChangesAsync();

        Assert.True(await service.IsOwnerAsync(db.HouseholdId, owner.Id));
        Assert.False(await service.IsOwnerAsync(db.HouseholdId, member.Id));
        Assert.False(await service.IsOwnerAsync(db.HouseholdId, Guid.NewGuid()));
    }

    private sealed class ThrowingSwitchBotClient : ISwitchBotClient
    {
        public bool IsConfigured => true;

        public Task<string> GetDeviceListRawAsync(CancellationToken ct = default) =>
            throw new HttpRequestException("SwitchBot API returned 401.");

        public Task<string> GetDeviceStatusRawAsync(string deviceId, CancellationToken ct = default) =>
            throw new HttpRequestException("SwitchBot API returned 401.");

        public Task<string> SendCommandRawAsync(string deviceId, string command, string parameter, string commandType, CancellationToken ct = default) =>
            throw new HttpRequestException("SwitchBot API returned 401.");
    }
}

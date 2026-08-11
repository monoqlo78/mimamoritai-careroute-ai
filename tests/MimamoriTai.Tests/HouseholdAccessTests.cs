using Microsoft.Extensions.Logging.Abstractions;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Tests;

/// <summary>Test double for <see cref="ICurrentUserAccessor"/> so each test can control who is "signed in".</summary>
public sealed class FakeCurrentUserAccessor(CurrentUser? current) : ICurrentUserAccessor
{
    public CurrentUser? Current { get; } = current;

    public static CurrentUser User(Guid id, string displayName, string idp = "dev", string subject = "") =>
        new(id, displayName, idp, subject == "" ? id.ToString() : subject, IsAuthenticated: true);
}

public class HouseholdAccessTests
{
    private static async Task<Guid> CreateSampleHouseholdAsync(TestDb testDb)
    {
        var household = new Household
        {
            Name = "サンプル家族",
            DataSourceMode = DataSourceMode.Sample,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        testDb.Context.Households.Add(household);
        await testDb.Context.SaveChangesAsync();
        return household.Id;
    }

    private static HouseholdAccessService Service(TestDb testDb, CurrentUser? user) =>
        new(testDb.Context, new FakeCurrentUserAccessor(user), TimeProvider.System);

    [Fact]
    public async Task ProductionHousehold_IsNotAccessibleToAnotherUser()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();

        var owner = FakeCurrentUserAccessor.User(Guid.NewGuid(), "所有者");
        var stranger = FakeCurrentUserAccessor.User(Guid.NewGuid(), "他人");

        var ownerService = Service(testDb, owner);
        var householdId = await ownerService.EnsureProductionHouseholdAsync("曽我部家");

        var strangerService = Service(testDb, stranger);
        Assert.False(await strangerService.CanAccessAsync(householdId));

        var strangerHouseholds = await strangerService.ListAccessibleAsync();
        Assert.DoesNotContain(strangerHouseholds, h => h.Id == householdId);

        // The owner can still access their own household.
        Assert.True(await ownerService.CanAccessAsync(householdId));
    }

    [Fact]
    public async Task SampleHousehold_IsAccessibleToEveryUser()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();
        var sampleId = await CreateSampleHouseholdAsync(testDb);

        var someUser = FakeCurrentUserAccessor.User(Guid.NewGuid(), "誰か");
        var anonymous = Service(testDb, null);
        var signedIn = Service(testDb, someUser);

        Assert.True(await anonymous.CanAccessAsync(sampleId));
        Assert.True(await signedIn.CanAccessAsync(sampleId));

        var accessible = await signedIn.ListAccessibleAsync();
        Assert.Contains(accessible, h => h.Id == sampleId && h.DataSourceMode == DataSourceMode.Sample);
        Assert.All(accessible.Where(h => h.DataSourceMode == DataSourceMode.Sample), h => Assert.Equal("デモデータ", h.AnalyticsLabel));
    }

    [Fact]
    public async Task ListAccessibleAsync_LabelsProductionHouseholdByActiveLineAccount()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();

        var owner = FakeCurrentUserAccessor.User(Guid.NewGuid(), "所有者");
        var service = Service(testDb, owner);
        var householdId = await service.EnsureProductionHouseholdAsync("わが家");
        testDb.Context.LineRecipients.AddRange(
            new LineRecipient
            {
                HouseholdId = householdId,
                LineUserId = "Uinactive",
                DisplayName = "古い利用者",
                IsActive = false,
                LastSeenAt = DateTimeOffset.UtcNow
            },
            new LineRecipient
            {
                HouseholdId = householdId,
                LineUserId = "Uactive",
                DisplayName = "まさあき",
                IsActive = true,
                LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            });
        await testDb.Context.SaveChangesAsync();

        var accessible = await service.ListAccessibleAsync();

        var production = Assert.Single(accessible.Where(h => h.Id == householdId));
        Assert.Equal("まさあき（LINE）", production.AnalyticsLabel);
    }

    [Fact]
    public async Task EnsureProductionHouseholdAsync_IsIdempotent()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();

        var user = FakeCurrentUserAccessor.User(Guid.NewGuid(), "所有者");
        var service = Service(testDb, user);

        var first = await service.EnsureProductionHouseholdAsync("わが家");
        var second = await service.EnsureProductionHouseholdAsync("わが家（再実行）");

        Assert.Equal(first, second);

        var householdCount = testDb.Context.Households.Count(h => h.DataSourceMode == DataSourceMode.Production);
        Assert.Equal(1, householdCount);

        var memberCount = testDb.Context.HouseholdMembers.Count(m => m.HouseholdId == first && m.AppUserId == user.AppUserId);
        Assert.Equal(1, memberCount);
    }

    [Fact]
    public async Task ResolveDefaultAsync_PrefersOwnProductionHousehold_ThenFallsBackToSample()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();
        var sampleId = testDb.HouseholdId; // TestDb.SeedAsync's household defaults to DataSourceMode.Sample.

        var user = FakeCurrentUserAccessor.User(Guid.NewGuid(), "所有者");
        var anonymousService = Service(testDb, null);
        var userService = Service(testDb, user);

        // No production household yet: falls back to the sample household for everyone.
        Assert.Equal(sampleId, await anonymousService.ResolveDefaultAsync());
        Assert.Equal(sampleId, await userService.ResolveDefaultAsync());

        var productionId = await userService.EnsureProductionHouseholdAsync("わが家");

        // Once the user owns a production household, it takes priority for them...
        Assert.Equal(productionId, await userService.ResolveDefaultAsync());
        // ...but anonymous/other callers still get the sample household.
        Assert.Equal(sampleId, await anonymousService.ResolveDefaultAsync());
    }

    [Fact]
    public async Task EnsureUserAsync_CreatesOnce_ThenUpdatesOnSecondCall()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();

        var appUserId = Guid.NewGuid();
        var user = new CurrentUser(appUserId, "初回の名前", "entra-external", "external-sub-1", IsAuthenticated: true);
        var service = Service(testDb, user);

        var created = await service.EnsureUserAsync(user);
        Assert.Equal(appUserId, created.Id);
        Assert.Equal("初回の名前", created.DisplayName);

        var updatedUser = user with { DisplayName = "更新後の名前" };
        var updated = await service.EnsureUserAsync(updatedUser);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("更新後の名前", updated.DisplayName);

        var count = testDb.Context.AppUsers.Count(u => u.IdentityProvider == "entra-external" && u.ExternalSubject == "external-sub-1");
        Assert.Equal(1, count);
    }

    [Fact]
    public void DeviceProviderFactory_Sample_ReturnsMockProvider()
    {
        var mock = new MockDeviceProvider();
        var switchBot = new SwitchBotDeviceProvider(
            new FakeSwitchBotClient { IsConfigured = false },
            NullLogger<SwitchBotDeviceProvider>.Instance);
        var factory = new DeviceProviderFactory(mock, switchBot);

        var resolved = factory.Get(DataSourceMode.Sample);

        Assert.Same(mock, resolved);
        Assert.Equal(DeviceProviderKind.Mock, resolved.Kind);
    }

    [Fact]
    public void DeviceProviderFactory_Production_FallsBackToMock_WhenSwitchBotUnconfigured()
    {
        var mock = new MockDeviceProvider();
        var switchBot = new SwitchBotDeviceProvider(
            new FakeSwitchBotClient { IsConfigured = false },
            NullLogger<SwitchBotDeviceProvider>.Instance);
        var factory = new DeviceProviderFactory(mock, switchBot);

        var resolved = factory.Get(DataSourceMode.Production);

        Assert.Same(mock, resolved);
        Assert.Equal(DeviceProviderKind.Mock, resolved.Kind);
    }

    [Fact]
    public void DeviceProviderFactory_Production_UsesSwitchBot_WhenConfigured()
    {
        var mock = new MockDeviceProvider();
        var switchBot = new SwitchBotDeviceProvider(
            new FakeSwitchBotClient { IsConfigured = true },
            NullLogger<SwitchBotDeviceProvider>.Instance);
        var factory = new DeviceProviderFactory(mock, switchBot);

        var resolved = factory.Get(DataSourceMode.Production);

        Assert.Same(switchBot, resolved);
        Assert.Equal(DeviceProviderKind.SwitchBot, resolved.Kind);
    }

    /// <summary>
    /// A household that connected its own SwitchBot account must reach real hardware even
    /// when the deployment itself has no global SwitchBot:Token -- which was exactly the
    /// production configuration that silently routed every command to the mock provider.
    /// </summary>
    [Fact]
    public async Task DataSourceAwareProvider_Production_PrefersHouseholdCredentials()
    {
        var mock = new MockDeviceProvider();
        var unconfiguredGlobal = new SwitchBotDeviceProvider(
            new FakeSwitchBotClient { IsConfigured = false },
            NullLogger<SwitchBotDeviceProvider>.Instance);
        var householdProvider = new SwitchBotDeviceProvider(
            new FakeSwitchBotClient { IsConfigured = true },
            NullLogger<SwitchBotDeviceProvider>.Instance);
        var householdId = Guid.NewGuid();
        var clients = new FakeHouseholdSwitchBotClientFactory(householdProvider);

        var provider = new DataSourceAwareDeviceProvider(
            new DeviceProviderFactory(mock, unconfiguredGlobal),
            new DataSourceContext { Mode = DataSourceMode.Production, HouseholdId = householdId },
            clients);

        await provider.TurnOnAsync("8CFD49F79C92");

        Assert.Equal(householdId, Assert.Single(clients.Requested));
    }

    [Fact]
    public async Task DataSourceAwareProvider_Sample_NeverTouchesHouseholdCredentials()
    {
        var mock = new MockDeviceProvider();
        var householdProvider = new SwitchBotDeviceProvider(
            new FakeSwitchBotClient { IsConfigured = true },
            NullLogger<SwitchBotDeviceProvider>.Instance);
        var clients = new FakeHouseholdSwitchBotClientFactory(householdProvider);

        var provider = new DataSourceAwareDeviceProvider(
            new DeviceProviderFactory(mock, householdProvider),
            new DataSourceContext { Mode = DataSourceMode.Sample, HouseholdId = Guid.NewGuid() },
            clients);

        await provider.GetDevicesAsync();

        Assert.Empty(clients.Requested);
    }
}

file sealed class FakeHouseholdSwitchBotClientFactory(IDeviceProvider provider) : IHouseholdSwitchBotClientFactory
{
    public List<Guid> Requested { get; } = [];

    public Task<ISwitchBotClient> GetClientAsync(Guid householdId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public ISwitchBotClient CreateAdHocClient(string token, string secret) =>
        throw new NotSupportedException();

    public Task<IDeviceProvider> GetDeviceProviderAsync(Guid householdId, CancellationToken ct = default)
    {
        Requested.Add(householdId);
        return Task.FromResult(provider);
    }
}

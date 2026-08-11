using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Auth;
using MimamoriTai.Web.Services;

namespace MimamoriTai.Tests;

public class AdminConsoleTests
{
    private static AuthOptions ConfiguredAuth() => new()
    {
        Enabled = true,
        Authority = "https://example.ciamlogin.com/tenant/v2.0",
        ClientId = "client",
        ClientSecret = "secret"
    };

    private static AdminAccessService Access(
        CurrentUser? user, AdminOptions admin, AuthOptions auth) =>
        new(new FakeCurrentUserAccessor(user), Options.Create(admin), Options.Create(auth));

    private static AdminConsoleService Console(TestDb testDb, AdminAccessService access) =>
        new(testDb.Context, access, TimeProvider.System);

    [Fact]
    public void ListedSubject_IsAdmin_WhenAuthIsConfigured()
    {
        var user = FakeCurrentUserAccessor.User(Guid.NewGuid(), "運用者", idp: "line", subject: "U123");
        var access = Access(user, new AdminOptions { Subjects = { "line:U123" } }, ConfiguredAuth());

        Assert.True(access.IsAdmin);
        Assert.False(access.IsDemoModeGrant);
    }

    [Fact]
    public void UnlistedSubject_IsNotAdmin_WhenAuthIsConfigured()
    {
        var user = FakeCurrentUserAccessor.User(Guid.NewGuid(), "一般", idp: "line", subject: "U999");
        var access = Access(user, new AdminOptions { Subjects = { "line:U123" } }, ConfiguredAuth());

        Assert.False(access.IsAdmin);
    }

    [Fact]
    public void DemoGrant_DoesNotApply_WhenAuthIsConfigured()
    {
        var user = FakeCurrentUserAccessor.User(Guid.NewGuid(), "一般");
        var access = Access(user, new AdminOptions { AllowDemoUserWhenAuthDisabled = true }, ConfiguredAuth());

        Assert.False(access.IsAdmin);
        Assert.False(access.IsDemoModeGrant);
    }

    [Fact]
    public void DemoGrant_Applies_WhenAuthIsNotConfigured()
    {
        var user = FakeCurrentUserAccessor.User(Guid.NewGuid(), "デモ");
        var access = Access(user, new AdminOptions(), new AuthOptions());

        Assert.True(access.IsAdmin);
        Assert.True(access.IsDemoModeGrant);
    }

    [Fact]
    public void DemoGrant_CanBeDisabled()
    {
        var user = FakeCurrentUserAccessor.User(Guid.NewGuid(), "デモ");
        var access = Access(user, new AdminOptions { AllowDemoUserWhenAuthDisabled = false }, new AuthOptions());

        Assert.False(access.IsAdmin);
    }

    [Fact]
    public void AnonymousCaller_IsNeverAdmin()
    {
        var access = Access(null, new AdminOptions(), new AuthOptions());

        Assert.False(access.IsAdmin);
    }

    [Fact]
    public async Task LoadAsync_ReturnsNull_ForNonAdmin()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();

        var user = FakeCurrentUserAccessor.User(Guid.NewGuid(), "一般");
        var console = Console(testDb, Access(user, new AdminOptions(), ConfiguredAuth()));

        Assert.Null(await console.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_AggregatesEveryHousehold_IncludingOnesTheAdminIsNotAMemberOf()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync(TestDb.Light());

        // A second household the admin has no HouseholdMember row for.
        var other = new Household
        {
            Name = "他人の家",
            DataSourceMode = DataSourceMode.Production,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        testDb.Context.Households.Add(other);
        testDb.Context.WatchAlerts.Add(new WatchAlert
        {
            HouseholdId = other.Id,
            PersonId = Guid.NewGuid(),
            RiskLevel = RiskLevel.High,
            Score = 80,
            Reason = "長時間の無反応",
            Message = "確認してください",
            SentAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
            Success = false,
            Error = "LINE push failed"
        });
        await testDb.Context.SaveChangesAsync();

        var admin = FakeCurrentUserAccessor.User(Guid.NewGuid(), "運用者", idp: "line", subject: "U123");
        var console = Console(testDb, Access(admin, new AdminOptions { Subjects = { "line:U123" } }, ConfiguredAuth()));

        var model = await console.LoadAsync();

        Assert.NotNull(model);
        Assert.Equal(2, model.HouseholdCount);
        Assert.Equal(1, model.DeviceCount);
        Assert.Equal(1, model.AlertsInWindow);
        Assert.Equal(1, model.FailedAlertsInWindow);

        var otherRow = Assert.Single(model.Households, h => h.Id == other.Id);
        Assert.Equal(1, otherRow.FailedAlertsInWindow);
        Assert.True(AdminConsoleService.NeedsAttention(otherRow));

        var alert = Assert.Single(model.RecentAlerts);
        Assert.Equal("他人の家", alert.HouseholdName);
        Assert.False(alert.Success);
    }

    [Fact]
    public async Task LoadAsync_ExcludesAlertsOutsideTheWindow()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();

        testDb.Context.WatchAlerts.Add(new WatchAlert
        {
            HouseholdId = testDb.HouseholdId,
            PersonId = testDb.ResidentId,
            RiskLevel = RiskLevel.Medium,
            Score = 40,
            Reason = "古い通知",
            Message = "old",
            SentAtUtc = DateTimeOffset.UtcNow.AddDays(-30),
            Success = true
        });
        await testDb.Context.SaveChangesAsync();

        var admin = FakeCurrentUserAccessor.User(Guid.NewGuid(), "運用者");
        var console = Console(testDb, Access(admin, new AdminOptions(), new AuthOptions()));

        var model = await console.LoadAsync(windowDays: 7);

        Assert.NotNull(model);
        Assert.Equal(0, model.AlertsInWindow);
        Assert.Empty(model.RecentAlerts);
    }

    [Fact]
    public async Task ProductionHouseholdWithNoActiveLineRecipient_NeedsAttention()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();

        var production = new Household
        {
            Name = "本番世帯",
            DataSourceMode = DataSourceMode.Production,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var sample = new Household
        {
            Name = "デモ世帯",
            DataSourceMode = DataSourceMode.Sample,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        testDb.Context.Households.AddRange(production, sample);
        await testDb.Context.SaveChangesAsync();

        var admin = FakeCurrentUserAccessor.User(Guid.NewGuid(), "運用者");
        var console = Console(testDb, Access(admin, new AdminOptions(), new AuthOptions()));

        var model = await console.LoadAsync();
        Assert.NotNull(model);

        Assert.True(AdminConsoleService.NeedsAttention(
            Assert.Single(model.Households, h => h.Id == production.Id)));
        Assert.False(AdminConsoleService.NeedsAttention(
            Assert.Single(model.Households, h => h.Id == sample.Id)));
    }

    [Fact]
    public async Task LoadAsync_RollsUpAiUsageByModel()
    {
        using var testDb = new TestDb();
        await testDb.SeedAsync();

        var now = DateTimeOffset.UtcNow;
        testDb.Context.AiRequestLogs.AddRange(
            new AiRequestLog
            {
                HouseholdId = testDb.HouseholdId,
                Purpose = "summary",
                Router = "orcarouter",
                ResolvedModel = "gpt-x",
                DurationMs = 100,
                Success = true,
                CreatedAtUtc = now.AddHours(-1)
            },
            new AiRequestLog
            {
                HouseholdId = testDb.HouseholdId,
                Purpose = "summary",
                Router = "orcarouter",
                ResolvedModel = "gpt-x",
                DurationMs = 300,
                Success = false,
                CreatedAtUtc = now.AddHours(-2)
            });
        await testDb.Context.SaveChangesAsync();

        var admin = FakeCurrentUserAccessor.User(Guid.NewGuid(), "運用者");
        var console = Console(testDb, Access(admin, new AdminOptions(), new AuthOptions()));

        var model = await console.LoadAsync();
        Assert.NotNull(model);

        var usage = Assert.Single(model.AiUsage);
        Assert.Equal("gpt-x", usage.ResolvedModel);
        Assert.Equal(2, usage.Requests);
        Assert.Equal(1, usage.Failures);
        Assert.Equal(200d, usage.AverageDurationMs, 3);
    }
}

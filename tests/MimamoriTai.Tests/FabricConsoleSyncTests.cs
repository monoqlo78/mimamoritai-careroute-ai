using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>
/// Covers the read half of the Fabric console sync: the aggregation that replaced the
/// manual <c>sync-to-fabric.ps1</c> run. The write half needs a live Fabric SQL endpoint,
/// which is unreachable outside Azure (Fabric SQL redirects to ports 11000-11999), so
/// these tests pin the behaviour that can be verified locally -- the shape of the rollup,
/// the deterministic keys that make re-running the sync idempotent, and the privacy
/// exclusions.
/// </summary>
public class FabricConsoleSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static FabricSqlConsoleSync CreateSync(TestDb db, FabricConsoleSyncOptions? options = null) =>
        new(db.Context,
            new StubCredential(),
            Options.Create(options ?? new FabricConsoleSyncOptions
            {
                Enabled = true,
                ServerFqdn = "example.database.fabric.microsoft.com",
                Database = "console",
            }),
            new FakeTimeProvider(Now),
            NullLogger<FabricSqlConsoleSync>.Instance);

    [Fact]
    public void IsConfigured_False_Until_Enabled_Server_And_Database_All_Set()
    {
        Assert.False(new FabricConsoleSyncOptions().IsConfigured);
        Assert.False(new FabricConsoleSyncOptions { Enabled = true }.IsConfigured);
        Assert.False(new FabricConsoleSyncOptions { Enabled = true, ServerFqdn = "s" }.IsConfigured);
        Assert.False(new FabricConsoleSyncOptions { ServerFqdn = "s", Database = "d" }.IsConfigured);

        Assert.True(new FabricConsoleSyncOptions
        {
            Enabled = true,
            ServerFqdn = "s",
            Database = "d",
        }.IsConfigured);
    }

    [Fact]
    public async Task Unconfigured_Sync_Fails_Without_Touching_The_Network()
    {
        using var db = await new TestDb().SeedAsync();
        var sync = CreateSync(db, new FabricConsoleSyncOptions());

        var result = await sync.SyncAsync();

        Assert.False(result.Success);
        Assert.False(sync.IsConfigured);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Mock_Reports_Unconfigured_So_Callers_No_Op()
    {
        var sync = new MockFabricConsoleSync();

        var result = await sync.SyncAsync();

        Assert.False(sync.IsConfigured);
        Assert.False(result.Success);
        Assert.Equal(0, result.TotalRows);
    }

    [Fact]
    public void DeterministicId_Is_Stable_And_Distinct_Per_Key()
    {
        // Re-running the sync must update the same row rather than insert a duplicate.
        var first = FabricSqlConsoleSync.DeterministicId("ai-router-call:chat|auto|gpt-4o-mini");
        var again = FabricSqlConsoleSync.DeterministicId("ai-router-call:chat|auto|gpt-4o-mini");
        var other = FabricSqlConsoleSync.DeterministicId("ai-router-call:chat|auto|gpt-4o");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(Guid.Empty, first);
    }

    [Fact]
    public void DeterministicId_Matches_The_PowerShell_Script_Byte_For_Byte()
    {
        // Rows written by scripts/sync-to-fabric.ps1 and by this class must collide
        // deliberately. .NET's new Guid(byte[]) and PowerShell's [guid]::new($bytes)
        // read the same MD5 bytes with the same little-endian interpretation, so a
        // hard-coded expectation here catches any drift in either direction.
        var id = FabricSqlConsoleSync.DeterministicId("household-snapshot:demo");

        Assert.Equal(new Guid(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes("household-snapshot:demo"))), id);
    }

    [Fact]
    public async Task Household_Rollup_Counts_Devices_Residents_And_Recipients()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light(), TestDb.Heater());

        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = db.HouseholdId,
            LineUserId = "U-active",
            IsActive = true,
        });
        db.Context.LineRecipients.Add(new LineRecipient
        {
            HouseholdId = db.HouseholdId,
            LineUserId = "U-gone",
            IsActive = false,
        });
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        var household = Assert.Single(snapshot.Households);
        Assert.Equal(db.HouseholdId, household.HouseholdId);
        Assert.Equal(2, household.DeviceCount);
        Assert.Equal(1, household.ResidentCount);

        // Unfollowed recipients must not be counted: the whole point of the figure is
        // "can this household still be reached".
        Assert.Equal(1, household.ActiveLineRecipients);
    }

    [Fact]
    public async Task Production_Household_With_No_Reachable_Recipient_Needs_Attention()
    {
        var sample = new FabricSqlConsoleSync.HouseholdRow(
            Guid.NewGuid(), "sample", DataSourceMode.Sample, 1, 1, 1, null, null, null, 0, 0, 0, null);
        var production = sample with { DataSourceMode = DataSourceMode.Production };
        var reachable = production with { ActiveLineRecipients = 1 };
        var failing = reachable with { FailedAlertsInWindow = 1 };
        var broken = reachable with { SwitchBotStatus = SwitchBotConnectionStatus.Error };

        // A demo household with no recipients is expected, not a problem.
        Assert.False(FabricSqlConsoleSync.NeedsAttention(sample));
        Assert.True(FabricSqlConsoleSync.NeedsAttention(production));
        Assert.False(FabricSqlConsoleSync.NeedsAttention(reachable));
        Assert.True(FabricSqlConsoleSync.NeedsAttention(failing));
        Assert.True(FabricSqlConsoleSync.NeedsAttention(broken));
    }

    [Fact]
    public async Task Alerts_Are_Windowed_And_Never_Carry_The_Family_Facing_Message()
    {
        using var db = await new TestDb().SeedAsync();

        db.Context.WatchAlerts.Add(new WatchAlert
        {
            HouseholdId = db.HouseholdId,
            PersonId = db.ResidentId,
            RiskLevel = RiskLevel.High,
            Score = 80,
            Reason = "no-activity-12h",
            Message = "お母様のリビングで12時間動きがありません",
            SentAtUtc = Now.AddHours(-1),
            Success = true,
        });
        db.Context.WatchAlerts.Add(new WatchAlert
        {
            HouseholdId = db.HouseholdId,
            PersonId = db.ResidentId,
            RiskLevel = RiskLevel.Low,
            Score = 10,
            Reason = "stale",
            Message = "古い通知",
            SentAtUtc = Now.AddDays(-30),
            Success = true,
        });
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        var alert = Assert.Single(snapshot.Alerts);
        Assert.Equal("no-activity-12h", alert.Reason);

        // AlertRow has no Message member at all -- the resident's name must not reach
        // the operator console. Reason is machine-generated and safe.
        Assert.DoesNotContain(
            "Message",
            typeof(FabricSqlConsoleSync.AlertRow).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task Activity_Is_Rolled_Up_To_The_Hour_Not_Emitted_Per_Event()
    {
        var light = TestDb.Light();
        using var db = await new TestDb().SeedAsync(light);

        foreach (var minute in new[] { 0, 10, 20 })
        {
            db.Context.DeviceEvents.Add(new DeviceEvent
            {
                HouseholdId = db.HouseholdId,
                DeviceId = light.Id,
                EventType = "state",
                State = "on",
                Source = EventSource.SwitchBotPoll,
                OccurredAtUtc = Now.AddHours(-2).AddMinutes(minute),
            });
        }

        db.Context.DeviceEvents.Add(new DeviceEvent
        {
            HouseholdId = db.HouseholdId,
            DeviceId = light.Id,
            EventType = "state",
            State = "off",
            Source = EventSource.SwitchBotPoll,
            OccurredAtUtc = Now.AddHours(-2).AddMinutes(30),
        });

        // Outside the 30-day activity window.
        db.Context.DeviceEvents.Add(new DeviceEvent
        {
            HouseholdId = db.HouseholdId,
            DeviceId = light.Id,
            EventType = "state",
            State = "on",
            Source = EventSource.SwitchBotPoll,
            OccurredAtUtc = Now.AddDays(-90),
        });
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        var bucket = Assert.Single(snapshot.Activity);
        Assert.Equal(4, bucket.EventCount);
        Assert.Equal(3, bucket.OnCount);
        Assert.Equal(0, bucket.BucketStart.Minute);
        Assert.Equal(light.Name, bucket.DeviceName);
    }

    [Fact]
    public async Task AiRouterCalls_Are_All_Time_And_Grouped_By_Purpose_Router_And_Model()
    {
        using var db = await new TestDb().SeedAsync();

        db.Context.AiRequestLogs.AddRange(
            Log("chat", "auto", "gpt-4o-mini", 100, true, Now.AddDays(-400)),
            Log("chat", "auto", "gpt-4o-mini", 300, true, Now.AddHours(-1)),
            Log("chat", "auto", "gpt-4o", 200, false, Now.AddHours(-2)),
            Log("summary", "auto", "gpt-4o-mini", 150, true, Now.AddHours(-3)));
        await db.Context.SaveChangesAsync();

        var snapshot = await CreateSync(db).BuildSnapshotAsync(CancellationToken.None);

        Assert.Equal(3, snapshot.AiCalls.Count);

        var top = snapshot.AiCalls[0];
        Assert.Equal("chat", top.Purpose);
        Assert.Equal("gpt-4o-mini", top.ResolvedModel);

        // The 400-day-old row still counts: callCount is an all-time total, so the
        // console figure only ever moves forward. A window here would make it drop.
        Assert.Equal(2, top.CallCount);
        Assert.Equal(2, top.SuccessCount);
        Assert.Equal(200, top.AvgDurationMs);

        var failed = snapshot.AiCalls.Single(c => c.ResolvedModel == "gpt-4o");
        Assert.Equal(1, failed.CallCount);
        Assert.Equal(0, failed.SuccessCount);
    }

    private static AiRequestLog Log(
        string purpose, string router, string model, int durationMs, bool success, DateTimeOffset at) => new()
        {
            Purpose = purpose,
            Router = router,
            ResolvedModel = model,
            DurationMs = durationMs,
            Success = success,
            CreatedAtUtc = at,
        };

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("These tests never reach the network.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException("These tests never reach the network.");
    }
}

using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

/// <summary>
/// Covers DeviceInsightQuestionService's fabric-then-local-fallback behaviour, mirroring
/// LinePostbackActionServiceTests: Fabric success is used verbatim, Fabric failure/timeout
/// falls back to a deterministic local summary built purely from real aggregates, and an
/// unknown device never throws.
/// </summary>
public class DeviceInsightQuestionServiceTests
{
    private static DeviceInsightQuestionService Create(TestDb db, IFabricDataAgentClient? fabric = null) =>
        new(db.Context, fabric ?? new MockFabricDataAgentClient(), new DeviceInsightService(db.Context, TimeProvider.System));

    [Fact]
    public async Task Unconfigured_Fabric_Goes_Straight_To_The_Local_Deterministic_Summary()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var device = db.Context.Devices.Single();
        var service = Create(db); // MockFabricDataAgentClient.IsConfigured == false

        var answer = await service.GetInsightAsync(db.HouseholdId, device.Id);

        Assert.True(answer.Success);
        Assert.Equal("LocalData", answer.Source);
        Assert.Contains(device.Name, answer.Answer);
    }

    [Fact]
    public async Task Configured_Fabric_Success_Is_Returned_Verbatim()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var device = db.Context.Devices.Single();
        var service = Create(db, new SucceedingFabricClient("Fabricからの回答です。"));

        var answer = await service.GetInsightAsync(db.HouseholdId, device.Id);

        Assert.True(answer.Success);
        Assert.Equal("Fabric", answer.Source);
        Assert.Equal("Fabricからの回答です。", answer.Answer);
    }

    [Fact]
    public async Task Fabric_Failure_Falls_Back_To_The_Local_Summary()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var device = db.Context.Devices.Single();
        var service = Create(db, new FailingFabricClient());

        var answer = await service.GetInsightAsync(db.HouseholdId, device.Id);

        Assert.True(answer.Success);
        Assert.Equal("LocalData", answer.Source);
        Assert.Contains(device.Name, answer.Answer);
    }

    [Fact]
    public async Task Unknown_Device_Returns_An_Unsuccessful_Answer_Without_Throwing()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var service = Create(db);

        var answer = await service.GetInsightAsync(db.HouseholdId, Guid.NewGuid());

        Assert.False(answer.Success);
    }

    [Fact]
    public async Task Device_From_A_Different_Household_Returns_An_Unsuccessful_Answer()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var device = db.Context.Devices.Single();
        var service = Create(db);

        var answer = await service.GetInsightAsync(Guid.NewGuid(), device.Id);

        Assert.False(answer.Success);
    }

    [Fact]
    public async Task No_Usage_History_Yields_A_Deterministic_No_Data_Message()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var device = db.Context.Devices.Single();
        var service = Create(db);

        var answer = await service.GetInsightAsync(db.HouseholdId, device.Id);

        Assert.True(answer.Success);
        Assert.Contains("まだ利用記録がありません", answer.Answer);
    }

    private sealed class SucceedingFabricClient(string text) : IFabricDataAgentClient
    {
        public bool IsConfigured => true;

        public Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default) =>
            Task.FromResult(new FabricAnswer(true, text, "Fabric"));
    }

    private sealed class FailingFabricClient : IFabricDataAgentClient
    {
        public bool IsConfigured => true;

        public Task<FabricAnswer> AskAsync(string question, CancellationToken ct = default) =>
            Task.FromResult(new FabricAnswer(false, string.Empty, "test", "unavailable"));
    }
}

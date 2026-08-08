using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

public class EventStreamOptionsTests
{
    [Fact]
    public void IsConfigured_Is_False_When_Disabled_Even_If_Everything_Else_Is_Set()
    {
        var options = new EventStreamOptions
        {
            Enabled = false,
            ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=s;EntityPath=eh",
            EventHubName = "esehmwhj31nhezoqs3entg9_eh"
        };

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_Is_False_When_ConnectionString_Blank()
    {
        var options = new EventStreamOptions
        {
            Enabled = true,
            ConnectionString = "",
            EventHubName = "esehmwhj31nhezoqs3entg9_eh"
        };

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_Is_False_When_EventHubName_Blank()
    {
        var options = new EventStreamOptions
        {
            Enabled = true,
            ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=s;EntityPath=eh",
            EventHubName = ""
        };

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void IsConfigured_Is_True_When_Enabled_And_All_Required_Fields_Set()
    {
        var options = new EventStreamOptions
        {
            Enabled = true,
            ConnectionString = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=s;EntityPath=eh",
            EventHubName = "esehmwhj31nhezoqs3entg9_eh"
        };

        Assert.True(options.IsConfigured);
    }
}

public class EventHubEventStreamPublisherTests
{
    private static readonly DeviceEventRecord SampleEvent = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "リビング照明",
        "リビング",
        "Light",
        "PowerState",
        "on",
        12.5,
        "SwitchBotPoll",
        new DateTime(2026, 8, 8, 14, 27, 24, DateTimeKind.Utc));

    // --- IsConfigured / no-op behaviour (no network access) -------------------

    [Fact]
    public void IsConfigured_Is_False_When_Disabled()
    {
        var publisher = new EventHubEventStreamPublisher(
            Options.Create(new EventStreamOptions { Enabled = false }),
            NullLogger<EventHubEventStreamPublisher>.Instance);

        Assert.False(publisher.IsConfigured);
        Assert.Equal("EventHub", publisher.DisplayName);
    }

    [Fact]
    public void IsConfigured_Is_False_When_ConnectionString_Or_EventHubName_Blank()
    {
        var publisher = new EventHubEventStreamPublisher(
            Options.Create(new EventStreamOptions { Enabled = true, ConnectionString = "", EventHubName = "eh" }),
            NullLogger<EventHubEventStreamPublisher>.Instance);

        Assert.False(publisher.IsConfigured);
    }

    [Fact]
    public async Task PublishAsync_Returns_NotConfigured_Failure_Without_Throwing_When_Disabled()
    {
        var publisher = new EventHubEventStreamPublisher(
            Options.Create(new EventStreamOptions { Enabled = false }),
            NullLogger<EventHubEventStreamPublisher>.Instance);

        var result = await publisher.PublishAsync([SampleEvent]);

        Assert.False(result.Success);
        Assert.Equal(0, result.PublishedCount);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task DisposeAsync_Does_Not_Throw_When_Not_Configured()
    {
        var publisher = new EventHubEventStreamPublisher(
            Options.Create(new EventStreamOptions { Enabled = false }),
            NullLogger<EventHubEventStreamPublisher>.Instance);

        await publisher.DisposeAsync();
    }

    // --- JSON projection (extracted static method, no network access) ---------

    [Fact]
    public void ToJson_Produces_CamelCase_Properties_Matching_DeviceEvents_Table()
    {
        var json = EventHubEventStreamPublisher.ToJson(SampleEvent);

        Assert.Contains("\"eventId\":\"11111111-1111-1111-1111-111111111111\"", json);
        Assert.Contains("\"householdId\":\"22222222-2222-2222-2222-222222222222\"", json);
        Assert.Contains("\"deviceId\":\"33333333-3333-3333-3333-333333333333\"", json);
        Assert.Contains("\"deviceName\":\"リビング照明\"", json);
        Assert.Contains("\"room\":\"リビング\"", json);
        Assert.Contains("\"deviceType\":\"Light\"", json);
        Assert.Contains("\"eventType\":\"PowerState\"", json);
        Assert.Contains("\"state\":\"on\"", json);
        Assert.Contains("\"powerWatts\":12.5", json);
        Assert.Contains("\"source\":\"SwitchBotPoll\"", json);
        Assert.Contains("\"occurredAtUtc\":\"2026-08-08T14:27:24.0000000Z\"", json);
    }

    [Fact]
    public void ToJson_Serializes_Null_PowerWatts_As_Json_Null()
    {
        var evt = SampleEvent with { PowerWatts = null };

        var json = EventHubEventStreamPublisher.ToJson(evt);

        Assert.Contains("\"powerWatts\":null", json);
    }
}

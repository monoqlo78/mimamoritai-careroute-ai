using Microsoft.Extensions.Logging.Abstractions;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Infrastructure.Fabric;

namespace MimamoriTai.Tests;

public class FallbackEventStreamPublisherTests
{
    /// <summary>A publisher that answers with whatever the test asked for, and counts calls.</summary>
    private sealed class StubPublisher(bool configured, bool success, string name) : IEventStreamPublisher
    {
        public int Calls { get; private set; }

        public bool IsConfigured => configured;

        public string DisplayName => name;

        public Task<EventStreamPublishResult> PublishAsync(
            IReadOnlyList<DeviceEventRecord> events, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(success
                ? new EventStreamPublishResult(true, events.Count, 1)
                : new EventStreamPublishResult(false, 0, 1, $"{name} failed"));
        }
    }

    private static FallbackEventStreamPublisher Publisher(IEventStreamPublisher primary, IEventStreamPublisher fallback) =>
        new(primary, fallback, NullLogger<FallbackEventStreamPublisher>.Instance);

    private static IReadOnlyList<DeviceEventRecord> OneEvent() =>
    [
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "リビング照明", "リビング",
            "Light", "PowerState", "on", null, "SwitchBot", DateTime.UtcNow)
    ];

    [Fact]
    public async Task Fallback_Is_Not_Touched_While_The_Stream_Works()
    {
        var primary = new StubPublisher(true, true, "EventHub");
        var fallback = new StubPublisher(true, true, "Eventhouse");

        var result = await Publisher(primary, fallback).PublishAsync(OneEvent());

        Assert.True(result.Success);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    /// <summary>
    /// The point of the whole class: a paused Eventstream destination must not
    /// cost us the events, because the direct path reaches the same table.
    /// </summary>
    [Fact]
    public async Task Failed_Stream_Falls_Back_And_Still_Counts_As_Published()
    {
        var primary = new StubPublisher(true, false, "EventHub");
        var fallback = new StubPublisher(true, true, "Eventhouse");

        var result = await Publisher(primary, fallback).PublishAsync(OneEvent());

        Assert.True(result.Success);
        Assert.Equal(1, result.PublishedCount);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Both_Failing_Reports_Both_Reasons_So_The_Batch_Is_Retried()
    {
        var primary = new StubPublisher(true, false, "EventHub");
        var fallback = new StubPublisher(true, false, "Eventhouse");

        var result = await Publisher(primary, fallback).PublishAsync(OneEvent());

        Assert.False(result.Success);
        Assert.Equal(0, result.PublishedCount);
        Assert.Contains("EventHub failed", result.Error);
        Assert.Contains("Eventhouse failed", result.Error);
    }

    [Fact]
    public async Task Unconfigured_Fallback_Is_Not_Called()
    {
        var primary = new StubPublisher(true, false, "EventHub");
        var fallback = new StubPublisher(false, true, "Eventhouse");

        var result = await Publisher(primary, fallback).PublishAsync(OneEvent());

        Assert.False(result.Success);
        Assert.Equal(0, fallback.Calls);
        Assert.Equal("EventHub failed", result.Error);
    }

    [Fact]
    public void Display_Name_Shows_Both_Hops()
    {
        var publisher = Publisher(
            new StubPublisher(true, true, "EventHub"),
            new StubPublisher(true, true, "Eventhouse"));

        Assert.Equal("EventHub+Eventhouse", publisher.DisplayName);
        Assert.True(publisher.IsConfigured);
    }
}

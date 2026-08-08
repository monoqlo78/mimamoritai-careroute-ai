using Microsoft.Extensions.Logging;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// DEMO ONLY. Stands in for the Fabric Eventhouse stream while it is not configured,
/// so SwitchBotPollingBackgroundService and the manual publish endpoint stay fully
/// functional (and demoable) with zero secrets and no network calls.
/// </summary>
public sealed class MockEventStreamPublisher(ILogger<MockEventStreamPublisher> logger) : IEventStreamPublisher
{
    public bool IsConfigured => false;

    public string DisplayName => "MockEventStream";

    public Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<DeviceEventRecord> events, CancellationToken ct = default)
    {
        logger.LogDebug("MockEventStream: pretending to publish {Count} device event(s).", events.Count);
        return Task.FromResult(new EventStreamPublishResult(true, events.Count, 0));
    }
}

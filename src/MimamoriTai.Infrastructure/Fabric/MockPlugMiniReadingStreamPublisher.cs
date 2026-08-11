using Microsoft.Extensions.Logging;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// DEMO ONLY. Stands in for the Fabric Eventhouse Plug Mini reading stream while it
/// is not configured, so the polling background service stays fully functional (and
/// demoable) with zero secrets and no network calls.
/// </summary>
public sealed class MockPlugMiniReadingStreamPublisher(ILogger<MockPlugMiniReadingStreamPublisher> logger)
    : IPlugMiniReadingStreamPublisher
{
    public bool IsConfigured => false;

    public string DisplayName => "MockPlugMiniReadingStream";

    public Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<PlugMiniReadingRecord> readings, CancellationToken ct = default)
    {
        logger.LogDebug("MockPlugMiniReadingStream: pretending to publish {Count} reading(s).", readings.Count);
        return Task.FromResult(new EventStreamPublishResult(true, readings.Count, 0));
    }
}

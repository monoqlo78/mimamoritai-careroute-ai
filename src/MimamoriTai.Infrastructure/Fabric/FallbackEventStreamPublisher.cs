using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MimamoriTai.Core.Abstractions;

namespace MimamoriTai.Infrastructure.Fabric;

/// <summary>
/// Sends device events to the Fabric Eventstream first and falls back to writing
/// straight into the Eventhouse when that fails.
///
/// The two paths end at the same KQL table, but they fail for different reasons:
/// the Eventstream sits between the app and the table, so a paused destination or
/// a throttled capacity silently stops delivery, while the direct path only needs
/// the Eventhouse itself. Keeping the direct path wired as a fallback means one
/// bad hop no longer costs us the data, and Azure SQL — the source of truth — is
/// unaffected either way.
///
/// A fallback that succeeds is reported as success so the caller stamps the events
/// as published and does not resend them: they did reach the Eventhouse.
/// </summary>
public sealed class FallbackEventStreamPublisher(
    IEventStreamPublisher primary,
    IEventStreamPublisher fallback,
    ILogger<FallbackEventStreamPublisher> logger) : IEventStreamPublisher
{
    // Configured when either hop can carry the events; the constructor is only
    // reached when both are wired, but this keeps the contract honest.
    public bool IsConfigured => primary.IsConfigured || fallback.IsConfigured;

    public string DisplayName => $"{primary.DisplayName}+{fallback.DisplayName}";

    public async Task<EventStreamPublishResult> PublishAsync(
        IReadOnlyList<DeviceEventRecord> events, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var result = await primary.PublishAsync(events, ct);
        if (result.Success)
        {
            return result;
        }

        // Nothing to fall back to, and nothing gained by trying: report the
        // original failure so the caller retries the whole batch later.
        if (!fallback.IsConfigured)
        {
            return result;
        }

        logger.LogWarning(
            "EventStream publish failed ({Error}); falling back to {Fallback}.",
            result.Error ?? "unknown", fallback.DisplayName);

        var fallbackResult = await fallback.PublishAsync(events, ct);

        // Report the total time, since the caller measures the whole attempt, and
        // keep both errors so the log says which hop broke first.
        return fallbackResult.Success
            ? fallbackResult with { DurationMs = sw.ElapsedMilliseconds }
            : new EventStreamPublishResult(
                false, 0, sw.ElapsedMilliseconds, $"{result.Error} / {fallbackResult.Error}");
    }
}

using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// Produces the "Fabric insight" shown on the device detail page: when the Fabric Data
/// Agent is configured, asks it a device-scoped question; otherwise (or when Fabric fails
/// or exceeds its bounded timeout) falls back to a deterministic summary computed purely
/// from <see cref="DeviceInsightService"/> aggregates - never an invented/guessed answer.
/// Mirrors the fabric-then-local-fallback pattern already used by
/// <see cref="AssistantOrchestrator"/> and <see cref="LinePostbackActionService"/>, with the
/// same bounded-timeout technique as <see cref="LinePostbackActionService"/> so a slow or
/// unreachable Fabric Data Agent can never block the page indefinitely.
/// </summary>
public sealed class DeviceInsightQuestionService(
    IAppDbContext db,
    IFabricDataAgentClient fabric,
    DeviceInsightService deviceInsight)
{
    private const string SourceName = "LocalData";

    /// <summary>
    /// Fabric Data Agent queries can legitimately take a while (the shared HTTP client
    /// allows up to 120s), but a device detail page load must never wait that long.
    /// Bounding the call here - like the LINE "status" postback does - means the page
    /// always resolves quickly, falling back to the local summary if Fabric is too slow.
    /// </summary>
    private static readonly TimeSpan FabricTimeout = TimeSpan.FromSeconds(8);

    public async Task<FabricAnswer> GetInsightAsync(Guid householdId, Guid deviceId, CancellationToken ct = default)
    {
        var device = await db.Devices
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.HouseholdId == householdId, ct);

        if (device is null)
        {
            return new FabricAnswer(false, string.Empty, SourceName, "Device not found.");
        }

        if (fabric.IsConfigured)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FabricTimeout);
            try
            {
                var answer = await fabric.AskAsync(BuildQuestion(device), cts.Token);
                if (answer.Success)
                {
                    return answer;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Fabric exceeded the bounded timeout; fall through to the local summary
                // instead of leaving the page waiting or surfacing a raw cancellation.
            }
        }

        return await BuildLocalSummaryAsync(householdId, device, ct);
    }

    private static string BuildQuestion(Device device) =>
        $"「{device.DisplayName}」（{device.DisplayRoom}、{device.DeviceType}）について、直近{DeviceInsightService.DefaultPeriodDays}日間の利用傾向で気づく点があれば、日本語で1〜2文の簡潔な説明をしてください。";

    /// <summary>
    /// Builds a plain-language summary strictly from real aggregated numbers (no LLM,
    /// nothing invented). Used whenever Fabric is unconfigured, fails, or times out.
    /// </summary>
    private async Task<FabricAnswer> BuildLocalSummaryAsync(Guid householdId, Device device, CancellationToken ct)
    {
        var summary = await deviceInsight.GetUsageSummaryAsync(householdId, device.Id, ct: ct);
        if (summary is null)
        {
            return new FabricAnswer(false, string.Empty, SourceName, "No usage data available.");
        }

        if (summary.PeriodUsageCount == 0 && summary.LastEventAtUtc is null)
        {
            return new FabricAnswer(true, $"{device.DisplayName} はまだ利用記録がありません。", SourceName);
        }

        var text = summary.LastUsedAtUtc is { } lastUsed
            ? $"直近{summary.PeriodDays}日間で{device.DisplayName}は{summary.PeriodUsageCount}回使用され、1日あたり平均{summary.AveragePerDay:0.#}回です。" +
              $"最終利用は{HouseholdTime.ToLocal(lastUsed):M/d HH\\:mm}でした。"
            : $"直近{summary.PeriodDays}日間で{device.DisplayName}は{summary.PeriodUsageCount}回使用され、1日あたり平均{summary.AveragePerDay:0.#}回です。";

        return new FabricAnswer(true, text, SourceName);
    }
}

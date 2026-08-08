using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// Configuration for <see cref="WatchAlertService"/>. Kept as a plain POCO (rather than
/// an IOptions&lt;T&gt; from the LINE infrastructure) so Core has no dependency on
/// Infrastructure; the concrete values are read from LineOptions and wired up in
/// MimamoriTai.Infrastructure.ServiceCollectionExtensions.
/// </summary>
public sealed class WatchAlertSettings
{
    /// <summary>LINE group id or user id to push the alert to. Empty = not configured.</summary>
    public string ToId { get; init; } = string.Empty;

    /// <summary>Minimum risk level (inclusive) that triggers an alert.</summary>
    public RiskLevel Threshold { get; init; } = RiskLevel.Medium;

    /// <summary>How long a repeat alert for the same person + risk level is suppressed.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromHours(6);
}

public enum WatchAlertStatus
{
    /// <summary>Current risk is below the configured threshold; nothing to do.</summary>
    BelowThreshold,

    /// <summary>An identical alert was already sent within the cooldown window.</summary>
    SuppressedByCooldown,

    /// <summary>A push was attempted and the LINE API reported success.</summary>
    Sent,

    /// <summary>A push was attempted but failed (or LINE/AlertToId is not configured).</summary>
    SendFailed,

    /// <summary>No resident is registered for the household; nothing to evaluate.</summary>
    NoResident
}

public sealed record WatchAlertOutcome(
    WatchAlertStatus Status,
    RiskResult? Risk,
    string Message,
    LineSendResult? SendResult)
{
    public bool Sent => Status == WatchAlertStatus.Sent;
    public bool Suppressed => Status == WatchAlertStatus.SuppressedByCooldown;
}

/// <summary>
/// Evaluates the current watch/risk status for a household's resident and, when the
/// risk is at or above the configured threshold, pushes a LINE alert to the family
/// group. Sending is deduplicated per person + risk level using a cooldown window
/// persisted as <see cref="WatchAlert"/> rows, so a demo (or an unattended poll) never
/// spams the family group.
/// </summary>
public sealed class WatchAlertService(IAppDbContext db, ILineMessagingClient line, TimeProvider clock, WatchAlertSettings settings)
{
    public async Task<WatchAlertOutcome> EvaluateAsync(Guid householdId, CancellationToken ct = default)
    {
        try
        {
            var resident = await db.People
                .FirstOrDefaultAsync(p => p.HouseholdId == householdId && p.Role == PersonRole.Resident, ct);

            if (resident is null)
            {
                return new WatchAlertOutcome(WatchAlertStatus.NoResident, null, "本人（Resident）が登録されていません。", null);
            }

            var (today, recent) = await LoadActivityAsync(householdId, ct);
            var nowLocal = HouseholdTime.LocalTime(clock.GetUtcNow());
            var risk = RiskAssessmentService.Evaluate(today, recent, nowLocal);

            if (risk.Level < settings.Threshold)
            {
                return new WatchAlertOutcome(
                    WatchAlertStatus.BelowThreshold,
                    risk,
                    "現在はリスクが低いため、アラートの送信は不要です。",
                    null);
            }

            var now = clock.GetUtcNow();
            var cooldownStart = now - settings.Cooldown;

            var recentAlert = await db.WatchAlerts
                .Where(a => a.PersonId == resident.Id && a.RiskLevel == risk.Level && a.SentAtUtc >= cooldownStart)
                .OrderByDescending(a => a.SentAtUtc)
                .FirstOrDefaultAsync(ct);

            if (recentAlert is not null)
            {
                return new WatchAlertOutcome(
                    WatchAlertStatus.SuppressedByCooldown,
                    risk,
                    "前回のアラートから間もないため、送信をスキップしました（クールダウン中）。",
                    null);
            }

            var text = BuildMessage(resident.DisplayName, risk);
            LineSendResult sendResult;
            try
            {
                sendResult = string.IsNullOrWhiteSpace(settings.ToId)
                    ? new LineSendResult(false, "AlertToId が未設定です。")
                    : await line.PushAsync(settings.ToId, text, ct);
            }
            catch (Exception ex)
            {
                // PushAsync/LineMessagingClient already catches its own network errors, but
                // this is a last-resort guard: an alert must never crash the caller.
                sendResult = new LineSendResult(false, ex.GetType().Name);
            }

            db.WatchAlerts.Add(new WatchAlert
            {
                HouseholdId = householdId,
                PersonId = resident.Id,
                RiskLevel = risk.Level,
                Score = risk.Score,
                Reason = risk.Reason,
                Message = text,
                SentAtUtc = now,
                Success = sendResult.Success,
                Error = sendResult.Error
            });
            await db.SaveChangesAsync(ct);

            return sendResult.Success
                ? new WatchAlertOutcome(WatchAlertStatus.Sent, risk, text, sendResult)
                : new WatchAlertOutcome(WatchAlertStatus.SendFailed, risk, text, sendResult);
        }
        catch (Exception ex)
        {
            // Defensive: this service is polled unattended by a background job and is
            // triggered manually from a demo endpoint. It must never throw.
            return new WatchAlertOutcome(WatchAlertStatus.SendFailed, null, $"アラート評価中にエラーが発生しました（{ex.GetType().Name}）。", new LineSendResult(false, ex.GetType().Name));
        }
    }

    private static string BuildMessage(string residentName, RiskResult risk) =>
        $"{residentName}の見守りアラートです。{risk.Reason}。（スコア {risk.Score}/100）";

    /// <summary>
    /// Loads today's activity plus a 14 day baseline using explicit local dates derived
    /// from <see cref="TimeProvider"/> so the result is deterministic under a fake clock
    /// in tests (ActivityService.GetRecentAsync currently derives "today" from the real
    /// system clock, which would defeat that determinism).
    /// </summary>
    private async Task<(DailyActivity Today, IReadOnlyList<DailyActivity> Recent)> LoadActivityAsync(Guid householdId, CancellationToken ct)
    {
        const int days = 14;
        var activity = new ActivityService(db);
        var todayDate = HouseholdTime.LocalDate(clock.GetUtcNow());

        var recent = new List<DailyActivity>(days);
        for (var offset = days - 1; offset >= 0; offset--)
        {
            recent.Add(await activity.GetDailyAsync(householdId, todayDate.AddDays(-offset), ct));
        }

        return (recent[^1], recent);
    }
}

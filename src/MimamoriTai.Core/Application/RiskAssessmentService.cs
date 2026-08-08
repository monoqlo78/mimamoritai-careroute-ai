using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

public sealed record RiskResult(RiskLevel Level, int Score, string Reason);

/// <summary>
/// Deterministic, rule based risk scoring. Intentionally NOT delegated to the LLM:
/// the model may phrase the result, but never decides whether something is abnormal.
/// </summary>
public sealed class RiskAssessmentService(IAppDbContext db, TimeProvider clock)
{
    public const int NoActivityByHour = 10;

    public static RiskResult Evaluate(DailyActivity today, IReadOnlyList<DailyActivity> baseline, TimeOnly nowLocal)
    {
        var score = 0;
        var reasons = new List<string>();

        if (today.DeviceUsageCount == 0)
        {
            if (nowLocal.Hour >= NoActivityByHour)
            {
                score += 60;
                reasons.Add($"{NoActivityByHour}時を過ぎても家電の利用がありません");
            }
            else
            {
                reasons.Add("まだ本日の活動記録がありません");
            }
        }
        else if (today.FirstActivityTime is { } first && first.Hour >= NoActivityByHour)
        {
            score += 35;
            reasons.Add($"活動開始が{first:HH\\:mm}と遅めです");
        }

        if (today.NightActivityCount >= 2)
        {
            score += 30;
            reasons.Add($"深夜帯に{today.NightActivityCount}回の家電利用があります");
        }

        // Compare against the recent norm, ignoring days with no data at all.
        var reference = baseline.Where(d => d.Date != today.Date && d.DeviceUsageCount > 0).ToList();
        if (reference.Count >= 3 && today.DeviceUsageCount > 0)
        {
            var average = reference.Average(d => d.DeviceUsageCount);
            if (average > 0 && today.DeviceUsageCount <= average * 0.4)
            {
                score += 25;
                reasons.Add($"普段（平均{average:0.#}回）より活動量が少なめです");
            }
        }

        var level = score switch
        {
            >= 60 => RiskLevel.High,
            >= 25 => RiskLevel.Medium,
            _ => RiskLevel.Low
        };

        var reason = reasons.Count > 0
            ? string.Join("／", reasons)
            : "普段どおりの生活リズムです";

        return new RiskResult(level, Math.Min(score, 100), reason);
    }

    public async Task<RiskResult> AssessTodayAsync(Guid householdId, CancellationToken ct = default)
    {
        var activity = new ActivityService(db);
        var recent = await activity.GetRecentAsync(householdId, 14, ct);
        var todayDate = HouseholdTime.LocalDate(clock.GetUtcNow());
        var today = recent.LastOrDefault(d => d.Date == todayDate) ?? new DailyActivity(todayDate, null, null, 0, 0, 0);
        var nowLocal = HouseholdTime.LocalTime(clock.GetUtcNow());

        var result = Evaluate(today, recent, nowLocal);

        var resident = await db.People
            .Where(p => p.HouseholdId == householdId && p.Role == PersonRole.Resident)
            .FirstOrDefaultAsync(ct);

        if (resident is not null)
        {
            db.RiskAssessments.Add(new RiskAssessment
            {
                HouseholdId = householdId,
                PersonId = resident.Id,
                RiskLevel = result.Level,
                Score = result.Score,
                Reason = result.Reason,
                CreatedAtUtc = clock.GetUtcNow()
            });
            await db.SaveChangesAsync(ct);
        }

        return result;
    }
}

using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

public sealed record RiskResult(RiskLevel Level, int Score, string Reason);

/// <summary>
/// A device that is currently on, and for how long. Passed into the risk rules so
/// "電気つけっぱなし" can be judged without the scoring logic touching the database.
/// </summary>
public sealed record LeftOnDevice(string Name, DeviceType DeviceType, TimeSpan On);

/// <summary>
/// Deterministic, rule based risk scoring. Intentionally NOT delegated to the LLM:
/// the model may phrase the result, but never decides whether something is abnormal.
/// </summary>
public sealed class RiskAssessmentService(IAppDbContext db, TimeProvider clock)
{
    public const int NoActivityByHour = 10;

    /// <summary>Anything that produces heat is treated as urgent when left running.</summary>
    public static readonly TimeSpan HeatLeftOnLimit = TimeSpan.FromHours(2);

    /// <summary>Lights and similar appliances left on through the small hours.</summary>
    public static readonly TimeSpan NightLeftOnLimit = TimeSpan.FromHours(4);

    /// <summary>Lights and similar appliances left on during the day.</summary>
    public static readonly TimeSpan DayLeftOnLimit = TimeSpan.FromHours(12);

    public static bool IsHeatProducing(DeviceType type) =>
        type is DeviceType.Heater or DeviceType.Kettle or DeviceType.CookingDevice or DeviceType.Microwave;

    /// <summary>How long this device may stay on before it counts as left on.</summary>
    public static TimeSpan LeftOnLimit(DeviceType type, TimeOnly nowLocal)
    {
        if (IsHeatProducing(type))
        {
            return HeatLeftOnLimit;
        }

        var isNight = nowLocal.Hour is >= ActivityService.NightStartHour and < ActivityService.NightEndHour;
        return isNight ? NightLeftOnLimit : DayLeftOnLimit;
    }

    public static RiskResult Evaluate(
        DailyActivity today,
        IReadOnlyList<DailyActivity> baseline,
        TimeOnly nowLocal,
        IReadOnlyList<LeftOnDevice>? leftOn = null)
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

        // Left-on appliances. Only the single worst offender adds to the score, so a
        // house with several lights on doesn't inflate the level past a real emergency.
        var worst = (leftOn ?? [])
            .Where(d => d.On >= LeftOnLimit(d.DeviceType, nowLocal))
            .OrderByDescending(d => IsHeatProducing(d.DeviceType))
            .ThenByDescending(d => d.On)
            .FirstOrDefault();

        if (worst is not null)
        {
            var hours = (int)worst.On.TotalHours;
            if (IsHeatProducing(worst.DeviceType))
            {
                // On its own this must reach High: a heater left running is the one
                // case where waiting for a second signal is not acceptable.
                score += 60;
                reasons.Add($"{worst.Name}が{hours}時間つけっぱなしです（火災の恐れ）");
            }
            else
            {
                score += 20;
                reasons.Add($"{worst.Name}が{hours}時間つけっぱなしです");
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

        var leftOn = await LoadLeftOnAsync(householdId, ct);
        var result = Evaluate(today, recent, nowLocal, leftOn);

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

    /// <summary>
    /// Reads the current on/off state of each enabled device from its most recent
    /// PowerState event. One query per device: a household has a handful of devices,
    /// and this keeps the intent obvious and provider-agnostic.
    /// </summary>
    public async Task<IReadOnlyList<LeftOnDevice>> LoadLeftOnAsync(Guid householdId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsEnabled)
            .ToListAsync(ct);

        var leftOn = new List<LeftOnDevice>();

        foreach (var device in devices)
        {
            var last = await db.DeviceEvents
                .Where(e => e.DeviceId == device.Id && e.EventType == "PowerState")
                .OrderByDescending(e => e.OccurredAtUtc)
                .FirstOrDefaultAsync(ct);

            if (last is null || !last.State.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var on = now - last.OccurredAtUtc;
            if (on > TimeSpan.Zero)
            {
                leftOn.Add(new LeftOnDevice(device.DisplayName, device.DeviceType, on));
            }
        }

        return leftOn;
    }
}

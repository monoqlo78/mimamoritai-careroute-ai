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
/// A device whose draw has not moved for long enough that the family asked to hear
/// about it. Separate from <see cref="LeftOnDevice"/>: that one is about an appliance
/// running too long, this one is about nothing happening at all.
/// </summary>
public sealed record FlatPowerDevice(string Name, TimeSpan Flat, int ThresholdHours);

/// <summary>
/// Deterministic, rule based risk scoring. Intentionally NOT delegated to the LLM:
/// the model may phrase the result, but never decides whether something is abnormal.
/// </summary>
public sealed class RiskAssessmentService(IAppDbContext db, TimeProvider clock)
{
    /// <summary>
    /// The hour by which every household is expected to have stirred. This is the
    /// backstop, not the goal: a resident who is always up at 6 should not have to wait
    /// until 10 for anyone to notice, which is what <see cref="LateStartGrace"/> is for.
    /// </summary>
    public const int NoActivityByHour = 10;

    /// <summary>
    /// How long past this household's own usual start we wait before calling the morning
    /// late. Short enough to beat the 10 o'clock backstop by hours for an early riser,
    /// long enough that a lie-in is not treated as an emergency.
    /// </summary>
    public static readonly TimeSpan LateStartGrace = TimeSpan.FromHours(2);

    /// <summary>
    /// The earliest the personalised rule is allowed to fire. Without this a household
    /// whose habit really is to be up at 3am would be reported every single morning.
    /// </summary>
    public const int EarliestLateStartHour = 6;

    /// <summary>Hours when a still house means "asleep", not "something is wrong".</summary>
    public const int QuietStartHour = 22;
    /// <summary>Hour the house is expected to be awake again.</summary>
    public const int QuietEndHour = 6;

    /// <summary>
    /// The figure offered when a family does turn stillness watching on for a device.
    /// Short enough to catch a missed lunch, long enough that a quiet afternoon with a
    /// book is not an incident.
    /// </summary>
    public const int DefaultFlatPowerAlertHours = 3;

    public static bool IsQuietHour(TimeOnly local) =>
        local.Hour >= QuietStartHour || local.Hour < QuietEndHour;

    /// <summary>
    /// The time this household usually gets going, taken as the median of the days we
    /// actually hold. Median rather than mean because one 2am night out would otherwise
    /// drag the whole habit an hour earlier and blunt the rule for weeks.
    /// </summary>
    public static TimeOnly? UsualFirstActivity(IReadOnlyList<DailyActivity> baseline, DateOnly today)
    {
        var starts = baseline
            .Where(d => d.Date != today && d.FirstActivityTime is not null)
            .Select(d => d.FirstActivityTime!.Value.ToTimeSpan().TotalMinutes)
            .OrderBy(m => m)
            .ToList();

        // Fewer than three days is a coincidence, not a habit.
        return starts.Count < 3
            ? null
            : TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(starts[starts.Count / 2]));
    }

    /// <summary>
    /// The time today counts as "the morning never happened": this household's usual
    /// start plus a grace period, or the fixed backstop when we have no habit to go on
    /// or the habit would have us calling before dawn.
    /// </summary>
    public static TimeOnly LateStartThreshold(IReadOnlyList<DailyActivity> baseline, DateOnly today)
    {
        var backstop = new TimeOnly(NoActivityByHour, 0);

        if (UsualFirstActivity(baseline, today) is not { } usual)
        {
            return backstop;
        }

        var personal = usual.Add(LateStartGrace);

        return personal.Hour >= EarliestLateStartHour && personal < backstop
            ? personal
            : backstop;
    }

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
        IReadOnlyList<LeftOnDevice>? leftOn = null,
        IReadOnlyList<FlatPowerDevice>? flatPower = null)
    {
        var score = 0;
        var reasons = new List<string>();
        var lateStart = LateStartThreshold(baseline, today.Date);

        if (today.DeviceUsageCount == 0)
        {
            if (nowLocal >= lateStart)
            {
                score += 60;
                reasons.Add($"{lateStart:HH\\:mm}を過ぎても家電の利用がありません");
            }
            else
            {
                reasons.Add("まだ本日の活動記録がありません");
            }
        }
        else if (today.FirstActivityTime is { } first && first >= lateStart)
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

        // Appliances the family asked to be told about when their draw stops moving.
        // Only the longest-still one scores, for the same reason as the left-on rule:
        // a quiet house should read as one concern, not as many.
        var stillest = (flatPower ?? [])
            .OrderByDescending(d => d.Flat)
            .FirstOrDefault();

        if (stillest is not null)
        {
            // A house is meant to be still while everyone is asleep. Scoring that would
            // fire at the same hour every single night, and an alert that cries wolf
            // nightly is worse than no alert at all -- the family stops reading them.
            // Both ends matter: at 6am the last three hours were always going to be
            // flat, so the window has to have covered waking hours to mean anything.
            var windowStart = nowLocal.Add(-stillest.Flat);

            if (!IsQuietHour(nowLocal) && !IsQuietHour(windowStart))
            {
                score += 45;
                reasons.Add(
                    $"{stillest.Name}の使用量が{stillest.ThresholdHours}時間以上変わっていません");
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

    /// <summary>
    /// Reads how long each device's draw has been sitting still, for the devices whose
    /// family asked to be told about it.
    ///
    /// This is the signal that matters for a Plug Mini, and it took a while to see why.
    /// A plug is put in the wall once and left there, so the socket's on/off state
    /// stops changing on day one and every "is anyone up?" rule built on it quietly
    /// stops working. What still moves is the draw: the kettle, the heater, the lamp
    /// behind the plug all show up as the watts going up and coming back down. A whole
    /// day of perfectly flat watts therefore does not mean the appliance is idle -- it
    /// means nobody touched it, which is exactly what the family wants to hear about.
    ///
    /// Deliberately opt-in per device, and measurement says to keep it that way. Making
    /// it the default was tried and rejected: over a week of this household's real
    /// readings, an always-on lamp sat perfectly flat through 91% of waking three-hour
    /// windows, and the house as a whole produced only five significant changes all
    /// week. Watching every device by default would therefore have raised an alert most
    /// afternoons and taught the family to ignore the alerts that matter. Silence stays
    /// the default; the family picks the appliances whose stillness actually says
    /// something -- typically a kettle or a microwave, used daily and never left on.
    /// </summary>
    public async Task<IReadOnlyList<FlatPowerDevice>> LoadFlatPowerAsync(
        Guid householdId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsEnabled && d.FlatPowerAlertHours != null)
            .ToListAsync(ct);

        var flat = new List<FlatPowerDevice>();

        foreach (var device in devices)
        {
            var hours = device.FlatPowerAlertHours!.Value;
            if (hours <= 0)
            {
                continue;
            }

            var since = now.AddHours(-hours);

            var readings = await db.PlugMiniReadings
                .Where(r => r.DeviceId == device.Id && r.ApproxWatts != null && r.OccurredAtUtc >= since)
                .OrderBy(r => r.OccurredAtUtc)
                .Select(r => new { r.OccurredAtUtc, Watts = r.ApproxWatts!.Value })
                .ToListAsync(ct);

            // Two samples is the minimum that can show a change at all. Below that the
            // plug is offline or newly added, and calling that "unchanging" would
            // report a monitoring gap as if it were the resident sitting still.
            if (readings.Count < 2)
            {
                continue;
            }

            // The window has to actually be covered. If the oldest sample we hold is
            // recent, the appliance may well have been busy before it and we simply
            // were not watching.
            if (readings[0].OccurredAtUtc - since > CoverageTolerance)
            {
                continue;
            }

            var min = readings.Min(r => r.Watts);
            var max = readings.Max(r => r.Watts);

            // Same significance test the poller uses to decide what is worth recording,
            // so "no change happened" here means precisely "no change was recorded".
            if (SwitchBotPollingCycleService.IsSignificantPowerChange(min, max))
            {
                continue;
            }

            flat.Add(new FlatPowerDevice(
                device.DisplayName,
                now - readings[0].OccurredAtUtc,
                hours));
        }

        return flat;
    }

    /// <summary>
    /// How much of the requested window may be missing before we decline to judge it.
    /// Readings arrive every few minutes, so an hour of slack absorbs a restart or a
    /// brief outage without letting a genuinely short history masquerade as a long
    /// quiet spell.
    /// </summary>
    public static readonly TimeSpan CoverageTolerance = TimeSpan.FromHours(1);
}

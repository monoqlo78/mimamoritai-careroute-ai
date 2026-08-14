using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// Answers life-rhythm questions straight from the app database. Used whenever the
/// Fabric Data Agent is not configured, and as a safety net when it errors.
/// </summary>
public sealed class LocalDataQuestionService(IAppDbContext db, TimeProvider clock) : ILocalDataQuestionService
{
    private const string SourceName = "LocalData";

    // Reads nothing but the same context and clock this service already holds, so it is
    // built here rather than threaded through every construction site.
    private PowerUsageService PowerUsage => new(db, clock);

    public async Task<FabricAnswer> AnswerAsync(Guid householdId, string question, CancellationToken ct = default)
    {
        var activity = new ActivityService(db);
        var recent = await activity.GetRecentAsync(householdId, 14, ct);
        var todayDate = HouseholdTime.LocalDate(clock.GetUtcNow());
        var today = recent.LastOrDefault(d => d.Date == todayDate) ?? new DailyActivity(todayDate, null, null, 0, 0, 0);
        var q = question ?? string.Empty;

        if (Contains(q, "電力", "電気代", "消費", "ワット", "W", "電圧", "電流", "使用量"))
        {
            return Answer(await PowerFactsAsync(householdId, ct));
        }

        if (Contains(q, "最初", "何時から", "起き", "朝"))
        {
            return Answer(today.FirstActivityTime is { } f
                ? $"今日は{f:HH\\:mm}頃から家電の利用がありました。"
                : "今日はまだ家電の利用が記録されていません。");
        }

        if (Contains(q, "最後", "最後に", "いつ", "直近"))
        {
            return Answer(today.LastActivityTime is { } l
                ? $"最後に家電が使われたのは{l:HH\\:mm}頃です。"
                : "今日はまだ家電の利用が記録されていません。");
        }

        if (Contains(q, "何回", "回数", "使った"))
        {
            return Answer($"今日は家電を{today.DeviceUsageCount}回利用しています。");
        }

        if (Contains(q, "夜中", "深夜", "夜間"))
        {
            var nights = recent.Where(d => d.NightActivityCount > 0).ToList();
            return Answer(nights.Count == 0
                ? "直近2週間で深夜帯の家電利用は記録されていません。"
                : $"深夜帯の利用があったのは {string.Join("、", nights.Select(n => $"{n.Date:M/d}({n.NightActivityCount}回)"))} です。");
        }

        if (Contains(q, "少な", "元気がない", "活動量"))
        {
            var withData = recent.Where(d => d.DeviceUsageCount > 0).ToList();
            if (withData.Count == 0)
            {
                return Answer("比較できる活動データがまだ足りません。");
            }

            var min = withData.OrderBy(d => d.DeviceUsageCount).First();
            var avg = withData.Average(d => d.DeviceUsageCount);
            return Answer($"直近2週間で最も活動が少なかったのは{min.Date:M/d}（{min.DeviceUsageCount}回、平均{avg:0.#}回）です。");
        }

        if (Contains(q, "昨日", "比べ", "変わ"))
        {
            var yesterday = recent.FirstOrDefault(d => d.Date == todayDate.AddDays(-1));
            if (yesterday is null)
            {
                return Answer("昨日のデータが見つかりませんでした。");
            }

            var diff = today.DeviceUsageCount - yesterday.DeviceUsageCount;
            var trend = diff switch
            {
                > 0 => $"昨日より{diff}回多く",
                < 0 => $"昨日より{-diff}回少なく",
                _ => "昨日と同じ回数"
            };
            return Answer($"今日は{today.DeviceUsageCount}回、{trend}家電を利用しています（昨日は{yesterday.DeviceUsageCount}回）。");
        }

        // Default: the "今日のお母さんどう？" style overview.
        var risk = RiskAssessmentService.Evaluate(today, recent, HouseholdTime.LocalTime(clock.GetUtcNow()));
        var resident = await db.People
            .Where(p => p.HouseholdId == householdId && p.Role == PersonRole.Resident)
            .Select(p => p.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "ご本人";

        var head = today.FirstActivityTime is { } start
            ? $"{resident}は今朝{start:HH\\:mm}頃から活動を始め、これまでに家電を{today.DeviceUsageCount}回利用しています。"
            : $"{resident}は本日まだ家電の利用が記録されていません。";

        // The registered devices are stated explicitly so a summarising model can never
        // infer a device count from the usage count ("2回" silently becoming "2台").
        var inventory = await DeviceInventoryAsync(householdId, ct);

        var since = HouseholdTime.StartOfLocalDayUtc(todayDate);
        return Answer($"{head} {risk.Reason}。{await PowerChangeFactsAsync(householdId, since, ct)}{inventory}");
    }

    /// <summary>
    /// The household's registered appliances, named. Included in every overview answer
    /// because a summarising model with no inventory in front of it will happily invent
    /// one out of whatever other number it can see.
    /// </summary>
    private async Task<string> DeviceInventoryAsync(Guid householdId, CancellationToken ct)
    {
        var names = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsActive)
            .OrderBy(d => d.Name)
            .Select(d => d.DisplayNameOverride ?? d.Alias ?? d.Name)
            .ToListAsync(ct);

        return names.Count == 0
            ? "登録されている家電はまだありません。"
            : $"登録されている家電は{names.Count}台（{string.Join("、", names)}）です。";
    }

    /// <summary>
    /// Answers power questions from the Plug Mini time series rather than from the
    /// on/off event count. Voltage, current and the daily energy total are recorded on
    /// every poll, so "電力使用量は？" has a real answer available -- reporting "記録が
    /// ありません" while those rows exist is simply wrong.
    /// </summary>
    private async Task<string> PowerFactsAsync(Guid householdId, CancellationToken ct)
    {
        var since = HouseholdTime.StartOfLocalDayUtc(HouseholdTime.LocalDate(clock.GetUtcNow()));

        var latest = await db.PlugMiniReadings
            .Where(r => r.HouseholdId == householdId)
            .OrderByDescending(r => r.OccurredAtUtc)
            .Select(r => new
            {
                r.OccurredAtUtc,
                r.VoltageV,
                r.CurrentMa,
                r.ApproxWatts,
                r.DailyEnergyWh,
                r.UsageMinutesToday,
                Name = r.Device!.DisplayNameOverride ?? r.Device.Alias ?? r.Device.Name
            })
            .FirstOrDefaultAsync(ct);

        var inventory = await DeviceInventoryAsync(householdId, ct);

        if (latest is null)
        {
            return $"電力の測定値はまだ記録されていません。{await PowerChangeFactsAsync(householdId, since, ct)}{inventory}";
        }

        var parts = new List<string>();

        if (latest.ApproxWatts is { } w)
        {
            parts.Add($"消費電力は約{w:0.#}W");
        }

        if (latest.VoltageV is { } v && latest.CurrentMa is { } ma)
        {
            parts.Add($"電圧{v:0.#}V・電流{ma:0}mA");
        }

        // Deliberately not SwitchBot's `weight` counter read as a daily total: that
        // field is instantaneous watts, so the figure below is integrated from the
        // measured draw instead -- the same arithmetic the SwitchBot app shows.
        var usage = await PowerUsage.GetAsync(householdId, ct: ct);
        if (usage.TodayWh > 0)
        {
            parts.Add($"今日の使用電力量は約{PowerUsageService.Format(usage.TodayWh)}");
        }

        if (latest.UsageMinutesToday is { } minutes)
        {
            parts.Add($"今日の通電時間は{minutes}分");
        }

        var measuredAt = HouseholdTime.LocalTime(latest.OccurredAtUtc);
        var samples = await db.PlugMiniReadings
            .CountAsync(r => r.HouseholdId == householdId && r.OccurredAtUtc >= since, ct);

        return $"{latest.Name}の{measuredAt:HH\\:mm}時点の測定値では、{string.Join("、", parts)}です"
            + $"（今日の測定回数は{samples}回）。{DescribeTrend(usage, HouseholdTime.LocalDate(clock.GetUtcNow()))}"
            + $"{await PowerChangeFactsAsync(householdId, since, ct)}{inventory}";
    }

    /// <summary>
    /// States whether today's electricity use is in line with this home's own habit.
    ///
    /// This is the sentence a family actually wants: the absolute watt-hours mean
    /// nothing to them, but "いつもより少ない" is the difference between a normal day and
    /// a day worth a phone call. Today is judged against how much the same hours had
    /// consumed on previous days, so an answer given at breakfast is not compared with
    /// whole finished days.
    ///
    /// When there is not yet enough history for that judgement, this deliberately does
    /// not stop at "cannot say". Refusing to answer is the least useful thing to tell
    /// someone who is worried, and there is nearly always something concrete on hand --
    /// yesterday's total, or the average of the days recorded so far. It reports what
    /// it has and how thin the evidence is, and only admits to nothing when literally
    /// nothing has been measured.
    /// </summary>
    private static string DescribeTrend(PowerUsageSummary usage, DateOnly todayDate)
    {
        var today = usage.Today ?? PowerUsageComparison.Unknown;
        if (today.Trend != PowerUsageTrend.Unknown)
        {
            var scale = today.Ratio is { } r ? $"（いつもの約{r * 100:0}%）" : string.Empty;
            return $"今の時刻までの使用電力量は、直近{today.BaselineDays}日の同じ時間帯"
                + $"（約{PowerUsageService.Format(today.Baseline)}）と比べて{TrendWord(today.Trend)}{scale}。";
        }

        if (usage.Yesterday is { Trend: not PowerUsageTrend.Unknown } y)
        {
            return $"昨日の使用電力量は約{PowerUsageService.Format(y.EnergyWh)}で、"
                + $"いつも（約{PowerUsageService.Format(y.Baseline)}）と比べて{TrendWord(y.Trend)}です。";
        }

        return DescribeWithoutBaseline(usage, todayDate);
    }

    /// <summary>
    /// The best answer available before a baseline exists: yesterday, the average of the
    /// days on record, and how today sits against them.
    /// </summary>
    private static string DescribeWithoutBaseline(PowerUsageSummary usage, DateOnly today)
    {
        // Today is still accumulating, so averaging it in would drag the "usual day"
        // figure down every morning.
        var pastDays = usage.Daily.Where(d => d.Date < today).ToList();

        if (pastDays.Count == 0)
        {
            return usage.TodayWh > 0
                ? $"比較できる過去の記録は今日が最初の一日です（今日はここまでで約{PowerUsageService.Format(usage.TodayWh)}）。"
                : "電力の記録はまだありません。";
        }

        var parts = new List<string>();

        if (usage.YesterdayWh is { } yesterday)
        {
            parts.Add($"昨日は約{PowerUsageService.Format(yesterday)}");
        }

        var average = pastDays.Average(d => d.EnergyWh);
        parts.Add($"記録のある{pastDays.Count}日間の平均は約{PowerUsageService.Format(average)}");

        // The difference is the point. Stating both figures and leaving the subtraction
        // to the listener is exactly the sort of answer that gets read past.
        var against = usage.YesterdayWh ?? average;
        var label = usage.YesterdayWh is null ? "平均" : "昨日";
        var diff = usage.TodayWh - against;
        var direction = Math.Abs(diff) < 0.05 ? "ほぼ同じ" : diff > 0 ? "多め" : "少なめ";
        var gap = Math.Abs(diff) < 0.05
            ? string.Empty
            : $"（差は約{PowerUsageService.Format(Math.Abs(diff))}）";

        parts.Add($"今の時刻までの今日は約{PowerUsageService.Format(usage.TodayWh)}で、{label}と比べて{direction}{gap}");

        return string.Join("、", parts)
            + $"です。いつもの傾向として判断するには記録が{pastDays.Count}日ぶんなので、参考値としてご覧ください。";
    }

    private static string TrendWord(PowerUsageTrend trend) => trend switch
    {
        PowerUsageTrend.Higher => "多い",
        PowerUsageTrend.Lower => "少ない",
        _ => "ほぼいつもどおり"
    };

    /// <summary>
    /// Describes the swings in draw recorded today. Consumption is not only on/off:
    /// a kettle boiling behind a permanently energised plug shows up here and nowhere
    /// else, so leaving it out would make the assistant claim nothing happened.
    /// </summary>
    private async Task<string> PowerChangeFactsAsync(
        Guid householdId, DateTimeOffset since, CancellationToken ct)
    {
        var changes = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId
                && e.EventType == "PowerChange"
                && e.OccurredAtUtc >= since)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(3)
            .Select(e => new { e.OccurredAtUtc, e.State, e.PowerWatts })
            .ToListAsync(ct);

        if (changes.Count == 0)
        {
            return "今日は消費電力の大きな変化は記録されていません。";
        }

        var described = changes
            .Select(c => $"{HouseholdTime.LocalTime(c.OccurredAtUtc):HH\\:mm}に"
                + (c.State == "increased" ? "増加" : "減少")
                + (c.PowerWatts is { } w ? $"（約{w:0.#}W）" : string.Empty));

        return $"今日は消費電力の変化が{changes.Count}回あり、直近は{string.Join("、", described)}です。";
    }

    private static FabricAnswer Answer(string text) => new(true, text, SourceName);

    private static bool Contains(string question, params string[] keywords) =>
        keywords.Any(k => question.Contains(k, StringComparison.OrdinalIgnoreCase));
}

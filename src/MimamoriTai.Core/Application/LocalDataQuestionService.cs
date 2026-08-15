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

            // Compared by electricity and by the shape of the day, not by a count of
            // polled state changes: "昨日より2回少ない" reads as a decline in someone's
            // health when it may only mean a plug reported less often.
            var usage = await PowerUsage.GetAsync(householdId, ct: ct);
            var rhythm = (yesterday.FirstActivityTime, today.FirstActivityTime) switch
            {
                ({ } was, { } now) => $"家電が動きはじめた時間は、昨日が{was:HH\\:mm}頃、今日は{now:HH\\:mm}頃です。",
                (null, { } now) => $"今日は{now:HH\\:mm}頃から家電が動いています（昨日は記録がありません）。",
                ({ } was, null) => $"昨日は{was:HH\\:mm}頃から動いていましたが、今日はまだ記録がありません。",
                _ => "家電が動きはじめた時間は、昨日も今日も記録がありません。"
            };

            var energy = usage.YesterdayWh is { } yWh
                ? $"使用電力量は、昨日が約{PowerUsageService.Format(yWh)}、今日はここまでで約{PowerUsageService.Format(usage.TodayWh)}です。"
                : $"今日ここまでの使用電力量は約{PowerUsageService.Format(usage.TodayWh)}です。";

            return Answer($"{rhythm}{energy}{await PowerStateFactsAsync(householdId, ct)}");
        }

        // Default: the "今日のお母さんどう？" style overview.
        var risk = RiskAssessmentService.Evaluate(today, recent, HouseholdTime.LocalTime(clock.GetUtcNow()));
        var resident = await db.People
            .Where(p => p.HouseholdId == householdId && p.Role == PersonRole.Resident)
            .Select(p => p.DisplayName)
            .FirstOrDefaultAsync(ct) ?? "ご本人";

        // Deliberately no usage count in the headline.
        //
        // "家電を6回利用しています" was what the assistant kept saying, and it is close to
        // meaningless: the figure counts state changes we happened to poll, so a quiet day
        // with a chatty plug outscores a busy day with a steady one. Worse, it invites the
        // model to reason with it -- the reply that prompted this rewrite managed
        // "家電も2台を6回使われています", which is not a sentence about anybody's wellbeing.
        // What the family is owed is the shape of the day (when things started, when they
        // last moved) and what the electricity says, so that is what the facts lead with.
        var head = today.FirstActivityTime is { } start
            ? today.LastActivityTime is { } last && last != start
                ? $"{resident}は今朝{start:HH\\:mm}頃から家電が動きはじめ、直近で動いたのは{last:HH\\:mm}頃です。"
                : $"{resident}は今朝{start:HH\\:mm}頃から家電が動きはじめています。"
            : $"{resident}は本日まだ家電の利用が記録されていません。";

        // The measured figures ride along with every overview, not only with questions
        // that happened to name a unit. A family asking "具体的に数値も教えて" after being
        // told the day looks normal is asking this same question in different words, and
        // matching on keywords will always miss some of those words; the previous wording
        // list did, and the follow-up came back with the rhythm summary and no numbers at
        // all. Carrying the readings unconditionally means the volts, amps, watts and the
        // day's energy are in front of the reader however the question was phrased -- and
        // it also puts the freshness warning there, which matters most in exactly the
        // vague "how are things?" question that would otherwise never trigger it.
        return Answer($"{head} {risk.Reason}。{await PowerStateFactsAsync(householdId, ct)}"
            + $"{await PowerFactsAsync(householdId, ct)}");
    }

    /// <summary>
    /// Which appliances are on right now, and since when.
    ///
    /// This is the fact the old overview was missing, and its absence is why the
    /// assistant fell back on repeating a usage count: "6回" was the only concrete
    /// number in front of the model. A count of polls is an artefact of how often we
    /// ask SwitchBot, not a description of anybody's day -- the family wants to know
    /// that the television is on and the heater is off.
    /// </summary>
    private async Task<string> PowerStateFactsAsync(Guid householdId, CancellationToken ct)
    {
        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.IsActive)
            .Select(d => new
            {
                d.Id,
                Name = d.DisplayNameOverride ?? d.Alias ?? d.Name
            })
            .ToListAsync(ct);

        if (devices.Count == 0)
        {
            return string.Empty;
        }

        var ids = devices.Select(d => d.Id).ToList();

        // One row per device: the most recent on/off it reported. Read in a single
        // query because a house can hold a dozen plugs and this runs on every question.
        var states = await db.DeviceEvents
            .Where(e => ids.Contains(e.DeviceId) && e.EventType == "PowerState")
            .GroupBy(e => e.DeviceId)
            .Select(g => g.OrderByDescending(e => e.OccurredAtUtc)
                .Select(e => new { e.DeviceId, e.State, e.OccurredAtUtc })
                .First())
            .ToListAsync(ct);

        var on = new List<string>();
        var off = new List<string>();

        foreach (var device in devices)
        {
            var state = states.FirstOrDefault(s => s.DeviceId == device.Id);
            if (state is null)
            {
                continue;
            }

            var since = HouseholdTime.LocalTime(state.OccurredAtUtc);

            if (state.State is "on" or "active")
            {
                on.Add($"{device.Name}（{since:HH\\:mm}頃から）");
            }
            else
            {
                off.Add(device.Name);
            }
        }

        if (on.Count == 0 && off.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        parts.Add(on.Count > 0
            ? $"いま電源が入っているのは{string.Join("、", on)}です"
            : "いま電源が入っている家電はありません");

        if (off.Count > 0)
        {
            parts.Add($"電源が切れているのは{string.Join("、", off)}です");
        }

        return string.Join("。", parts) + "。";
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
    ///
    /// Two things this has to be careful about, both learned from production readings.
    ///
    /// The draw quoted is the plug's own measured real power, never voltage times
    /// current. On a live socket those disagree wildly: a reading of 103.4V and 140mA
    /// computes to 14.5VA while the plug itself reported 0W of real power, and quoting
    /// the former told a family their lamp was drawing 14.5W when nothing was on.
    ///
    /// And a row existing does not mean the plug reported anything. SwitchBot's cloud
    /// serves the last status it received, so when a plug stops reporting -- it dropped
    /// off Wi-Fi, or the cloud went quiet -- every poll keeps returning the same numbers
    /// and we keep storing them. Production has run ten hours that way, with voltage
    /// frozen to the same 103.4V across 123 polls, which mains voltage never does. A
    /// watching-over app that presents that as "the reading at 21:12" is not merely
    /// imprecise; it hides exactly the case a family needs to hear about, because a
    /// silent plug and a quiet evening look identical. So the time quoted is when these
    /// values were first seen, not when we last asked, and a long-frozen reading says so.
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

        // SwitchBot's `weight`, which is instantaneous real watts despite the property
        // name. See the remarks above for why the voltage*current figure is not used.
        if (latest.DailyEnergyWh is { } w)
        {
            parts.Add($"消費電力は{w:0.#}W");
        }

        if (latest.VoltageV is { } v && latest.CurrentMa is { } ma)
        {
            parts.Add($"電圧{v:0.#}V・電流{ma:0}mA");
        }

        if (latest.UsageMinutesToday is { } minutes)
        {
            parts.Add($"その時点の通電時間は{minutes}分");
        }

        var reportedAtUtc = await ReportedAtUtcAsync(householdId, latest.OccurredAtUtc, ct);
        var measuredAt = HouseholdTime.LocalTime(reportedAtUtc);
        var polls = await db.PlugMiniReadings
            .CountAsync(r => r.HouseholdId == householdId && r.OccurredAtUtc >= since, ct);

        var stale = latest.OccurredAtUtc - reportedAtUtc >= StaleAfter
            ? $"ただし、この値は{measuredAt:HH\\:mm}から一度も変わっていません。"
              + "プラグからの新しい報告が届いていない可能性があるため、今の様子としては読まないでください。"
            : string.Empty;

        // Today's total is integrated across the whole day's samples, so it belongs in
        // its own clause rather than inside the "as of HH:mm" reading above.
        var usage = await PowerUsage.GetAsync(householdId, ct: ct);
        var todayEnergy = usage.TodayWh > 0
            ? $"今日ここまでの使用電力量は約{PowerUsageService.Format(usage.TodayWh)}です。"
            : string.Empty;

        return $"{latest.Name}の{measuredAt:HH\\:mm}時点の測定値では、{string.Join("、", parts)}です"
            + $"（今日の取得回数は{polls}回）。{stale}{todayEnergy}"
            + $"{DescribeTrend(usage, HouseholdTime.LocalDate(clock.GetUtcNow()))}"
            + $"{await PowerChangeFactsAsync(householdId, since, ct)}{inventory}";
    }

    /// <summary>
    /// How long every reported field may stay byte-identical before the reading is
    /// treated as a repeat of a stale cache rather than a fresh measurement.
    ///
    /// Six poll cycles. Mains voltage drifts continuously, so half an hour of an
    /// unchanging figure to a tenth of a volt is not a steady house -- it is silence.
    /// Short enough to catch a plug that has fallen off the network within one answer,
    /// long enough that ordinary jitter or a couple of skipped polls never trips it.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>
    /// When the plug last actually told us something new: the oldest poll in the
    /// unbroken run of readings identical to the newest one.
    ///
    /// We have no device-side timestamp to use -- SwitchBot returns a status with no
    /// indication of when it was taken -- so the first time we saw these values is the
    /// closest honest estimate of when they were true.
    /// </summary>
    private async Task<DateTimeOffset> ReportedAtUtcAsync(
        Guid householdId, DateTimeOffset latestAtUtc, CancellationToken ct)
    {
        // Bounded so a plug that has been silent for weeks cannot pull the whole table.
        var floor = latestAtUtc - TimeSpan.FromDays(2);

        var rows = await db.PlugMiniReadings
            .Where(r => r.HouseholdId == householdId
                && r.OccurredAtUtc <= latestAtUtc && r.OccurredAtUtc >= floor)
            .OrderByDescending(r => r.OccurredAtUtc)
            .Select(r => new { r.OccurredAtUtc, r.VoltageV, r.CurrentMa, r.DailyEnergyWh, r.UsageMinutesToday })
            .ToListAsync(ct);

        if (rows.Count == 0)
        {
            return latestAtUtc;
        }

        var newest = rows[0];
        var reportedAt = newest.OccurredAtUtc;

        foreach (var row in rows.Skip(1))
        {
            if (row.VoltageV != newest.VoltageV || row.CurrentMa != newest.CurrentMa
                || row.DailyEnergyWh != newest.DailyEnergyWh
                || row.UsageMinutesToday != newest.UsageMinutesToday)
            {
                break;
            }

            reportedAt = row.OccurredAtUtc;
        }

        return reportedAt;
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

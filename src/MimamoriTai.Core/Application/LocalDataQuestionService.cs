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

    public async Task<FabricAnswer> AnswerAsync(Guid householdId, string question, CancellationToken ct = default)
    {
        var activity = new ActivityService(db);
        var recent = await activity.GetRecentAsync(householdId, 14, ct);
        var todayDate = HouseholdTime.LocalDate(clock.GetUtcNow());
        var today = recent.LastOrDefault(d => d.Date == todayDate) ?? new DailyActivity(todayDate, null, null, 0, 0, 0);
        var q = question ?? string.Empty;

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

        return Answer($"{head} {risk.Reason}。");
    }

    private static FabricAnswer Answer(string text) => new(true, text, SourceName);

    private static bool Contains(string question, params string[] keywords) =>
        keywords.Any(k => question.Contains(k, StringComparison.OrdinalIgnoreCase));
}

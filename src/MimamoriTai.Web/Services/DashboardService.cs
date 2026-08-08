using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Services;

public sealed record DeviceCard(
    Guid Id,
    string Name,
    string Alias,
    string Room,
    string DeviceType,
    bool IsOn,
    DateTimeOffset? LastUsedUtc,
    int TodayUsageCount,
    bool RemoteControlAllowed,
    string SafetyClass);

public sealed record TimelineItem(DateTimeOffset OccurredAtUtc, string DeviceName, string State);

public sealed record FeedItem(DateTimeOffset OccurredAtUtc, string Author, string Content, bool IsAi);

public sealed record DashboardModel(
    Guid HouseholdId,
    string HouseholdName,
    string ResidentName,
    RiskResult Risk,
    IReadOnlyList<Person> People,
    IReadOnlyList<DeviceCard> Devices,
    IReadOnlyList<TimelineItem> Timeline,
    IReadOnlyList<FeedItem> Feed,
    DailyActivity Today,
    IReadOnlyList<DailyActivity> Recent,
    string? LastResolvedModel,
    IntegrationStatus Integrations);

/// <summary>Read model builder for the Blazor dashboard.</summary>
public sealed class DashboardService(
    AppDbContext db,
    IDeviceProvider deviceProvider,
    IntegrationStatus integrations,
    TimeProvider clock)
{
    public async Task<Guid?> GetDefaultHouseholdIdAsync(CancellationToken ct = default) =>
        await db.Households.OrderBy(h => h.CreatedAtUtc).Select(h => (Guid?)h.Id).FirstOrDefaultAsync(ct);

    public async Task<DashboardModel?> LoadAsync(Guid householdId, CancellationToken ct = default)
    {
        var household = await db.Households.FirstOrDefaultAsync(h => h.Id == householdId, ct);
        if (household is null)
        {
            return null;
        }

        var people = await db.People.Where(p => p.HouseholdId == householdId).OrderBy(p => p.Role).ToListAsync(ct);
        var devices = await db.Devices.Where(d => d.HouseholdId == householdId).OrderBy(d => d.Name).ToListAsync(ct);

        var activity = new ActivityService(db);
        var recent = await activity.GetRecentAsync(householdId, 14, ct);
        var todayDate = HouseholdTime.LocalDate(clock.GetUtcNow());
        var today = recent.LastOrDefault(d => d.Date == todayDate) ?? new DailyActivity(todayDate, null, null, 0, 0, 0);
        var risk = RiskAssessmentService.Evaluate(today, recent, HouseholdTime.LocalTime(clock.GetUtcNow()));

        var dayStart = HouseholdTime.StartOfLocalDayUtc(todayDate);

        var todayEvents = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId && e.OccurredAtUtc >= dayStart)
            .ToListAsync(ct);

        var lastUsedList = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId)
            .GroupBy(e => e.DeviceId)
            .Select(g => new { DeviceId = g.Key, Last = g.Max(x => x.OccurredAtUtc) })
            .ToListAsync(ct);

        var lastUsed = lastUsedList.ToDictionary(x => x.DeviceId, x => x.Last);

        var cards = new List<DeviceCard>();
        foreach (var device in devices)
        {
            var status = await deviceProvider.GetStatusAsync(device.ExternalDeviceId, ct);
            cards.Add(new DeviceCard(
                device.Id,
                device.Name,
                device.Alias,
                device.Room,
                device.DeviceType.ToString(),
                status?.IsOn ?? false,
                lastUsed.TryGetValue(device.Id, out var last) ? last : null,
                todayEvents.Count(e => e.DeviceId == device.Id && e.State.Equals("on", StringComparison.OrdinalIgnoreCase)),
                device.RemoteControlAllowed,
                device.SafetyClass.ToString()));
        }

        var deviceNames = devices.ToDictionary(d => d.Id, d => d.Name);

        var timeline = await db.DeviceEvents
            .Where(e => e.HouseholdId == householdId)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(20)
            .ToListAsync(ct);

        var timelineItems = timeline
            .Select(e => new TimelineItem(
                e.OccurredAtUtc,
                deviceNames.TryGetValue(e.DeviceId, out var name) ? name : "不明な機器",
                e.State))
            .ToList();

        var messages = await db.FamilyMessages
            .Where(m => m.HouseholdId == householdId)
            .OrderByDescending(m => m.OccurredAtUtc)
            .Take(20)
            .ToListAsync(ct);

        var peopleNames = people.ToDictionary(p => p.Id, p => p.DisplayName);

        var feed = messages
            .OrderBy(m => m.OccurredAtUtc)
            .Select(m => new FeedItem(
                m.OccurredAtUtc,
                m.MessageType == MessageType.AiReply
                    ? "見守りAI"
                    : (m.PersonId is { } pid && peopleNames.TryGetValue(pid, out var n) ? n : "家族"),
                m.Content,
                m.MessageType == MessageType.AiReply))
            .ToList();

        var lastModel = await db.AiRequestLogs
            .OrderByDescending(l => l.CreatedAtUtc)
            .Select(l => l.ResolvedModel)
            .FirstOrDefaultAsync(ct);

        var resident = people.FirstOrDefault(p => p.Role == PersonRole.Resident)?.DisplayName ?? "ご本人";

        return new DashboardModel(
            household.Id,
            household.Name,
            resident,
            risk,
            people,
            cards,
            timelineItems,
            feed,
            today,
            recent,
            lastModel,
            integrations);
    }
}

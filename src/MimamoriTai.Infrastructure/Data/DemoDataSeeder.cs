using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Auth;
using MimamoriTai.Infrastructure.Devices;

namespace MimamoriTai.Infrastructure.Data;

/// <summary>
/// ==========================================================================
///  DEMO DATA ONLY - NOT REAL SENSOR DATA
/// ==========================================================================
/// Generates a household, three mock devices and ~14 days of synthetic device
/// events so the dashboard and the Q&amp;A features are demoable before the
/// physical SwitchBot devices arrive.
///
/// Every generated event is written with <see cref="EventSource.Seed"/> and the
/// devices carry the "demo-" external id prefix, so demo data is always
/// distinguishable from real data in the database.
/// </summary>
public static class DemoDataSeeder
{
    public const string DemoHouseholdName = "見守り隊デモ世帯";
    public const int DemoDays = 14;

    /// <summary>Deterministic seed keeps demos reproducible between runs.</summary>
    private const int RandomSeed = 20260808;

    public static async Task<Guid> SeedAsync(AppDbContext db, TimeProvider clock, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        // Ensure the fixed dev/demo AppUser row exists so HouseholdAccessService and
        // ownership checks work out of the box with zero login.
        var demoUser = await db.AppUsers.FirstOrDefaultAsync(
            u => u.IdentityProvider == "dev" && u.ExternalSubject == "demo", ct);
        if (demoUser is null)
        {
            db.AppUsers.Add(new AppUser
            {
                Id = DevCurrentUserAccessor.DemoUserId,
                IdentityProvider = "dev",
                ExternalSubject = "demo",
                DisplayName = "デモユーザー",
                CreatedAtUtc = now,
                LastLoginAtUtc = now
            });
        }

        var existing = await db.Households.FirstOrDefaultAsync(h => h.Name == DemoHouseholdName, ct);
        if (existing is not null)
        {
            await db.SaveChangesAsync(ct);
            return existing.Id;
        }

        var household = new Household { Name = DemoHouseholdName, DataSourceMode = DataSourceMode.Sample, CreatedAtUtc = now };
        db.Households.Add(household);

        var resident = new Person { HouseholdId = household.Id, DisplayName = "お母さん", Role = PersonRole.Resident, CreatedAtUtc = now };
        var daughter = new Person { HouseholdId = household.Id, DisplayName = "娘", Role = PersonRole.Family, CreatedAtUtc = now };
        var son = new Person { HouseholdId = household.Id, DisplayName = "息子", Role = PersonRole.Family, CreatedAtUtc = now };
        db.People.AddRange(resident, daughter, son);

        var devices = MockDeviceProvider.SeedDevices.Select(d => new Device
        {
            HouseholdId = household.Id,
            ExternalDeviceId = d.ExternalDeviceId,
            Name = d.Name,
            Alias = MockDeviceProvider.SeedAliases[d.ExternalDeviceId],
            DeviceType = d.DeviceType,
            Room = d.Room,
            Provider = DeviceProviderKind.Mock,
            IsEnabled = true,
            RemoteControlAllowed = true,
            SafetyClass = DeviceSafetyPolicy.Classify(d.DeviceType),
            CreatedAtUtc = now
        }).ToList();

        // The seed list includes a Heater, which DeviceSafetyPolicy.Classify marks as
        // Restricted. That device exists so the safety guard-rail can be demonstrated:
        // asking the AI to turn it on must be refused and audited, never executed.
        db.Devices.AddRange(devices);

        db.DeviceEvents.AddRange(GenerateEvents(household.Id, devices, now));

        db.FamilyMessages.AddRange(
            new FamilyMessage
            {
                HouseholdId = household.Id,
                PersonId = daughter.Id,
                Source = CommandSource.Line,
                MessageType = MessageType.Text,
                Content = "お母さん、今日は暑いから水分とってね",
                OccurredAtUtc = now.AddHours(-5)
            },
            new FamilyMessage
            {
                HouseholdId = household.Id,
                PersonId = resident.Id,
                Source = CommandSource.Line,
                MessageType = MessageType.Text,
                Content = "ありがとう、大丈夫よ",
                OccurredAtUtc = now.AddHours(-4)
            },
            new FamilyMessage
            {
                HouseholdId = household.Id,
                PersonId = son.Id,
                Source = CommandSource.Line,
                MessageType = MessageType.Text,
                Content = "週末に顔出すね",
                OccurredAtUtc = now.AddHours(-3)
            });

        await db.SaveChangesAsync(ct);
        return household.Id;
    }

    /// <summary>
    /// Builds a normal daily rhythm (wake ~07:00, wind down ~23:00) and injects the
    /// three abnormal patterns the demo scenario needs.
    /// </summary>
    public static List<DeviceEvent> GenerateEvents(Guid householdId, IReadOnlyList<Device> devices, DateTimeOffset now)
    {
        var random = new Random(RandomSeed);
        var events = new List<DeviceEvent>();
        var today = HouseholdTime.LocalDate(now);

        var livingLight = devices.First(d => d.Alias == "living-light");
        var bedroomLight = devices.First(d => d.Alias == "bedroom-light");
        var fan = devices.First(d => d.Alias == "living-fan");

        for (var offset = DemoDays - 1; offset >= 0; offset--)
        {
            var date = today.AddDays(-offset);
            var dayStart = HouseholdTime.StartOfLocalDayUtc(date);

            // Abnormal pattern A: no activity until late morning (10 days ago).
            var lateStart = offset == 10;
            // Abnormal pattern B: night-time appliance usage (5 days ago).
            var nightActivity = offset == 5;
            // Abnormal pattern C: unusually low activity (3 days ago).
            var lowActivity = offset == 3;

            var wakeMinutes = lateStart
                ? 11 * 60 + random.Next(0, 30)
                : 7 * 60 + random.Next(-20, 20);

            Add(events, householdId, bedroomLight, dayStart, wakeMinutes, "on");
            Add(events, householdId, bedroomLight, dayStart, wakeMinutes + 12, "off");
            Add(events, householdId, livingLight, dayStart, wakeMinutes + 15, "on");

            if (!lowActivity)
            {
                Add(events, householdId, fan, dayStart, 13 * 60 + random.Next(-30, 30), "on");
                Add(events, householdId, fan, dayStart, 17 * 60 + random.Next(-30, 30), "off");
                Add(events, householdId, livingLight, dayStart, 12 * 60 + random.Next(-20, 20), "off");
                Add(events, householdId, livingLight, dayStart, 18 * 60 + random.Next(-20, 20), "on");
            }

            if (nightActivity)
            {
                Add(events, householdId, livingLight, dayStart, 2 * 60 + 10, "on");
                Add(events, householdId, livingLight, dayStart, 2 * 60 + 40, "off");
                Add(events, householdId, bedroomLight, dayStart, 3 * 60 + 5, "on");
                Add(events, householdId, bedroomLight, dayStart, 3 * 60 + 20, "off");
            }

            // The current day is only simulated up to "now".
            var sleepMinutes = 23 * 60 + random.Next(-25, 25);
            Add(events, householdId, livingLight, dayStart, sleepMinutes, "off");
            Add(events, householdId, bedroomLight, dayStart, sleepMinutes + 5, "on");
            Add(events, householdId, bedroomLight, dayStart, sleepMinutes + 35, "off");
        }

        return events.Where(e => e.OccurredAtUtc <= now).OrderBy(e => e.OccurredAtUtc).ToList();
    }

    private static void Add(
        List<DeviceEvent> events, Guid householdId, Device device, DateTimeOffset dayStartUtc, int minutesFromLocalMidnight, string state)
    {
        events.Add(new DeviceEvent
        {
            HouseholdId = householdId,
            DeviceId = device.Id,
            EventType = "PowerState",
            State = state,
            PowerWatts = state == "on" ? 32.0 : 0.0,
            Source = EventSource.Seed,
            OccurredAtUtc = dayStartUtc.AddMinutes(minutesFromLocalMidnight),
            ReceivedAtUtc = dayStartUtc.AddMinutes(minutesFromLocalMidnight),
            RawPayloadJson = null
        });
    }
}

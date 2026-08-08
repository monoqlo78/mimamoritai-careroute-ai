using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Tests;

public class DemoDataSeederTests
{
    [Fact]
    public async Task TopUpAsync_GeneratesEventsUpToNow()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await DemoDataSeeder.SeedAsync(db.Context, clock);

        // Move the clock 5 days forward, well past the 14-day window SeedAsync built.
        clock.Advance(TimeSpan.FromDays(5));

        await DemoDataSeeder.TopUpAsync(db.Context, clock);

        var newest = db.Context.DeviceEvents
            .Where(e => e.Source == EventSource.Seed)
            .Max(e => e.OccurredAtUtc);

        // The newest event should now be close to "now" (within one simulated day),
        // rather than stuck at the end of the original 14-day demo window.
        Assert.True(newest > clock.GetUtcNow().AddDays(-1));
        Assert.True(newest <= clock.GetUtcNow());
    }

    [Fact]
    public async Task TopUpAsync_IsIdempotent()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await DemoDataSeeder.SeedAsync(db.Context, clock);
        clock.Advance(TimeSpan.FromDays(5));

        await DemoDataSeeder.TopUpAsync(db.Context, clock);
        var countAfterFirst = db.Context.DeviceEvents.Count();

        // Calling it again for the same "now" must not add any more events.
        await DemoDataSeeder.TopUpAsync(db.Context, clock);
        var countAfterSecond = db.Context.DeviceEvents.Count();

        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Fact]
    public async Task TopUpAsync_DoesNotTouchProductionHousehold()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // A Production household that happens to share the demo household's name,
        // so a name-only lookup would incorrectly match it. TopUpAsync must filter
        // on DataSourceMode too.
        var production = new Household
        {
            Name = DemoDataSeeder.DemoHouseholdName,
            DataSourceMode = DataSourceMode.Production,
            CreatedAtUtc = clock.GetUtcNow()
        };
        db.Context.Households.Add(production);
        var device = TestDb.Light();
        device.HouseholdId = production.Id;
        db.Context.Devices.Add(device);
        await db.Context.SaveChangesAsync();

        await DemoDataSeeder.TopUpAsync(db.Context, clock);

        Assert.Empty(db.Context.DeviceEvents.Where(e => e.HouseholdId == production.Id));
    }

    [Fact]
    public async Task TopUpAsync_OnEmptyDatabase_IsNoOp()
    {
        using var db = new TestDb();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        // No exception, no rows created, when there is no demo household at all.
        await DemoDataSeeder.TopUpAsync(db.Context, clock);

        Assert.Empty(db.Context.Households);
        Assert.Empty(db.Context.DeviceEvents);
    }
}

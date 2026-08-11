using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>Controllable publisher stub: records every batch it was asked to publish and can be forced to fail.</summary>
public sealed class FakePlugMiniReadingStreamPublisher : IPlugMiniReadingStreamPublisher
{
    public bool IsConfigured { get; init; } = true;

    public string DisplayName => "FakePlugMiniReadingStream";

    public bool ShouldFail { get; set; }

    public List<IReadOnlyList<PlugMiniReadingRecord>> Calls { get; } = [];

    public Task<EventStreamPublishResult> PublishAsync(IReadOnlyList<PlugMiniReadingRecord> readings, CancellationToken ct = default)
    {
        Calls.Add(readings);

        if (ShouldFail)
        {
            return Task.FromResult(new EventStreamPublishResult(false, 0, 0, "simulated failure"));
        }

        return Task.FromResult(new EventStreamPublishResult(true, readings.Count, 0));
    }
}

public class PlugMiniReadingPublishServiceTests
{
    private static PlugMiniReading MakeReading(
        Guid householdId, Guid deviceId, DateTimeOffset occurredAtUtc, DateTimeOffset? publishedAtUtc = null) => new()
    {
        HouseholdId = householdId,
        DeviceId = deviceId,
        VoltageV = 100.0,
        CurrentMa = 500.0,
        DailyEnergyWh = 12.3,
        UsageMinutesToday = 45,
        ApproxWatts = 50.0,
        OccurredAtUtc = occurredAtUtc,
        PublishedToStreamAtUtc = publishedAtUtc
    };

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Returns_Empty_When_There_Is_Nothing_Pending()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var publisher = new FakePlugMiniReadingStreamPublisher();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new PlugMiniReadingPublishService(db.Context, publisher, clock);

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(0, result.Attempted);
        Assert.Equal(0, result.Published);
        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Empty(publisher.Calls);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Publishes_And_Stamps_Unpublished_Readings()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var light = await db.Context.Devices.SingleAsync();
        var now = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);

        var reading = MakeReading(db.HouseholdId, light.Id, now.AddMinutes(-5));
        db.Context.PlugMiniReadings.Add(reading);
        await db.Context.SaveChangesAsync();

        var publisher = new FakePlugMiniReadingStreamPublisher();
        var clock = new FakeTimeProvider(now);
        var service = new PlugMiniReadingPublishService(db.Context, publisher, clock);

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(1, result.Attempted);
        Assert.Equal(1, result.Published);
        Assert.True(result.Success);
        Assert.Single(publisher.Calls);
        Assert.Equal(light.Name, publisher.Calls[0][0].DeviceName);
        Assert.Equal(light.Room, publisher.Calls[0][0].Room);
        Assert.Equal(50.0, publisher.Calls[0][0].ApproxWatts);

        var reloaded = await db.Context.PlugMiniReadings.SingleAsync(r => r.Id == reading.Id);
        Assert.Equal(now, reloaded.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Does_Not_ReSend_Already_Published_Readings()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var light = await db.Context.Devices.SingleAsync();
        var now = DateTimeOffset.UtcNow;

        var alreadyPublished = MakeReading(db.HouseholdId, light.Id, now.AddMinutes(-10), publishedAtUtc: now.AddMinutes(-9));
        var stillPending = MakeReading(db.HouseholdId, light.Id, now.AddMinutes(-5));
        db.Context.PlugMiniReadings.AddRange(alreadyPublished, stillPending);
        await db.Context.SaveChangesAsync();

        var publisher = new FakePlugMiniReadingStreamPublisher();
        var service = new PlugMiniReadingPublishService(db.Context, publisher, new FakeTimeProvider(now));

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(1, result.Attempted);
        Assert.Single(publisher.Calls);
        Assert.Single(publisher.Calls[0]);
        Assert.Equal(stillPending.Id, publisher.Calls[0][0].ReadingId);

        var reloadedAlreadyPublished = await db.Context.PlugMiniReadings.SingleAsync(r => r.Id == alreadyPublished.Id);
        Assert.Equal(alreadyPublished.PublishedToStreamAtUtc, reloadedAlreadyPublished.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Leaves_Rows_Unstamped_When_Publisher_Fails()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var light = await db.Context.Devices.SingleAsync();

        var reading = MakeReading(db.HouseholdId, light.Id, DateTimeOffset.UtcNow.AddMinutes(-5));
        db.Context.PlugMiniReadings.Add(reading);
        await db.Context.SaveChangesAsync();

        var publisher = new FakePlugMiniReadingStreamPublisher { ShouldFail = true };
        var service = new PlugMiniReadingPublishService(db.Context, publisher, new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(1, result.Attempted);
        Assert.Equal(0, result.Published);
        Assert.False(result.Success);
        Assert.Equal("simulated failure", result.Error);

        var reloaded = await db.Context.PlugMiniReadings.SingleAsync(r => r.Id == reading.Id);
        Assert.Null(reloaded.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Caps_At_BatchSize_And_Orders_Oldest_First()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var light = await db.Context.Devices.SingleAsync();
        var now = DateTimeOffset.UtcNow;

        var oldest = MakeReading(db.HouseholdId, light.Id, now.AddMinutes(-30));
        var middle = MakeReading(db.HouseholdId, light.Id, now.AddMinutes(-20));
        var newest = MakeReading(db.HouseholdId, light.Id, now.AddMinutes(-10));
        db.Context.PlugMiniReadings.AddRange(newest, oldest, middle);
        await db.Context.SaveChangesAsync();

        var publisher = new FakePlugMiniReadingStreamPublisher();
        var service = new PlugMiniReadingPublishService(db.Context, publisher, new FakeTimeProvider(now));

        var result = await service.PublishUnpublishedBatchAsync(batchSize: 2);

        Assert.Equal(2, result.Attempted);
        Assert.Single(publisher.Calls);
        Assert.Equal(2, publisher.Calls[0].Count);
        Assert.Equal(oldest.Id, publisher.Calls[0][0].ReadingId);
        Assert.Equal(middle.Id, publisher.Calls[0][1].ReadingId);

        var reloadedNewest = await db.Context.PlugMiniReadings.SingleAsync(r => r.Id == newest.Id);
        Assert.Null(reloadedNewest.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task ProjectAsync_Fills_In_DeviceName_And_Room_From_The_Devices_Table()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var light = await db.Context.Devices.SingleAsync();
        var reading = MakeReading(db.HouseholdId, light.Id, DateTimeOffset.UtcNow);

        var service = new PlugMiniReadingPublishService(db.Context, new FakePlugMiniReadingStreamPublisher(), new FakeTimeProvider(DateTimeOffset.UtcNow));
        var projected = await service.ProjectAsync([reading]);

        var record = Assert.Single(projected);
        Assert.Equal(light.Name, record.DeviceName);
        Assert.Equal(light.Room, record.Room);
        Assert.Equal(reading.HouseholdId, record.HouseholdId);
        Assert.Equal(reading.DeviceId, record.DeviceId);
        Assert.Equal(reading.VoltageV, record.VoltageV);
        Assert.Equal(reading.CurrentMa, record.CurrentMa);
        Assert.Equal(reading.DailyEnergyWh, record.DailyEnergyWh);
        Assert.Equal(reading.UsageMinutesToday, record.UsageMinutesToday);
        Assert.Equal(reading.ApproxWatts, record.ApproxWatts);
    }
}

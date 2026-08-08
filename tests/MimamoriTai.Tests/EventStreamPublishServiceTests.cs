using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Tests;

/// <summary>Controllable publisher stub: records every batch it was asked to publish and can be forced to fail.</summary>
public sealed class FakeEventStreamPublisher : IEventStreamPublisher
{
    public bool IsConfigured { get; init; } = true;

    public string DisplayName => "FakeEventStream";

    public bool ShouldFail { get; set; }

    public List<IReadOnlyList<DeviceEventRecord>> Calls { get; } = [];

    public Task<EventStreamPublishResult> PublishAsync(IReadOnlyList<DeviceEventRecord> events, CancellationToken ct = default)
    {
        Calls.Add(events);

        if (ShouldFail)
        {
            return Task.FromResult(new EventStreamPublishResult(false, 0, 0, "simulated failure"));
        }

        return Task.FromResult(new EventStreamPublishResult(true, events.Count, 0));
    }
}

public class EventStreamPublishServiceTests
{
    private static DeviceEvent MakeEvent(Guid householdId, Guid deviceId, DateTimeOffset occurredAtUtc, DateTimeOffset? publishedAtUtc = null) => new()
    {
        HouseholdId = householdId,
        DeviceId = deviceId,
        EventType = "PowerState",
        State = "on",
        Source = EventSource.SwitchBotPoll,
        OccurredAtUtc = occurredAtUtc,
        PublishedToStreamAtUtc = publishedAtUtc
    };

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Returns_Empty_When_There_Is_Nothing_Pending()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var publisher = new FakeEventStreamPublisher();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new EventStreamPublishService(db.Context, publisher, clock);

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(0, result.Attempted);
        Assert.Equal(0, result.Published);
        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Empty(publisher.Calls);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Publishes_And_Stamps_Unpublished_Events()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var light = await db.Context.Devices.SingleAsync();
        var now = new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);

        var deviceEvent = MakeEvent(db.HouseholdId, light.Id, now.AddMinutes(-5));
        db.Context.DeviceEvents.Add(deviceEvent);
        await db.Context.SaveChangesAsync();

        var publisher = new FakeEventStreamPublisher();
        var clock = new FakeTimeProvider(now);
        var service = new EventStreamPublishService(db.Context, publisher, clock);

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(1, result.Attempted);
        Assert.Equal(1, result.Published);
        Assert.True(result.Success);
        Assert.Single(publisher.Calls);
        Assert.Equal(light.Name, publisher.Calls[0][0].DeviceName);

        var reloaded = await db.Context.DeviceEvents.SingleAsync(e => e.Id == deviceEvent.Id);
        Assert.Equal(now, reloaded.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Does_Not_ReSend_Already_Published_Events()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var light = await db.Context.Devices.SingleAsync();
        var now = DateTimeOffset.UtcNow;

        var alreadyPublished = MakeEvent(db.HouseholdId, light.Id, now.AddMinutes(-10), publishedAtUtc: now.AddMinutes(-9));
        var stillPending = MakeEvent(db.HouseholdId, light.Id, now.AddMinutes(-5));
        db.Context.DeviceEvents.AddRange(alreadyPublished, stillPending);
        await db.Context.SaveChangesAsync();

        var publisher = new FakeEventStreamPublisher();
        var service = new EventStreamPublishService(db.Context, publisher, new FakeTimeProvider(now));

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(1, result.Attempted);
        Assert.Single(publisher.Calls);
        Assert.Single(publisher.Calls[0]);
        Assert.Equal(stillPending.Id, publisher.Calls[0][0].EventId);

        var reloadedAlreadyPublished = await db.Context.DeviceEvents.SingleAsync(e => e.Id == alreadyPublished.Id);
        Assert.Equal(alreadyPublished.PublishedToStreamAtUtc, reloadedAlreadyPublished.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Leaves_Rows_Unstamped_When_Publisher_Fails()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var light = await db.Context.Devices.SingleAsync();

        var deviceEvent = MakeEvent(db.HouseholdId, light.Id, DateTimeOffset.UtcNow.AddMinutes(-5));
        db.Context.DeviceEvents.Add(deviceEvent);
        await db.Context.SaveChangesAsync();

        var publisher = new FakeEventStreamPublisher { ShouldFail = true };
        var service = new EventStreamPublishService(db.Context, publisher, new FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = await service.PublishUnpublishedBatchAsync();

        Assert.Equal(1, result.Attempted);
        Assert.Equal(0, result.Published);
        Assert.False(result.Success);
        Assert.Equal("simulated failure", result.Error);

        var reloaded = await db.Context.DeviceEvents.SingleAsync(e => e.Id == deviceEvent.Id);
        Assert.Null(reloaded.PublishedToStreamAtUtc);
    }

    [Fact]
    public async Task PublishUnpublishedBatchAsync_Caps_At_BatchSize_And_Orders_Oldest_First()
    {
        using var db = await new TestDb().SeedAsync(TestDb.Light());
        var light = await db.Context.Devices.SingleAsync();
        var now = DateTimeOffset.UtcNow;

        var oldest = MakeEvent(db.HouseholdId, light.Id, now.AddMinutes(-30));
        var middle = MakeEvent(db.HouseholdId, light.Id, now.AddMinutes(-20));
        var newest = MakeEvent(db.HouseholdId, light.Id, now.AddMinutes(-10));
        db.Context.DeviceEvents.AddRange(newest, oldest, middle);
        await db.Context.SaveChangesAsync();

        var publisher = new FakeEventStreamPublisher();
        var service = new EventStreamPublishService(db.Context, publisher, new FakeTimeProvider(now));

        var result = await service.PublishUnpublishedBatchAsync(batchSize: 2);

        Assert.Equal(2, result.Attempted);
        Assert.Single(publisher.Calls);
        Assert.Equal(2, publisher.Calls[0].Count);
        Assert.Equal(oldest.Id, publisher.Calls[0][0].EventId);
        Assert.Equal(middle.Id, publisher.Calls[0][1].EventId);

        var reloadedNewest = await db.Context.DeviceEvents.SingleAsync(e => e.Id == newest.Id);
        Assert.Null(reloadedNewest.PublishedToStreamAtUtc);
    }
}

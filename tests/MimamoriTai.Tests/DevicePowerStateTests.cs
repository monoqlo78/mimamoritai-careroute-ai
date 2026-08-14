using MimamoriTai.Core.Application;

namespace MimamoriTai.Tests;

/// <summary>
/// The rule that decides what the family sees on an appliance card in the seconds
/// right after something changes. Getting this wrong is very visible: pressing 消す
/// and still reading 使用中 makes the whole product look broken.
/// </summary>
public class DevicePowerStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 7, 23, 0, TimeSpan.Zero);

    /// <summary>
    /// The reported bug. SwitchBot still says "on" a moment after we turned the plug
    /// off, so the card used to keep saying 使用中 until the family reloaded the page.
    /// </summary>
    [Fact]
    public void FreshEventBeatsAStaleLiveRead()
    {
        var result = DevicePowerState.Resolve(
            liveIsOn: true, lastEventState: "off", lastEventAtUtc: Now.AddSeconds(-1), nowUtc: Now);

        Assert.False(result.IsOn);
        Assert.True(result.IsKnown);
    }

    [Fact]
    public void FreshEventAlsoWinsWhenTurningOn()
    {
        var result = DevicePowerState.Resolve(
            liveIsOn: false, lastEventState: "on", lastEventAtUtc: Now.AddSeconds(-1), nowUtc: Now);

        Assert.True(result.IsOn);
    }

    /// <summary>
    /// Once the hub has had time to catch up, it is the authority again -- otherwise a
    /// change made at the appliance itself would stay hidden.
    /// </summary>
    [Fact]
    public void LiveReadWinsOnceTheEventIsOlderThanTheWindow()
    {
        var result = DevicePowerState.Resolve(
            liveIsOn: true,
            lastEventState: "off",
            lastEventAtUtc: Now - DevicePowerState.SettlingWindow - TimeSpan.FromSeconds(1),
            nowUtc: Now);

        Assert.True(result.IsOn);
    }

    /// <summary>Infrared remotes and an offline hub both report nothing at all.</summary>
    [Fact]
    public void FallsBackToTheLastEventWhenThereIsNoLiveRead()
    {
        var result = DevicePowerState.Resolve(
            liveIsOn: null, lastEventState: "on", lastEventAtUtc: Now.AddHours(-9), nowUtc: Now);

        Assert.True(result.IsOn);
        Assert.True(result.IsKnown);
    }

    /// <summary>Better to admit "確認中" than to claim the stove is off.</summary>
    [Fact]
    public void NothingToGoOnIsReportedAsUnknown()
    {
        var result = DevicePowerState.Resolve(
            liveIsOn: null, lastEventState: null, lastEventAtUtc: null, nowUtc: Now);

        Assert.False(result.IsKnown);
        Assert.False(result.IsOn);
    }

    /// <summary>
    /// A recent event that is not a definite on/off carries no power information, so it
    /// must not override the hub.
    /// </summary>
    [Fact]
    public void RecentNonPowerEventDoesNotOverrideTheLiveRead()
    {
        var result = DevicePowerState.Resolve(
            liveIsOn: true, lastEventState: "unknown", lastEventAtUtc: Now, nowUtc: Now);

        Assert.True(result.IsOn);
    }

    /// <summary>
    /// A device the app has never recorded still shows its live state -- that is the
    /// newly paired plug in the family's list on day one.
    /// </summary>
    [Fact]
    public void LiveReadAloneIsEnough()
    {
        var result = DevicePowerState.Resolve(
            liveIsOn: true, lastEventState: null, lastEventAtUtc: null, nowUtc: Now);

        Assert.True(result.IsOn);
        Assert.True(result.IsKnown);
    }

    /// <summary>Clock skew between the app and the database must not flip a card.</summary>
    [Fact]
    public void EventStampedSlightlyInTheFutureIsStillTreatedAsFresh()
    {
        var result = DevicePowerState.Resolve(
            liveIsOn: true, lastEventState: "off", lastEventAtUtc: Now.AddSeconds(2), nowUtc: Now);

        Assert.False(result.IsOn);
    }

    [Theory]
    [InlineData("ON")]
    [InlineData("On")]
    public void PowerStateIsMatchedRegardlessOfCasing(string state)
    {
        var result = DevicePowerState.Resolve(
            liveIsOn: false, lastEventState: state, lastEventAtUtc: Now, nowUtc: Now);

        Assert.True(result.IsOn);
    }
}

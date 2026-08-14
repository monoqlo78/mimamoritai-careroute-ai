namespace MimamoriTai.Core.Application;

/// <summary>What the screen should say about an appliance right now.</summary>
/// <param name="IsOn">Whether the appliance is drawing power.</param>
/// <param name="IsKnown">
/// False when nothing can answer - the caller should show "確認中" rather than a
/// confidently wrong "停止中".
/// </param>
public readonly record struct DevicePowerStateResult(bool IsOn, bool IsKnown);

/// <summary>
/// Decides which of two disagreeing sources describes an appliance's power state
/// right now: the live read from the hub, or the newest recorded event.
///
/// SwitchBot applies commands asynchronously and its status endpoint is eventually
/// consistent, so a read issued immediately after a state change still reports the
/// PREVIOUS state. <see cref="DeviceControlService"/> already works around that when
/// it records the requested action instead of the read-back, but the read models
/// then trusted the live value unconditionally - so pressing 消す left the card
/// saying 使用中 until the family reloaded the page a few seconds later.
///
/// A recorded event carries a timestamp; a live read does not. So while the newest
/// event is younger than the hub's settling window, that event is the better answer
/// - whether this app wrote it after its own command, or a SwitchBot webhook pushed
/// it because someone pressed the button on the plug itself. Once the window passes,
/// the live read wins again, so a change made outside the app is never masked for
/// more than a few seconds.
/// </summary>
public static class DevicePowerState
{
    /// <summary>
    /// How long a fresh event outranks the live read. Comfortably longer than the
    /// observed SwitchBot lag (about 1-5 seconds), short enough that the live read
    /// takes over again while the family is still looking at the same screen.
    /// </summary>
    public static readonly TimeSpan SettlingWindow = TimeSpan.FromSeconds(15);

    public static DevicePowerStateResult Resolve(
        bool? liveIsOn,
        string? lastEventState,
        DateTimeOffset? lastEventAtUtc,
        DateTimeOffset nowUtc)
    {
        if (ParsePowerState(lastEventState) is { } recorded
            && lastEventAtUtc is { } occurredAt
            && nowUtc - occurredAt < SettlingWindow)
        {
            return new DevicePowerStateResult(recorded, true);
        }

        return new DevicePowerStateResult(
            liveIsOn ?? IsOnState(lastEventState),
            liveIsOn is not null || lastEventState is not null);
    }

    /// <summary>Null for anything that is not a definite on/off, such as "unknown".</summary>
    private static bool? ParsePowerState(string? state) => state switch
    {
        not null when state.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
        not null when state.Equals("off", StringComparison.OrdinalIgnoreCase) => false,
        _ => null
    };

    private static bool IsOnState(string? state) =>
        string.Equals(state, "on", StringComparison.OrdinalIgnoreCase);
}

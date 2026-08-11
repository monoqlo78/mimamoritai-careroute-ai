namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// Everything derivable from exactly ONE upstream device status request: the
/// state-change projection used for DeviceEvent/alerting (<see cref="Status"/>),
/// plus -- for Plug Mini class devices, from the very same parsed response -- the
/// raw voltage/current/energy telemetry used for PlugMiniReading time-series rows
/// (<see cref="PlugMiniReading"/>, null for non-Plug-Mini devices).
///
/// Introduced specifically so a polling cycle never issues two live
/// GET /v1.1/devices/{id}/status calls for the same device: one for on/off state and
/// a second, separate one for Plug Mini telemetry. See
/// <see cref="IDeviceStatusSnapshotProvider"/> and
/// MimamoriTai.Core.Application.SwitchBotPollingCycleService.
/// </summary>
public sealed record DeviceStatusSnapshot(ProviderDeviceStatus? Status, PlugMiniPowerReading? PlugMiniReading);

/// <summary>
/// Optional capability implemented by providers that can produce both the on/off
/// state projection (<see cref="ProviderDeviceStatus"/>) and Plug Mini telemetry
/// (<see cref="PlugMiniPowerReading"/>) from a single underlying status request per
/// device. <see cref="MimamoriTai.Infrastructure.Devices.SwitchBotDeviceProvider"/>
/// implements this by fetching and parsing the raw status envelope exactly once and
/// deriving both projections from that one parsed body -- it never calls the
/// transport twice to satisfy both halves of this method.
///
/// Kept as a separate, optional interface (rather than folding into
/// <see cref="IDeviceProvider"/>) so providers with no Plug Mini concept at all
/// (e.g. the mock/demo provider) are entirely unaffected and do not need to
/// implement it: callers fall back to plain <see cref="IDeviceProvider.GetStatusAsync"/>
/// for those, and no Plug Mini reading is attempted.
/// </summary>
public interface IDeviceStatusSnapshotProvider
{
    /// <summary>
    /// Single status fetch per call: <see cref="DeviceStatusSnapshot.Status"/> is
    /// null when the device could not be read (matches <see cref="IDeviceProvider.GetStatusAsync"/>'s
    /// null contract); <see cref="DeviceStatusSnapshot.PlugMiniReading"/> is null
    /// whenever the same response carries no Plug Mini telemetry fields (i.e. the
    /// device is not a Plug Mini variant), never triggering a second request to find
    /// that out.
    /// </summary>
    Task<DeviceStatusSnapshot> GetStatusSnapshotAsync(string externalDeviceId, CancellationToken ct = default);
}

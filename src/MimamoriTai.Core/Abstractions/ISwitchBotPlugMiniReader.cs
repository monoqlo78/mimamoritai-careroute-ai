namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// A single Plug Mini telemetry sample, read directly from the device's raw status
/// fields (never derived from the on/off <see cref="ProviderDeviceStatus"/> used for
/// alerting). All values are as reported by the provider; <see cref="ApproxWatts"/> is
/// this provider's own approximation, not a value SwitchBot reports directly.
/// </summary>
public sealed record PlugMiniPowerReading(
    string ExternalDeviceId,
    double? VoltageV,
    double? CurrentMa,
    double? DailyEnergyWh,
    int? UsageMinutesToday,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>
    /// Power-factor-1 approximation (VoltageV * CurrentMa / 1000), i.e. it assumes a
    /// purely resistive/near-unity-power-factor load. SwitchBot does not report
    /// instantaneous real power for Plug Mini, so this is an estimate, not a
    /// manufacturer-reported value -- documented here and in PlugMiniReading.ApproxWatts.
    /// </summary>
    public double? ApproxWatts => VoltageV is { } v && CurrentMa is { } c ? v * c / 1000.0 : null;
}

/// <summary>
/// Optional capability implemented by providers that can read Plug Mini-class
/// telemetry (voltage/current/daily energy/usage minutes) beyond the basic on/off
/// contract in <see cref="IDeviceProvider"/>. Kept as a separate interface (rather
/// than extending IDeviceProvider itself) so the mock provider and any future
/// provider that has no such telemetry are unaffected and existing IDeviceProvider
/// callers/tests never need to know about it.
/// </summary>
public interface ISwitchBotPlugMiniReader
{
    /// <summary>Null when the device does not report Plug Mini telemetry, or the read failed (never throws).</summary>
    Task<PlugMiniPowerReading?> GetPlugMiniReadingAsync(string externalDeviceId, CancellationToken ct = default);
}

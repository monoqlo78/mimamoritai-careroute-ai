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
    /// Power-factor-1 approximation (VoltageV * CurrentMa / 1000), i.e. apparent power
    /// (VA) rather than real power. Only trustworthy for a near-resistive load: checked
    /// against a live plug, a socket drawing 314mA at 104V computes to 32.7W here while
    /// the device itself reported 0.3W of real power. Kept for diagnostics and as a
    /// fallback, but <see cref="RealWatts"/> is the value to reason about.
    /// </summary>
    public double? ApproxWatts => VoltageV is { } v && CurrentMa is { } c ? v * c / 1000.0 : null;

    /// <summary>
    /// Instantaneous real power, in watts, as measured by the plug itself.
    ///
    /// This is SwitchBot's `weight` field, which the carrying property is unfortunately
    /// named for energy. It is not a daily total: it moves up and down through the day
    /// (observed decreasing in production, which a cumulative counter cannot do), it is
    /// zero exactly when the measured current is zero, and it is what the SwitchBot app
    /// labels "電力". The app's own "消費電力量" agrees with this reading integrated over
    /// the app's "使用時間" -- 0.9W over 9h59m is the 0.01kWh it displays.
    /// </summary>
    public double? RealWatts => DailyEnergyWh;
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

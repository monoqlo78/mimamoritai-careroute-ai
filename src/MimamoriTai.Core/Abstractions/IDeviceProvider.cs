using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Abstractions;

public sealed record ProviderDevice(
    string ExternalDeviceId,
    string Name,
    DeviceType DeviceType,
    string Room);

public sealed record ProviderDeviceStatus(
    string ExternalDeviceId,
    string State,
    double? PowerWatts = null,
    DateTimeOffset? ObservedAtUtc = null)
{
    public bool IsOn => string.Equals(State, "on", StringComparison.OrdinalIgnoreCase);
}

public sealed record ProviderResult(bool Success, string? FailureReason = null)
{
    public static ProviderResult Ok() => new(true);
    public static ProviderResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Abstraction over a smart-home device backend. Implemented today by the mock
/// provider and (from tomorrow) by the SwitchBot provider, without touching the
/// application layer.
/// </summary>
public interface IDeviceProvider
{
    DeviceProviderKind Kind { get; }
    bool IsConfigured { get; }

    Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default);
    Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default);
    Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default);
    Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default);
    Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default);
}

/// <summary>
/// Resolves the <see cref="IDeviceProvider"/> that backs a given <see cref="DataSourceMode"/>.
/// Sample always resolves to the mock provider; Production resolves to the real
/// SwitchBot provider when configured, and otherwise falls back to the mock provider
/// so the app never throws for an unconfigured production household.
/// </summary>
public interface IDeviceProviderFactory
{
    IDeviceProvider Get(DataSourceMode mode);
}

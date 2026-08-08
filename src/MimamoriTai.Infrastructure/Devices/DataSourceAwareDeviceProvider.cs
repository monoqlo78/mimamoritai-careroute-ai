using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Infrastructure.Devices;

/// <summary>Plain scoped mutable holder for <see cref="IDataSourceContext"/>. Defaults to Sample.</summary>
public sealed class DataSourceContext : IDataSourceContext
{
    public DataSourceMode Mode { get; set; } = DataSourceMode.Sample;
    public Guid? HouseholdId { get; set; }
}

/// <summary>
/// Decorator registered as the single <see cref="IDeviceProvider"/> in DI. Every call
/// re-reads <see cref="IDataSourceContext.Mode"/> and delegates to the provider the
/// factory resolves for that mode -- so existing constructor-injected call sites
/// (<c>DeviceControlService</c>, <c>DeviceSyncService</c>, <c>AssistantOrchestrator</c>)
/// keep compiling unchanged, while still switching providers per household/request
/// as long as the context is set once at the start of a unit of work (see
/// <see cref="IDataSourceContext"/> for details on where that happens).
/// </summary>
public sealed class DataSourceAwareDeviceProvider(
    IDeviceProviderFactory factory,
    IDataSourceContext context) : IDeviceProvider
{
    private IDeviceProvider Current => factory.Get(context.Mode);

    public DeviceProviderKind Kind => Current.Kind;
    public bool IsConfigured => Current.IsConfigured;

    public Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Current.GetDevicesAsync(ct);

    public Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default) =>
        Current.GetStatusAsync(externalDeviceId, ct);

    public Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default) =>
        Current.TurnOnAsync(externalDeviceId, ct);

    public Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default) =>
        Current.TurnOffAsync(externalDeviceId, ct);

    public Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default) =>
        Current.ToggleAsync(externalDeviceId, ct);
}

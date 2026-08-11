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
    IDataSourceContext context,
    IHouseholdSwitchBotClientFactory householdClients) : IDeviceProvider
{
    private IDeviceProvider Current => factory.Get(context.Mode);

    /// <summary>
    /// Prefers the household's own SwitchBot credentials (the Settings UI writes them to
    /// a SwitchBotConnection row) over the global bootstrap options. Without this a
    /// household that connected its own account still resolved to the mock provider
    /// whenever the deployment had no SwitchBot:Token of its own, so every real device
    /// failed with "未登録の機器です" even though it existed in the database.
    /// Falls back to the mode-based provider when this household has no usable credentials.
    /// </summary>
    private async Task<IDeviceProvider> ResolveAsync(CancellationToken ct)
    {
        if (context.Mode != DataSourceMode.Production || context.HouseholdId is not { } householdId)
        {
            return Current;
        }

        var perHousehold = await householdClients.GetDeviceProviderAsync(householdId, ct);
        return perHousehold.IsConfigured ? perHousehold : Current;
    }

    public DeviceProviderKind Kind => Current.Kind;
    public bool IsConfigured => Current.IsConfigured;

    public async Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        await (await ResolveAsync(ct)).GetDevicesAsync(ct);

    public async Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default) =>
        await (await ResolveAsync(ct)).GetStatusAsync(externalDeviceId, ct);

    public async Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default) =>
        await (await ResolveAsync(ct)).TurnOnAsync(externalDeviceId, ct);

    public async Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default) =>
        await (await ResolveAsync(ct)).TurnOffAsync(externalDeviceId, ct);

    public async Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default) =>
        await (await ResolveAsync(ct)).ToggleAsync(externalDeviceId, ct);
}

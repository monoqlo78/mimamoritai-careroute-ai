using Microsoft.Extensions.Logging;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Infrastructure.Devices;

/// <summary>
/// Skeleton SwitchBot provider.
///
/// The transport (<see cref="ISwitchBotClient"/>) is wired up, but the response
/// mapping is deliberately NOT implemented from guesswork. It is completed once the
/// physical devices arrive and the official SwitchBot OpenAPI v1.1 response shapes
/// have been verified. Until then the application keeps using MockDeviceProvider,
/// and no other layer needs to change.
/// </summary>
public sealed class SwitchBotDeviceProvider(
    ISwitchBotClient client,
    ILogger<SwitchBotDeviceProvider> logger) : IDeviceProvider
{
    internal const string NotImplementedReason =
        "SwitchBot response mapping is pending verification against the official OpenAPI v1.1 specification.";

    public DeviceProviderKind Kind => DeviceProviderKind.SwitchBot;

    public bool IsConfigured => client.IsConfigured;

    public Task<IReadOnlyList<ProviderDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        logger.LogWarning("SwitchBotDeviceProvider.GetDevicesAsync called. {Reason}", NotImplementedReason);
        return Task.FromResult<IReadOnlyList<ProviderDevice>>([]);
    }

    public Task<ProviderDeviceStatus?> GetStatusAsync(string externalDeviceId, CancellationToken ct = default)
    {
        logger.LogWarning("SwitchBotDeviceProvider.GetStatusAsync called. {Reason}", NotImplementedReason);
        return Task.FromResult<ProviderDeviceStatus?>(null);
    }

    public Task<ProviderResult> TurnOnAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Fail(NotImplementedReason));

    public Task<ProviderResult> TurnOffAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Fail(NotImplementedReason));

    public Task<ProviderResult> ToggleAsync(string externalDeviceId, CancellationToken ct = default) =>
        Task.FromResult(ProviderResult.Fail(NotImplementedReason));
}

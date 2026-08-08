using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Infrastructure.Devices;

/// <summary>
/// Sample always resolves to the mock provider (the shared demo dataset never touches
/// real hardware); Production resolves to the real SwitchBot provider when configured,
/// and otherwise falls back to the mock provider so an unconfigured production
/// household never throws -- the demo must always work.
/// </summary>
public sealed class DeviceProviderFactory(
    MockDeviceProvider mock,
    SwitchBotDeviceProvider switchBot) : IDeviceProviderFactory
{
    public IDeviceProvider Get(DataSourceMode mode) => mode switch
    {
        DataSourceMode.Production when switchBot.IsConfigured => switchBot,
        _ => mock
    };
}

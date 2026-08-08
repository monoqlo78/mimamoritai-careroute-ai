namespace MimamoriTai.Core.Abstractions;

/// <summary>
/// Raw SwitchBot OpenAPI v1.1 surface. Deliberately minimal and free of guessed
/// response shapes: the concrete DTO mapping is finished once the physical device
/// arrives and the official specification has been checked.
/// </summary>
public interface ISwitchBotClient
{
    bool IsConfigured { get; }

    Task<string> GetDeviceListRawAsync(CancellationToken ct = default);
    Task<string> GetDeviceStatusRawAsync(string deviceId, CancellationToken ct = default);
    Task<string> SendCommandRawAsync(string deviceId, string command, string parameter, string commandType, CancellationToken ct = default);
}

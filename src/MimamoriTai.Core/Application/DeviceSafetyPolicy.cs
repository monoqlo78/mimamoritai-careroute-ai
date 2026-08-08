using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// Central policy for "may an AI-resolved intent touch this device?".
/// Kept free of I/O so it is trivially unit testable.
/// </summary>
public static class DeviceSafetyPolicy
{
    public static SafetyClass Classify(DeviceType type) => type switch
    {
        DeviceType.Light or DeviceType.Fan or DeviceType.DemoDevice => SafetyClass.Safe,
        _ => SafetyClass.Restricted
    };

    public static readonly DeviceAction[] AllowedAiActions =
    [
        DeviceAction.TurnOn,
        DeviceAction.TurnOff,
        DeviceAction.Toggle,
        DeviceAction.GetStatus
    ];

    public static bool IsStateChanging(DeviceAction action) =>
        action is DeviceAction.TurnOn or DeviceAction.TurnOff or DeviceAction.Toggle;

    /// <summary>
    /// Returns null when the operation is permitted, otherwise a human readable reason.
    /// </summary>
    public static string? Validate(Device device, DeviceAction action, double confidence)
    {
        if (!AllowedAiActions.Contains(action))
        {
            return "許可されていない操作です。";
        }

        if (!device.IsEnabled)
        {
            return $"{device.Name} は現在無効になっています。";
        }

        if (action == DeviceAction.GetStatus)
        {
            return null;
        }

        if (confidence < IntentParser.MinimumConfidence)
        {
            return "指示を確実に理解できませんでした。もう一度、機器の名前を含めて教えてください。";
        }

        if (!device.RemoteControlAllowed)
        {
            return $"{device.Name} は遠隔操作が許可されていません。";
        }

        if (device.SafetyClass == SafetyClass.Restricted && action is DeviceAction.TurnOn or DeviceAction.Toggle)
        {
            return $"{device.Name} は安全のため音声・チャットからの操作を禁止しています。";
        }

        return null;
    }
}

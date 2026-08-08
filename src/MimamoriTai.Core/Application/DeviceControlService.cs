using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

public sealed record DeviceControlOutcome(bool Executed, string Message, Guid? DeviceId, CommandStatus Status);

/// <summary>
/// Resolves an alias to a single device, applies the safety policy, executes through
/// the provider and audits every attempt (including rejections) as a DeviceCommand.
/// </summary>
public sealed class DeviceControlService(
    IAppDbContext db,
    IDeviceProvider provider,
    TimeProvider clock)
{
    public async Task<DeviceControlOutcome> ExecuteAsync(
        Guid householdId,
        string? alias,
        DeviceAction action,
        double confidence,
        string originalText,
        CommandSource source,
        Guid? requestedByPersonId,
        string? aiResolvedModel,
        CancellationToken ct = default)
    {
        var command = new DeviceCommand
        {
            HouseholdId = householdId,
            RequestedByPersonId = requestedByPersonId,
            Source = source,
            OriginalText = originalText,
            Action = action,
            Status = CommandStatus.Pending,
            RequestedAtUtc = clock.GetUtcNow(),
            AiResolvedModel = aiResolvedModel
        };

        var devices = await db.Devices
            .Where(d => d.HouseholdId == householdId)
            .ToListAsync(ct);

        var matches = DeviceResolver.Resolve(devices, alias);

        if (matches.Count == 0)
        {
            return await RejectAsync(command, "対象の機器が見つかりませんでした。登録済みの機器名で指定してください。", ct);
        }

        if (matches.Count > 1)
        {
            var names = string.Join("・", matches.Select(m => m.Name));
            return await RejectAsync(command, $"どの機器か特定できませんでした。{names} のどれでしょうか？", ct);
        }

        var device = matches[0];
        command.DeviceId = device.Id;

        var violation = DeviceSafetyPolicy.Validate(device, action, confidence);
        if (violation is not null)
        {
            return await RejectAsync(command, violation, ct);
        }

        if (action == DeviceAction.GetStatus)
        {
            var status = await provider.GetStatusAsync(device.ExternalDeviceId, ct);
            command.Status = CommandStatus.Succeeded;
            command.ExecutedAtUtc = clock.GetUtcNow();
            db.DeviceCommands.Add(command);
            await db.SaveChangesAsync(ct);

            var stateText = status is null ? "不明" : (status.IsOn ? "ON" : "OFF");
            return new DeviceControlOutcome(true, $"{device.Name} は現在 {stateText} です。", device.Id, CommandStatus.Succeeded);
        }

        var result = action switch
        {
            DeviceAction.TurnOn => await provider.TurnOnAsync(device.ExternalDeviceId, ct),
            DeviceAction.TurnOff => await provider.TurnOffAsync(device.ExternalDeviceId, ct),
            DeviceAction.Toggle => await provider.ToggleAsync(device.ExternalDeviceId, ct),
            _ => ProviderResult.Fail("許可されていない操作です。")
        };

        command.ExecutedAtUtc = clock.GetUtcNow();

        if (!result.Success)
        {
            command.Status = CommandStatus.Failed;
            command.FailureReason = result.FailureReason;
            db.DeviceCommands.Add(command);
            await db.SaveChangesAsync(ct);
            return new DeviceControlOutcome(false, $"{device.Name} の操作に失敗しました。{result.FailureReason}", device.Id, CommandStatus.Failed);
        }

        var newStatus = await provider.GetStatusAsync(device.ExternalDeviceId, ct);
        var newState = newStatus?.State ?? (action == DeviceAction.TurnOn ? "on" : "off");

        command.Status = CommandStatus.Succeeded;
        db.DeviceCommands.Add(command);

        db.DeviceEvents.Add(new DeviceEvent
        {
            HouseholdId = householdId,
            DeviceId = device.Id,
            EventType = "PowerState",
            State = newState,
            PowerWatts = newStatus?.PowerWatts,
            Source = EventSource.AppCommand,
            OccurredAtUtc = command.ExecutedAtUtc.Value,
            ReceivedAtUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(ct);

        var verb = newState.Equals("on", StringComparison.OrdinalIgnoreCase) ? "つけました" : "消しました";
        return new DeviceControlOutcome(true, $"{device.Name} を{verb}。", device.Id, CommandStatus.Succeeded);
    }

    private async Task<DeviceControlOutcome> RejectAsync(DeviceCommand command, string reason, CancellationToken ct)
    {
        command.Status = CommandStatus.Rejected;
        command.FailureReason = reason;
        db.DeviceCommands.Add(command);
        await db.SaveChangesAsync(ct);
        return new DeviceControlOutcome(false, reason, command.DeviceId, CommandStatus.Rejected);
    }
}

public static class DeviceResolver
{
    /// <summary>
    /// Matches an alias against alias / name / "room + type" without ever inventing a device.
    /// An empty or unknown alias yields no matches.
    /// </summary>
    public static List<Device> Resolve(IReadOnlyCollection<Device> devices, string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return [];
        }

        var needle = Normalize(alias);

        var exact = devices
            .Where(d => Normalize(d.Alias) == needle || Normalize(d.Name) == needle)
            .ToList();

        if (exact.Count > 0)
        {
            return exact;
        }

        return devices
            .Where(d => Normalize(d.Alias).Contains(needle, StringComparison.Ordinal)
                     || needle.Contains(Normalize(d.Alias), StringComparison.Ordinal)
                     || Normalize(d.Name).Contains(needle, StringComparison.Ordinal)
                     || needle.Contains(Normalize(d.Name), StringComparison.Ordinal))
            .ToList();
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("　", string.Empty);
}

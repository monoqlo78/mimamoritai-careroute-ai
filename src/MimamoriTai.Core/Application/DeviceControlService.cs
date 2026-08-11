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

        // "電源はついてる？" names no device at all. For a read-only status check on a
        // household that owns exactly one device there is nothing to disambiguate and
        // nothing to break, so answering beats refusing. State-changing actions keep the
        // stricter contract: they never act on a device the resident did not name.
        if (matches.Count == 0 && devices.Count == 1 && action == DeviceAction.GetStatus)
        {
            matches = devices;
        }

        if (matches.Count == 0)
        {
            var known = devices.Count == 0
                ? "まだ機器が登録されていません。"
                : $"登録されているのは {string.Join("・", devices.Select(d => d.DisplayName))} です。";

            return await RejectAsync(command, $"対象の機器が見つかりませんでした。{known}", ct);
        }

        if (matches.Count > 1)
        {
            var names = string.Join("・", matches.Select(m => m.DisplayName));
            return await RejectAsync(command, $"どの機器か特定できませんでした。{names} のどれでしょうか？", ct);
        }

        var device = matches[0];
        command.DeviceId = device.Id;

        var violation = DeviceSafetyPolicy.Validate(device, action, confidence);
        if (violation is not null)
        {
            return await RejectAsync(command, violation, ct);
        }

        if (DeviceSafetyPolicy.IsStateChanging(action))
        {
            var throttle = await CheckRateLimitAsync(householdId, device.Id, action, ct);
            if (throttle is not null)
            {
                return await RejectAsync(command, throttle, ct);
            }
        }

        if (action == DeviceAction.GetStatus)
        {
            var status = await provider.GetStatusAsync(device.ExternalDeviceId, ct);
            command.Status = CommandStatus.Succeeded;
            command.ExecutedAtUtc = clock.GetUtcNow();
            db.DeviceCommands.Add(command);
            await db.SaveChangesAsync(ct);

            var stateText = status is null ? "不明" : (status.IsOn ? "ON" : "OFF");
            return new DeviceControlOutcome(true, $"{device.DisplayName} は現在 {stateText} です。", device.Id, CommandStatus.Succeeded);
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
            return new DeviceControlOutcome(false, $"{device.DisplayName} の操作に失敗しました。{result.FailureReason}", device.Id, CommandStatus.Failed);
        }

        // SwitchBot applies commands asynchronously: a read-back issued immediately
        // after the command still reports the PREVIOUS power state. Trusting it wrote
        // an "on" event right after turning a device off, which both flipped the reply
        // ("つけました" for a turn-off) and poisoned downstream rules such as the
        // left-on detection. The requested action is therefore the source of truth for
        // the resulting state; the read-back is only used for the live wattage, and for
        // Toggle, where the caller by definition does not know the target state.
        var newStatus = await provider.GetStatusAsync(device.ExternalDeviceId, ct);
        var newState = action switch
        {
            DeviceAction.TurnOn => "on",
            DeviceAction.TurnOff => "off",
            _ => newStatus?.State ?? "unknown"
        };

        command.Status = CommandStatus.Succeeded;
        db.DeviceCommands.Add(command);

        // Only record an event when the state actually changed. Asking to turn on a
        // device that is already on is a no-op for the resident: writing an event
        // regardless made "家電の利用" climb every time someone spoke to the
        // assistant, so the dashboard reported activity that never happened. This
        // mirrors the guard the polling cycle already applies.
        var lastState = await db.DeviceEvents
            .Where(e => e.DeviceId == device.Id)
            .OrderByDescending(e => e.OccurredAtUtc)
            .Select(e => e.State)
            .FirstOrDefaultAsync(ct);

        if (!string.Equals(lastState, newState, StringComparison.OrdinalIgnoreCase))
        {
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
        }

        await db.SaveChangesAsync(ct);

        var verb = newState switch
        {
            "on" => "つけました",
            "off" => "消しました",
            _ => "操作しました"
        };
        return new DeviceControlOutcome(true, $"{device.DisplayName} を{verb}。", device.Id, CommandStatus.Succeeded);
    }

    /// <summary>
    /// Caps how often the assistant may physically change the home, using the same
    /// DeviceCommand audit trail the UI shows. Returns null when the command may
    /// proceed, otherwise the Japanese reason shown to the family.
    ///
    /// Only executed (Succeeded) state changes count: rejections must not lock the
    /// household out, and reads are never limited.
    /// </summary>
    private async Task<string?> CheckRateLimitAsync(
        Guid householdId, Guid deviceId, DeviceAction action, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var windowStart = now - DeviceSafetyPolicy.RateLimitWindow;

        var recent = await db.DeviceCommands
            .Where(c => c.HouseholdId == householdId
                     && c.Status == CommandStatus.Succeeded
                     && c.ExecutedAtUtc != null
                     && c.ExecutedAtUtc >= windowStart)
            .Select(c => new { c.DeviceId, c.Action, c.ExecutedAtUtc })
            .ToListAsync(ct);

        var stateChanges = recent.Count(c => DeviceSafetyPolicy.IsStateChanging(c.Action));

        if (stateChanges >= DeviceSafetyPolicy.MaxStateChangesPerWindow)
        {
            var minutes = (int)DeviceSafetyPolicy.RateLimitWindow.TotalMinutes;
            return $"安全のため、{minutes}分間に操作できる回数の上限（{DeviceSafetyPolicy.MaxStateChangesPerWindow}回）に達しました。少し時間をおいてから試してください。";
        }

        var repeatStart = now - DeviceSafetyPolicy.RepeatWindow;
        var repeats = recent.Count(c =>
            c.DeviceId == deviceId && c.Action == action && c.ExecutedAtUtc >= repeatStart);

        if (repeats >= DeviceSafetyPolicy.MaxIdenticalRepeats)
        {
            return "同じ操作が短時間に繰り返されています。安全のため一度お休みします。";
        }

        return null;
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
    /// Matches an alias against alias / name / the family's own display name without ever
    /// inventing a device. Both the provider's label and the name typed on screen are
    /// matched, so a device stays reachable by whichever name the speaker happens to know.
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
            .Where(d => Names(d).Any(n => n == needle))
            .ToList();

        if (exact.Count > 0)
        {
            return exact;
        }

        return devices
            .Where(d => Names(d).Any(n =>
                n.Length > 0
                && (n.Contains(needle, StringComparison.Ordinal) || needle.Contains(n, StringComparison.Ordinal))))
            .ToList();
    }

    /// <summary>Every name this device answers to, normalized.</summary>
    private static IEnumerable<string> Names(Device device)
    {
        yield return Normalize(device.Alias);
        yield return Normalize(device.Name);

        if (!string.IsNullOrWhiteSpace(device.DisplayNameOverride))
        {
            yield return Normalize(device.DisplayNameOverride);
        }
    }

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("　", string.Empty);
}

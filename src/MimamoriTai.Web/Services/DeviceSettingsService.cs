using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Application;
using MimamoriTai.Core.Domain;
using MimamoriTai.Infrastructure.Data;

namespace MimamoriTai.Web.Services;

public enum DeviceSettingsUpdateStatus
{
    Updated,
    NotFoundOrDenied,
    SampleHouseholdNotEditable,
    InvalidName
}

public sealed record DeviceSettingsUpdateResult(
    DeviceSettingsUpdateStatus Status,
    bool RemoteControlAllowed,
    string SafetyClass,
    string Message);

/// <summary>
/// Owner-facing settings for a single device: what it is called, and whether it may be
/// actuated remotely.
///
/// <para>
/// <see cref="DeviceSyncService"/> deliberately never grants remote control: discovery
/// and authority are separate concerns, so a device that merely appears in the SwitchBot
/// account cannot be actuated until a human says so. This service is the missing other
/// half of that design - without it a synced device is permanently unusable, which is the
/// state real hardware was found in.
/// </para>
///
/// <para>
/// Two independent switches are exposed because they answer different questions:
/// <list type="bullet">
///   <item><description><c>RemoteControlAllowed</c> - may this device be actuated remotely at all?</description></item>
///   <item><description><c>SafetyClass</c> - may it be turned <em>on</em> unattended? A plug is
///   <see cref="SafetyClass.Restricted"/> by default because the appliance plugged into it is
///   unknown to us and may be a heater; only the owner knows what is actually connected.</description></item>
/// </list>
/// Both default to the safe answer and only the owner can widen them.
/// </para>
/// </summary>
public sealed class DeviceSettingsService(
    AppDbContext db,
    HouseholdAccessService householdAccess)
{
    /// <summary>
    /// Sets the name the family sees and speaks. The name matters functionally, not just
    /// cosmetically: <see cref="DeviceResolver"/> matches spoken and typed phrases against it,
    /// so a device still carrying its vendor label ("プラグミニ 92") can never be reached by
    /// asking for "電気". Renaming it is what makes natural language work at all.
    ///
    /// <para>
    /// The new name is written to <see cref="Device.DisplayNameOverride"/>, never over
    /// <see cref="Device.Name"/>: Name is the provider's own label and
    /// <see cref="DeviceSyncService"/> refreshes it from SwitchBot on every poll, so a
    /// correction stored there would silently disappear minutes later. Keeping the raw label
    /// also means both names keep resolving, and the device stays recognisable in the
    /// SwitchBot app.
    /// </para>
    /// </summary>
    public async Task<DeviceSettingsUpdateResult> RenameAsync(
        Guid deviceId,
        string newName,
        CancellationToken ct = default)
    {
        var (device, denied) = await ResolveEditableAsync(deviceId, ct);
        if (denied is not null)
        {
            return denied;
        }

        var validation = TryApplyName(device!, newName);
        if (validation is not null)
        {
            return validation;
        }

        await db.SaveChangesAsync(ct);

        return new DeviceSettingsUpdateResult(
            DeviceSettingsUpdateStatus.Updated,
            device!.RemoteControlAllowed,
            device.SafetyClass.ToString(),
            $"呼び名を「{device.DisplayName}」に変更しました。この名前で話しかけると操作できます。");
    }

    /// <summary>
    /// Sets both the display name and the room in one save, which is how the screen presents
    /// them: the family corrects "リビングの電気" and "リビング" together.
    ///
    /// <para>
    /// A blank <paramref name="room"/> clears the override and falls back to whatever the
    /// provider reported - SwitchBot has no room concept, so that fallback is a placeholder,
    /// but it is still better than storing an empty string as if the family had chosen it.
    /// The display name cannot be blanked the same way, because an unnamed device cannot be
    /// spoken to at all.
    /// </para>
    /// </summary>
    public async Task<DeviceSettingsUpdateResult> UpdateNamingAsync(
        Guid deviceId,
        string newName,
        string? room,
        CancellationToken ct = default)
    {
        var (device, denied) = await ResolveEditableAsync(deviceId, ct);
        if (denied is not null)
        {
            return denied;
        }

        room = room?.Trim();
        if (room is { Length: > 64 })
        {
            // Checked before anything is applied, so a rejected save leaves the device untouched.
            return new DeviceSettingsUpdateResult(
                DeviceSettingsUpdateStatus.InvalidName,
                device!.RemoteControlAllowed,
                device.SafetyClass.ToString(),
                "部屋の名前は64文字以内で入力してください。");
        }

        var validation = TryApplyName(device!, newName);
        if (validation is not null)
        {
            return validation;
        }

        // Storing null rather than a copy of the provider value keeps "the family chose this"
        // distinguishable from "nobody has said yet", which is what sync relies on.
        device!.RoomOverride = string.IsNullOrEmpty(room) || room == device.Room ? null : room;

        await db.SaveChangesAsync(ct);

        var where = string.IsNullOrWhiteSpace(device.DisplayRoom)
            ? string.Empty
            : $"（{device.DisplayRoom}）";

        return new DeviceSettingsUpdateResult(
            DeviceSettingsUpdateStatus.Updated,
            device.RemoteControlAllowed,
            device.SafetyClass.ToString(),
            $"「{device.DisplayName}」{where}に変更しました。この名前で話しかけると操作できます。");
    }

    /// <summary>
    /// Validates and stages the display name. Returns null on success, otherwise the refusal
    /// to hand straight back to the caller.
    /// </summary>
    private static DeviceSettingsUpdateResult? TryApplyName(Device device, string newName)
    {
        newName = newName?.Trim() ?? string.Empty;
        if (newName.Length is 0 or > 60)
        {
            return new DeviceSettingsUpdateResult(
                DeviceSettingsUpdateStatus.InvalidName,
                device.RemoteControlAllowed,
                device.SafetyClass.ToString(),
                "呼び名は1〜60文字で入力してください。");
        }

        device.DisplayNameOverride = newName == device.Name ? null : newName;
        return null;
    }

    public async Task<DeviceSettingsUpdateResult> UpdatePermissionsAsync(
        Guid deviceId,
        bool remoteControlAllowed,
        bool treatAsSafeAppliance,
        CancellationToken ct = default,
        bool blockRemoteTurnOn = false)
    {
        var (device, denied) = await ResolveEditableAsync(deviceId, ct);
        if (denied is not null)
        {
            return denied;
        }

        device!.RemoteControlAllowed = remoteControlAllowed;

        // Three settings, deliberately ordered so the most cautious one wins. An owner who
        // has ticked "never switch this on from away" has said something specific about a
        // specific appliance, and no default about device types should be able to override
        // that. Below it, an explicit "treat as safe" drops the surroundings check; with
        // neither ticked the device type decides, which for anything that heats means
        // Guarded rather than blocked outright.
        var classified = DeviceSafetyPolicy.Classify(device.DeviceType);
        device.SafetyClass = blockRemoteTurnOn
            ? SafetyClass.Restricted
            : treatAsSafeAppliance || classified == SafetyClass.Safe
                ? SafetyClass.Safe
                : classified;

        await db.SaveChangesAsync(ct);

        var message = device.RemoteControlAllowed
            ? device.SafetyClass switch
            {
                SafetyClass.Safe => "遠隔操作を許可しました。「つける」「消す」の両方が使えます。",
                SafetyClass.Guarded =>
                    "遠隔操作を許可しました。つけるときは周囲の安全を確認したうえで実行し、ご家族全員にお知らせが届きます。",
                _ => "遠隔操作を許可しました。安全のため「消す」のみ使えます。"
            }
            : "遠隔操作を禁止しました。この機器は画面からもAIからも操作できません。";

        return new DeviceSettingsUpdateResult(
            DeviceSettingsUpdateStatus.Updated,
            device.RemoteControlAllowed,
            device.SafetyClass.ToString(),
            message);
    }

    private async Task<(Device? Device, DeviceSettingsUpdateResult? Denied)> ResolveEditableAsync(
        Guid deviceId,
        CancellationToken ct)
    {
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null || !await householdAccess.CanAccessAsync(device.HouseholdId, ct))
        {
            // Same response for "missing" and "forbidden" so callers cannot probe for
            // the existence of devices in households they cannot see.
            return (null, new DeviceSettingsUpdateResult(
                DeviceSettingsUpdateStatus.NotFoundOrDenied,
                false,
                nameof(SafetyClass.Restricted),
                "この機器は見つからないか、変更する権限がありません。"));
        }

        var mode = await db.Households
            .Where(h => h.Id == device.HouseholdId)
            .Select(h => h.DataSourceMode)
            .FirstAsync(ct);

        // Sample households are shared demo data that any visitor can view, so allowing
        // edits there would let one visitor change what everyone else sees.
        if (mode == DataSourceMode.Sample)
        {
            return (device, new DeviceSettingsUpdateResult(
                DeviceSettingsUpdateStatus.SampleHouseholdNotEditable,
                device.RemoteControlAllowed,
                device.SafetyClass.ToString(),
                "デモデータの機器は変更できません。"));
        }

        return (device, null);
    }
}

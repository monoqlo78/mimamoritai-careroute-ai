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
    /// Renames a device. The name matters functionally, not just cosmetically:
    /// <see cref="DeviceResolver"/> matches spoken and typed phrases against Alias/Name, so a
    /// device still carrying its vendor label ("プラグミニ 92") can never be reached by asking
    /// for "電気". Renaming it is what makes natural language work at all.
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

        newName = newName.Trim();
        if (newName.Length is 0 or > 60)
        {
            return new DeviceSettingsUpdateResult(
                DeviceSettingsUpdateStatus.InvalidName,
                device!.RemoteControlAllowed,
                device.SafetyClass.ToString(),
                "呼び名は1〜60文字で入力してください。");
        }

        // Alias is what DeviceResolver matches first, so both must move together;
        // leaving Alias behind would make the rename look applied but change nothing.
        device!.Name = newName;
        device.Alias = newName;
        await db.SaveChangesAsync(ct);

        return new DeviceSettingsUpdateResult(
            DeviceSettingsUpdateStatus.Updated,
            device.RemoteControlAllowed,
            device.SafetyClass.ToString(),
            $"呼び名を「{newName}」に変更しました。この名前で話しかけると操作できます。");
    }

    public async Task<DeviceSettingsUpdateResult> UpdatePermissionsAsync(
        Guid deviceId,
        bool remoteControlAllowed,
        bool treatAsSafeAppliance,
        CancellationToken ct = default)
    {
        var (device, denied) = await ResolveEditableAsync(deviceId, ct);
        if (denied is not null)
        {
            return denied;
        }

        device!.RemoteControlAllowed = remoteControlAllowed;

        // Never widen beyond what the device type itself allows: if the type is already
        // classified Safe, keep it Safe. Restricted types are only relaxed by explicit
        // owner consent, and withdrawing consent restores the type's own classification.
        var classified = DeviceSafetyPolicy.Classify(device.DeviceType);
        device.SafetyClass = treatAsSafeAppliance || classified == SafetyClass.Safe
            ? SafetyClass.Safe
            : classified;

        await db.SaveChangesAsync(ct);

        var message = device.RemoteControlAllowed
            ? device.SafetyClass == SafetyClass.Safe
                ? "遠隔操作を許可しました。「つける」「消す」の両方が使えます。"
                : "遠隔操作を許可しました。安全のため「消す」のみ使えます。"
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

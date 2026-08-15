using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>Outcome of a single <see cref="DeviceSyncService"/> run, surfaced to the UI.</summary>
public sealed record DeviceSyncResult(int Added, int Updated, int Deactivated)
{
    public int TotalChanges => Added + Updated + Deactivated;
}

/// <summary>
/// Upserts <see cref="IDeviceProvider.GetDevicesAsync"/> results into the Devices
/// table, keyed by ExternalDeviceId, so real hardware (e.g. SwitchBot) shows up in
/// the dashboard, the safety policy, and the alert engine exactly like demo devices.
///
/// - New provider devices are inserted (Restricted by default; an operator must
///   opt a device into RemoteControlAllowed separately -- sync never grants remote
///   control on its own).
/// - Existing devices (matched by ExternalDeviceId + HouseholdId) have their
///   Name/DeviceType/Room refreshed and are reactivated if they had been deactivated.
///   Name/Room here are the PROVIDER's own values; the family's own
///   DisplayNameOverride/RoomOverride are separate columns sync never touches, so a
///   correction typed on screen survives every subsequent poll.
/// - Devices previously synced from the provider but no longer reported by it are
///   marked IsActive = false (never deleted), so their historical events/commands
///   remain valid and they naturally disappear from active device resolution.
///   Callers that cannot be sure a missing device is a real removal (rather than a
///   transient API hiccup dropping it from one response) may pass
///   <paramref name="deactivateMissing"/> = false to skip this step entirely -- see
///   SwitchBotPollingBackgroundService's periodic auto-discovery for exactly that case.
/// Running this twice in a row with an unchanged provider device list is a no-op.
/// </summary>
public sealed class DeviceSyncService(IAppDbContext db, IDeviceProvider provider, TimeProvider clock)
{
    public async Task<DeviceSyncResult> SyncAsync(Guid householdId, bool deactivateMissing = true, CancellationToken ct = default)
    {
        var providerDevices = await provider.GetDevicesAsync(ct);

        var existing = await db.Devices
            .Where(d => d.HouseholdId == householdId && d.Provider == provider.Kind)
            .ToListAsync(ct);

        var existingById = existing.ToDictionary(d => d.ExternalDeviceId, StringComparer.Ordinal);
        var seenExternalIds = new HashSet<string>(StringComparer.Ordinal);

        var added = 0;
        var updated = 0;

        foreach (var pd in providerDevices)
        {
            seenExternalIds.Add(pd.ExternalDeviceId);

            if (existingById.TryGetValue(pd.ExternalDeviceId, out var device))
            {
                var changed = false;

                if (device.Name != pd.Name)
                {
                    device.Name = pd.Name;
                    changed = true;
                }

                if (device.DeviceType != pd.DeviceType)
                {
                    device.DeviceType = pd.DeviceType;
                    device.SafetyClass = DeviceSafetyPolicy.Classify(pd.DeviceType);
                    changed = true;
                }

                if (device.Room != pd.Room)
                {
                    device.Room = pd.Room;
                    changed = true;
                }

                if (!device.IsActive)
                {
                    device.IsActive = true;
                    changed = true;
                }

                if (changed)
                {
                    updated++;
                }
            }
            else
            {
                db.Devices.Add(new Device
                {
                    HouseholdId = householdId,
                    ExternalDeviceId = pd.ExternalDeviceId,
                    Name = pd.Name,
                    Alias = pd.Name,
                    DeviceType = pd.DeviceType,
                    Room = pd.Room,
                    Provider = provider.Kind,
                    IsEnabled = true,
                    // Sync only ever discovers devices; it never grants remote control.
                    // An operator must explicitly allow remote control per device.
                    RemoteControlAllowed = false,
                    SafetyClass = DeviceSafetyPolicy.Classify(pd.DeviceType),
                    IsActive = true,
                    CreatedAtUtc = clock.GetUtcNow()
                });
                added++;
            }
        }

        var deactivated = 0;
        if (deactivateMissing)
        {
            foreach (var device in existing)
            {
                if (device.IsActive && !seenExternalIds.Contains(device.ExternalDeviceId))
                {
                    device.IsActive = false;
                    deactivated++;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        return new DeviceSyncResult(added, updated, deactivated);
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MimamoriTai.Core.Abstractions;
using MimamoriTai.Core.Domain;

namespace MimamoriTai.Core.Application;

/// <summary>
/// What one webhook callback produced. Nothing is not a failure: SwitchBot delivers
/// callbacks for every device on the account, most of which belong to nobody here.
/// </summary>
public sealed record SwitchBotWebhookResult(
    bool Recognised,
    DeviceEvent? StateChange = null,
    PlugMiniReading? Reading = null)
{
    public static readonly SwitchBotWebhookResult Ignored = new(false);
}

/// <summary>
/// Ingests a SwitchBot webhook callback -- the push counterpart to
/// <see cref="SwitchBotPollingCycleService"/>.
///
/// Polling cannot tell a steady house from a silent one. SwitchBot's cloud answers a
/// status request with the last report it received from the device, so once a plug
/// stops talking the polls keep succeeding and keep returning the same numbers:
/// production spent ten hours storing 103.4V every five minutes because of this. A
/// callback is only sent when the device actually reports, so silence stays silent and
/// arrives as a gap rather than as a confident flat line.
///
/// This is deliberately additive. Polling stays exactly as it was, because the callback
/// is not a superset: SwitchBot only pushes on change, so a plug whose draw never moves
/// may say nothing for hours, and the account-wide webhook is a single URL that is easy
/// to have pointed elsewhere. Whichever arrives first wins and the other de-duplicates
/// against it -- both write through the same (household, device, timestamp) uniqueness
/// the poller already relies on.
///
/// Field names and units are taken to match the status API exactly, because they are
/// the same names: <c>voltage</c> volts, <c>electricCurrent</c> milliamps,
/// <c>weight</c> the plug's own real power in watts, <c>electricityOfDay</c> minutes
/// of use. Every one of them is optional here and simply skipped when absent -- the
/// official specification documents the envelope and only publishes worked examples
/// for Bot and Curtain, so a callback that carries only <c>powerState</c> must record
/// the state change and nothing more rather than inventing a measurement. Never
/// compute watts from volts times amps: that is apparent power, and quoting it once
/// already told a family a lamp was on while it drew nothing.
/// </summary>
public sealed class SwitchBotWebhookIngestService(IAppDbContext db, TimeProvider clock)
{
    /// <summary>
    /// SwitchBot writes the MAC with separators in some payloads and without in others,
    /// and devices are registered here by the id the status API returns, which is the
    /// bare hex. Compare on the hex alone so a colon does not silently drop telemetry.
    /// </summary>
    private static string Normalise(string? mac) =>
        new(mac?.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray() ?? []);

    public async Task<SwitchBotWebhookResult> IngestAsync(string body, CancellationToken ct = default)
    {
        JsonElement root;
        try
        {
            using var parsed = JsonDocument.Parse(body);
            root = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            return SwitchBotWebhookResult.Ignored;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("context", out var context)
            || context.ValueKind != JsonValueKind.Object)
        {
            return SwitchBotWebhookResult.Ignored;
        }

        var mac = Normalise(String(context, "deviceMac"));
        if (mac.Length == 0)
        {
            return SwitchBotWebhookResult.Ignored;
        }

        // The account may own devices from other households, or none of ours at all.
        var candidates = await db.Devices
            .Where(d => d.Provider == DeviceProviderKind.SwitchBot && d.IsActive)
            .ToListAsync(ct);

        var device = candidates.FirstOrDefault(d => Normalise(d.ExternalDeviceId) == mac);
        if (device is null)
        {
            return SwitchBotWebhookResult.Ignored;
        }

        var now = clock.GetUtcNow();
        var observedAt = SampledAt(context) ?? now;

        var stateChange = await StateChangeAsync(device, context, observedAt, now, ct);
        var reading = await ReadingAsync(device, context, observedAt, now, ct);

        if (stateChange is not null || reading is not null)
        {
            await db.SaveChangesAsync(ct);
        }

        return new SwitchBotWebhookResult(true, stateChange, reading);
    }

    /// <summary>
    /// <c>timeOfSample</c> is when the device reported, which is the only timestamp worth
    /// storing: the moment of arrival says more about the network than about the house.
    /// SwitchBot has shipped it in both seconds and milliseconds, so pick by magnitude
    /// rather than trusting either -- reading milliseconds as seconds lands the sample
    /// tens of thousands of years from now and would silently poison every chart range.
    /// </summary>
    private DateTimeOffset? SampledAt(JsonElement context)
    {
        if (!context.TryGetProperty("timeOfSample", out var raw)
            || !raw.TryGetInt64(out var value)
            || value <= 0)
        {
            return null;
        }

        var at = value > 100_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(value)
            : DateTimeOffset.FromUnixTimeSeconds(value);

        // A clock that disagrees with ours by more than a day is not a timestamp we can
        // place on a timeline; fall back to arrival rather than drawing it in the future.
        return Math.Abs((at - clock.GetUtcNow()).TotalDays) > 1 ? null : at;
    }

    private async Task<DeviceEvent?> StateChangeAsync(
        Device device, JsonElement context, DateTimeOffset observedAt, DateTimeOffset now, CancellationToken ct)
    {
        // "powerState" on plugs, "power" on the Bot. Both carry on/off.
        var state = String(context, "powerState") ?? String(context, "power");
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        state = state.ToLowerInvariant() switch { "on" => "on", "off" => "off", _ => state };

        var last = await db.DeviceEvents
            .Where(e => e.DeviceId == device.Id && e.EventType == "PowerState")
            .OrderByDescending(e => e.OccurredAtUtc)
            .FirstOrDefaultAsync(ct);

        // The poller applies the same guard, so a callback that merely confirms what a
        // poll already recorded adds nothing.
        if (last is not null && string.Equals(last.State, state, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var deviceEvent = new DeviceEvent
        {
            HouseholdId = device.HouseholdId,
            DeviceId = device.Id,
            EventType = "PowerState",
            State = state,
            PowerWatts = Number(context, "weight"),
            Source = EventSource.SwitchBotWebhook,
            OccurredAtUtc = observedAt,
            ReceivedAtUtc = now
        };

        db.DeviceEvents.Add(deviceEvent);
        return deviceEvent;
    }

    private async Task<PlugMiniReading?> ReadingAsync(
        Device device, JsonElement context, DateTimeOffset observedAt, DateTimeOffset now, CancellationToken ct)
    {
        var volts = Number(context, "voltage");
        var milliamps = Number(context, "electricCurrent");
        var watts = Number(context, "weight");
        var usageMinutes = Number(context, "electricityOfDay");

        // A state-only callback is the documented shape for several devices. Storing a
        // row of nulls for it would manufacture an observation nobody made.
        if (volts is null && milliamps is null && watts is null && usageMinutes is null)
        {
            return null;
        }

        var alreadyStored = await db.PlugMiniReadings.AnyAsync(
            r => r.DeviceId == device.Id && r.OccurredAtUtc == observedAt, ct);
        if (alreadyStored)
        {
            return null;
        }

        var reading = new PlugMiniReading
        {
            HouseholdId = device.HouseholdId,
            DeviceId = device.Id,
            VoltageV = volts,
            CurrentMa = milliamps,
            DailyEnergyWh = watts,
            UsageMinutesToday = usageMinutes is null ? null : (int)Math.Round(usageMinutes.Value),
            ApproxWatts = volts is null || milliamps is null ? null : volts * milliamps / 1000.0,
            OccurredAtUtc = observedAt,
            ReceivedAtUtc = now
        };

        db.PlugMiniReadings.Add(reading);
        return reading;
    }

    private static string? String(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// SwitchBot has quoted numerics as strings in past payloads, so accept both rather
    /// than dropping a measurement over its JSON type.
    /// </summary>
    private static double? Number(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(
                value.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null
        };
    }
}

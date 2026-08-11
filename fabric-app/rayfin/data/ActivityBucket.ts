import { entity, role, text, date, uuid } from '@microsoft/rayfin-core';

/**
 * Hourly rollup of device activity, mirrored from mimamori.DeviceEvents.
 *
 * The console needs a dense time series to show a household's living rhythm,
 * but raw events are personal telemetry. This table therefore stores counts
 * only -- never a raw payload, never a resident name -- bucketed to the hour.
 *
 * One row per (household, device, hour) that actually had activity; empty hours
 * are simply absent and the client fills the gaps.
 */
@entity()
@role('authenticated', 'read')
export class ActivityBucket {
  @uuid() id!: string;

  /** Household.Id in the 見守り隊 application database. */
  @text() householdId!: string;

  @text({ max: 200 }) householdName!: string;

  /** Device display name, e.g. "リビング照明". Not a resident identifier. */
  @text({ max: 200 }) deviceName!: string;

  /** "Light" | "Plug" | "Fan" | "Heater" | ... mirrors Device.DeviceType. */
  @text({ max: 40 }) deviceType!: string;

  /** Start of the UTC hour this bucket covers. */
  @date() bucketStart!: Date;

  /** Events observed in the bucket. */
  @text({ max: 10 }) eventCount!: string;

  /** Subset of `eventCount` that switched a device on -- the "activity" signal. */
  @text({ max: 10 }) onCount!: string;

  /** Ingestion origin: "Seed" | "SwitchBotPoll" | "AppCommand" | "Simulator". */
  @text({ max: 40 }) source!: string;
}

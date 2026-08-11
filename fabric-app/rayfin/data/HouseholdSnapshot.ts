import { entity, role, text, boolean, date, uuid } from '@microsoft/rayfin-core';

/**
 * One household's operational health as of the last push from the 見守り隊
 * (.NET / Blazor) application. This is a *snapshot* table, not a copy of the
 * operational database: the Blazor app remains the system of record for
 * residents, devices and events, and pushes only the non-personal counters an
 * operator needs to triage. No resident name, address, device payload or
 * credential is ever mirrored here.
 *
 * `householdId` is the Blazor-side Household.Id, so an operator can jump from
 * this console straight to the corresponding household in the main app.
 */
@entity()
@role('authenticated', 'read')
export class HouseholdSnapshot {
  @uuid() id!: string;

  /** Household.Id in the 見守り隊 application database. */
  @text() householdId!: string;

  @text({ min: 1, max: 200 }) name!: string;

  /** "Sample" (demo dataset) or "Production". Mirrors DataSourceMode. */
  @text({ max: 20 }) dataSourceMode!: string;

  @text({ max: 10 }) memberCount!: string;
  @text({ max: 10 }) residentCount!: string;
  @text({ max: 10 }) deviceCount!: string;

  /** ISO-8601 timestamp of the newest device event, or empty when never seen. */
  @text({ max: 40 }) lastEventUtc!: string;

  /** "NotConfigured" | "Connected" | "Error". Empty when no connection row exists. */
  @text({ max: 20 }) switchBotStatus!: string;

  /** Human-readable failure summary only -- never a token or secret. */
  @text({ max: 500 }) switchBotError!: string;

  @text({ max: 10 }) activeLineRecipients!: string;
  @text({ max: 10 }) alertsInWindow!: string;
  @text({ max: 10 }) failedAlertsInWindow!: string;

  /** "Low" | "Medium" | "High", or empty when no assessment exists yet. */
  @text({ max: 10 }) latestRiskLevel!: string;

  /** True when an operator has to act: see AdminConsoleService.NeedsAttention. */
  @boolean() needsAttention!: boolean;

  /** When the Blazor app produced this snapshot. */
  @date() capturedAt!: Date;
}

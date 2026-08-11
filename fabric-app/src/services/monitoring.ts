import { getRayfinClient, isLocalBackend } from './rayfinClient';

export interface HouseholdRow {
  id: string;
  householdId: string;
  name: string;
  dataSourceMode: string;
  memberCount: string;
  residentCount: string;
  deviceCount: string;
  lastEventUtc: string;
  switchBotStatus: string;
  switchBotError: string;
  activeLineRecipients: string;
  alertsInWindow: string;
  failedAlertsInWindow: string;
  latestRiskLevel: string;
  needsAttention: boolean;
  capturedAt: Date;
}

export interface AlertRow {
  id: string;
  householdId: string;
  householdName: string;
  riskLevel: string;
  score: string;
  reason: string;
  success: boolean;
  error: string;
  sentAt: Date;
}

const HOUSEHOLD_FIELDS = [
  'id',
  'householdId',
  'name',
  'dataSourceMode',
  'memberCount',
  'residentCount',
  'deviceCount',
  'lastEventUtc',
  'switchBotStatus',
  'switchBotError',
  'activeLineRecipients',
  'alertsInWindow',
  'failedAlertsInWindow',
  'latestRiskLevel',
  'needsAttention',
  'capturedAt',
] as const;

const ALERT_FIELDS = [
  'id',
  'householdId',
  'householdName',
  'riskLevel',
  'score',
  'reason',
  'success',
  'error',
  'sentAt',
] as const;

// Local-dev fallback. `rayfin up` has not provisioned a Fabric SQL database
// yet when running purely on localhost, so the console renders a small fixture
// that mirrors the shape the Blazor app pushes. This keeps the UI reviewable
// before a Fabric capacity is available -- it is never used once
// VITE_RAYFIN_API_URL points at a deployed backend.
const SAMPLE_HOUSEHOLDS: HouseholdRow[] = [
  {
    id: '00000000-0000-0000-0000-000000000001',
    householdId: '11111111-1111-1111-1111-111111111111',
    name: 'サンプル家族',
    dataSourceMode: 'Sample',
    memberCount: '1',
    residentCount: '1',
    deviceCount: '4',
    lastEventUtc: new Date(Date.now() - 20 * 60_000).toISOString(),
    switchBotStatus: 'NotConfigured',
    switchBotError: '',
    activeLineRecipients: '0',
    alertsInWindow: '0',
    failedAlertsInWindow: '0',
    latestRiskLevel: 'Low',
    needsAttention: false,
    capturedAt: new Date(),
  },
  {
    id: '00000000-0000-0000-0000-000000000002',
    householdId: '22222222-2222-2222-2222-222222222222',
    name: '田中家',
    dataSourceMode: 'Production',
    memberCount: '3',
    residentCount: '1',
    deviceCount: '6',
    lastEventUtc: new Date(Date.now() - 9 * 3600_000).toISOString(),
    switchBotStatus: 'Error',
    switchBotError: 'SwitchBot API returned 401 (token may have been revoked)',
    activeLineRecipients: '2',
    alertsInWindow: '3',
    failedAlertsInWindow: '1',
    latestRiskLevel: 'High',
    needsAttention: true,
    capturedAt: new Date(),
  },
];

const SAMPLE_ALERTS: AlertRow[] = [
  {
    id: '00000000-0000-0000-0000-0000000000a1',
    householdId: '22222222-2222-2222-2222-222222222222',
    householdName: '田中家',
    riskLevel: 'High',
    score: '82',
    reason: '長時間の無反応',
    success: false,
    error: 'LINE push failed (429 rate limited)',
    sentAt: new Date(Date.now() - 3 * 3600_000),
  },
  {
    id: '00000000-0000-0000-0000-0000000000a2',
    householdId: '22222222-2222-2222-2222-222222222222',
    householdName: '田中家',
    riskLevel: 'Medium',
    score: '48',
    reason: '深夜の活動増加',
    success: true,
    error: '',
    sentAt: new Date(Date.now() - 26 * 3600_000),
  },
];

export async function getHouseholds(): Promise<HouseholdRow[]> {
  if (isLocalBackend()) {
    return sortHouseholds([...SAMPLE_HOUSEHOLDS]);
  }

  const client = getRayfinClient();
  const results = await client.data.HouseholdSnapshot.select([
    ...HOUSEHOLD_FIELDS,
  ]).execute();

  return sortHouseholds(results as unknown as HouseholdRow[]);
}

export async function getAlerts(limit = 50): Promise<AlertRow[]> {
  if (isLocalBackend()) {
    return [...SAMPLE_ALERTS]
      .sort((a, b) => b.sentAt.getTime() - a.sentAt.getTime())
      .slice(0, limit);
  }

  const client = getRayfinClient();
  const results = await client.data.AlertRecord.select([...ALERT_FIELDS])
    .orderBy({ sentAt: 'desc' })
    .first(limit)
    .execute();

  return results as unknown as AlertRow[];
}

/** Households needing attention first, then by name, so triage is the default view. */
export function sortHouseholds(rows: HouseholdRow[]): HouseholdRow[] {
  return rows.sort((a, b) => {
    if (a.needsAttention !== b.needsAttention) return a.needsAttention ? -1 : 1;
    return a.name.localeCompare(b.name, 'ja');
  });
}

export interface ConsoleTotals {
  households: number;
  production: number;
  devices: number;
  alerts: number;
  failedAlerts: number;
  needingAttention: number;
}

export function summarize(rows: HouseholdRow[]): ConsoleTotals {
  const toInt = (value: string) => {
    const parsed = Number.parseInt(value, 10);
    return Number.isNaN(parsed) ? 0 : parsed;
  };

  return {
    households: rows.length,
    production: rows.filter((r) => r.dataSourceMode === 'Production').length,
    devices: rows.reduce((sum, r) => sum + toInt(r.deviceCount), 0),
    alerts: rows.reduce((sum, r) => sum + toInt(r.alertsInWindow), 0),
    failedAlerts: rows.reduce(
      (sum, r) => sum + toInt(r.failedAlertsInWindow),
      0
    ),
    needingAttention: rows.filter((r) => r.needsAttention).length,
  };
}

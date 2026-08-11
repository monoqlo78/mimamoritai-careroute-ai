import { getRayfinClient, isLocalBackend } from './rayfinClient';
import {
  SNAPSHOT_ACTIVITY,
  SNAPSHOT_ALERTS,
  SNAPSHOT_CAPTURED_AT,
  SNAPSHOT_HOUSEHOLDS,
} from './snapshotFallback';

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

export interface ActivityRow {
  id: string;
  householdId: string;
  householdName: string;
  deviceName: string;
  deviceType: string;
  bucketStart: Date;
  eventCount: string;
  onCount: string;
  source: string;
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

const ACTIVITY_FIELDS = [
  'id',
  'householdId',
  'householdName',
  'deviceName',
  'deviceType',
  'bucketStart',
  'eventCount',
  'onCount',
  'source',
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

// Local-dev sample activity: a synthetic two-week rhythm so the charts have
// shape before a Fabric backend exists. Deliberately labelled `Sample` so it is
// distinguishable from the SwitchBotPoll/AppCommand sources of real data.
const SAMPLE_ACTIVITY: ActivityRow[] = (() => {
  const rows: ActivityRow[] = [];
  const devices = [
    { name: 'リビング照明', type: 'Light' },
    { name: '寝室照明', type: 'Light' },
    { name: '扇風機', type: 'Fan' },
  ];
  // Rough diurnal weighting: quiet at night, busy morning / evening.
  const weights = [
    0, 0, 1, 2, 1, 0, 0, 3, 4, 2, 1, 1, 1, 2, 3, 1, 1, 1, 2, 3, 4, 5, 6, 2,
  ];
  const start = new Date();
  start.setUTCHours(0, 0, 0, 0);
  for (let day = 13; day >= 0; day -= 1) {
    for (let hour = 0; hour < 24; hour += 1) {
      const count = weights[hour];
      if (count === 0) continue;
      const device = devices[(day + hour) % devices.length];
      const bucket = new Date(start);
      bucket.setUTCDate(bucket.getUTCDate() - day);
      bucket.setUTCHours(hour);
      rows.push({
        id: `sample-${day}-${hour}`,
        householdId: '22222222-2222-2222-2222-222222222222',
        householdName: '田中家',
        deviceName: device.name,
        deviceType: device.type,
        bucketStart: bucket,
        eventCount: String(count),
        onCount: String(Math.ceil(count / 2)),
        source: 'Sample',
      });
    }
  }
  return rows;
})();

/**
 * How the rows on screen were obtained. The console must never imply a live
 * Fabric read when it is actually serving the bundled snapshot, so the UI reads
 * this and says so.
 */
export type DataOrigin = 'fabric' | 'snapshot' | 'sample';

let dataOrigin: DataOrigin = 'fabric';

export function getDataOrigin(): DataOrigin {
  return dataOrigin;
}

export const SNAPSHOT_TAKEN_AT = SNAPSHOT_CAPTURED_AT;

/**
 * Reads from Fabric, but degrades to the bundled production snapshot when the
 * backend is unreachable (typically because the Fabric capacity is paused) or
 * returns nothing at all. An empty result is treated as unavailable so the
 * console shows real history rather than a blank chart.
 */
async function withSnapshotFallback<T>(
  snapshot: T[],
  read: () => Promise<T[]>
): Promise<T[]> {
  try {
    const rows = await read();
    if (rows.length > 0) {
      if (dataOrigin !== 'snapshot') dataOrigin = 'fabric';
      return rows;
    }
  } catch (error) {
    console.warn('Fabric read failed; falling back to the bundled snapshot', error);
  }

  dataOrigin = 'snapshot';
  return snapshot.map((row) => ({ ...row }));
}

export async function getHouseholds(): Promise<HouseholdRow[]> {
  if (isLocalBackend()) {
    dataOrigin = 'sample';
    return sortHouseholds([...SAMPLE_HOUSEHOLDS]);
  }

  return withSnapshotFallback(SNAPSHOT_HOUSEHOLDS, async () => {
    const client = getRayfinClient();
    const results = await client.data.HouseholdSnapshot.select([
      ...HOUSEHOLD_FIELDS,
    ]).execute();

    return results as unknown as HouseholdRow[];
  }).then(sortHouseholds);
}

export async function getAlerts(limit = 50): Promise<AlertRow[]> {
  if (isLocalBackend()) {
    dataOrigin = 'sample';
    return [...SAMPLE_ALERTS]
      .sort((a, b) => b.sentAt.getTime() - a.sentAt.getTime())
      .slice(0, limit);
  }

  const rows = await withSnapshotFallback(SNAPSHOT_ALERTS, async () => {
    const client = getRayfinClient();
    const results = await client.data.AlertRecord.select([...ALERT_FIELDS])
      .orderBy({ sentAt: 'desc' })
      .first(limit)
      .execute();

    return results as unknown as AlertRow[];
  });

  return rows
    .slice()
    .sort((a, b) => new Date(b.sentAt).getTime() - new Date(a.sentAt).getTime())
    .slice(0, limit);
}

/** Hourly device-activity buckets, oldest first, for the timeline charts. */
export async function getActivity(limit = 2000): Promise<ActivityRow[]> {
  if (isLocalBackend()) {
    dataOrigin = 'sample';
    return [...SAMPLE_ACTIVITY].slice(-limit);
  }

  const rows = await withSnapshotFallback(SNAPSHOT_ACTIVITY, async () => {
    const client = getRayfinClient();
    const results = await client.data.ActivityBucket.select([...ACTIVITY_FIELDS])
      .orderBy({ bucketStart: 'desc' })
      .first(limit)
      .execute();

    return results as unknown as ActivityRow[];
  });

  return rows
    .slice(-limit)
    .sort(
      (a, b) => new Date(a.bucketStart).getTime() - new Date(b.bucketStart).getTime()
    );
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

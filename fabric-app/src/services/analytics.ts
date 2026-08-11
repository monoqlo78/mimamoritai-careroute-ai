import type { ActivityRow, AlertRow, HouseholdRow } from './monitoring';

/** Rayfin returns datetimes as `Date`, but a rehydrated JSON payload may hand back a string. */
export function toDate(value: Date | string): Date {
  return value instanceof Date ? value : new Date(value);
}

function toInt(value: string): number {
  const parsed = Number.parseInt(value, 10);
  return Number.isNaN(parsed) ? 0 : parsed;
}

export interface DayBucket {
  /** Local midnight of the bucket. */
  date: Date;
  label: string;
  total: number;
  failed: number;
}

/**
 * Buckets alerts into the last `days` local days, oldest first. Empty days are
 * kept so the timeline keeps a constant width regardless of activity.
 */
export function alertsByDay(alerts: AlertRow[], days = 7, now = new Date()): DayBucket[] {
  const buckets: DayBucket[] = [];
  const base = new Date(now.getFullYear(), now.getMonth(), now.getDate());

  for (let offset = days - 1; offset >= 0; offset -= 1) {
    const date = new Date(base);
    date.setDate(base.getDate() - offset);
    buckets.push({
      date,
      label: `${date.getMonth() + 1}/${date.getDate()}`,
      total: 0,
      failed: 0,
    });
  }

  const firstMs = buckets[0].date.getTime();

  for (const alert of alerts) {
    const sent = toDate(alert.sentAt);
    if (Number.isNaN(sent.getTime())) continue;

    const sentDay = new Date(sent.getFullYear(), sent.getMonth(), sent.getDate());
    const index = Math.round((sentDay.getTime() - firstMs) / 86_400_000);
    if (index < 0 || index >= buckets.length) continue;

    buckets[index].total += 1;
    if (!alert.success) buckets[index].failed += 1;
  }

  return buckets;
}

export interface RiskSlice {
  level: 'High' | 'Medium' | 'Low' | 'Unknown';
  label: string;
  count: number;
  color: string;
}

const RISK_ORDER: RiskSlice[] = [
  { level: 'High', label: '高', count: 0, color: '#dc2626' },
  { level: 'Medium', label: '中', count: 0, color: '#f59e0b' },
  { level: 'Low', label: '低', count: 0, color: '#10b981' },
  { level: 'Unknown', label: '不明', count: 0, color: '#cbd5e1' },
];

export function riskDistribution(alerts: AlertRow[]): RiskSlice[] {
  const slices = RISK_ORDER.map((slice) => ({ ...slice }));

  for (const alert of alerts) {
    const match = slices.find((slice) => slice.level === alert.riskLevel);
    (match ?? slices[slices.length - 1]).count += 1;
  }

  return slices.filter((slice) => slice.count > 0);
}

export interface DeliveryStats {
  total: number;
  success: number;
  failed: number;
  /** 0–100, rounded. `100` when there is nothing to deliver. */
  successRate: number;
}

export function deliveryStats(alerts: AlertRow[]): DeliveryStats {
  const total = alerts.length;
  const failed = alerts.filter((alert) => !alert.success).length;
  const success = total - failed;

  return {
    total,
    success,
    failed,
    successRate: total === 0 ? 100 : Math.round((success / total) * 100),
  };
}

export interface HouseholdBar {
  id: string;
  name: string;
  devices: number;
  alerts: number;
  failed: number;
  needsAttention: boolean;
}

export function householdBars(rows: HouseholdRow[]): HouseholdBar[] {
  return rows
    .map((row) => ({
      id: row.id,
      name: row.name || '(名称未設定)',
      devices: toInt(row.deviceCount),
      alerts: toInt(row.alertsInWindow),
      failed: toInt(row.failedAlertsInWindow),
      needsAttention: row.needsAttention,
    }))
    .sort((a, b) => b.devices - a.devices || a.name.localeCompare(b.name, 'ja'));
}

export interface ActivityPoint {
  /** UTC midnight of the day. */
  date: Date;
  label: string;
  events: number;
  onEvents: number;
}

/**
 * Daily totals across whatever window the buckets actually cover. The range is
 * taken from the data rather than "the last N days from now" so a historical
 * export still renders a continuous line instead of a flat empty chart.
 * Days with no events are filled with zero to keep the x-axis linear in time.
 */
export function dailyActivity(buckets: ActivityRow[], maxDays = 30): ActivityPoint[] {
  if (buckets.length === 0) return [];

  const byDay = new Map<number, ActivityPoint>();
  let min = Infinity;
  let max = -Infinity;

  for (const bucket of buckets) {
    const start = toDate(bucket.bucketStart);
    if (Number.isNaN(start.getTime())) continue;

    const key = Date.UTC(
      start.getUTCFullYear(),
      start.getUTCMonth(),
      start.getUTCDate()
    );
    min = Math.min(min, key);
    max = Math.max(max, key);

    const point = byDay.get(key);
    if (point) {
      point.events += toInt(bucket.eventCount);
      point.onEvents += toInt(bucket.onCount);
    } else {
      const date = new Date(key);
      byDay.set(key, {
        date,
        label: `${date.getUTCMonth() + 1}/${date.getUTCDate()}`,
        events: toInt(bucket.eventCount),
        onEvents: toInt(bucket.onCount),
      });
    }
  }

  if (!Number.isFinite(min)) return [];

  const oldestAllowed = max - (maxDays - 1) * 86_400_000;
  const from = Math.max(min, oldestAllowed);
  const points: ActivityPoint[] = [];

  for (let key = from; key <= max; key += 86_400_000) {
    const date = new Date(key);
    points.push(
      byDay.get(key) ?? {
        date,
        label: `${date.getUTCMonth() + 1}/${date.getUTCDate()}`,
        events: 0,
        onEvents: 0,
      }
    );
  }

  return points;
}

export interface HeatmapCell {
  hour: number;
  events: number;
}

/**
 * 24-slot histogram of when devices actually report, in the household's local
 * time (JST). This is the "living rhythm" view -- the shape is the point, so it
 * aggregates every day rather than showing one row per date.
 */
export function hourlyRhythm(buckets: ActivityRow[], utcOffsetHours = 9): HeatmapCell[] {
  const cells: HeatmapCell[] = Array.from({ length: 24 }, (_, hour) => ({
    hour,
    events: 0,
  }));

  for (const bucket of buckets) {
    const start = toDate(bucket.bucketStart);
    if (Number.isNaN(start.getTime())) continue;
    const hour = (start.getUTCHours() + utcOffsetHours + 24) % 24;
    cells[hour].events += toInt(bucket.eventCount);
  }

  return cells;
}

export interface DeviceSlice {
  name: string;
  type: string;
  events: number;
}

/** Per-device event totals, busiest first, for the contribution bars. */
export function deviceBreakdown(buckets: ActivityRow[], limit = 8): DeviceSlice[] {
  const byDevice = new Map<string, DeviceSlice>();

  for (const bucket of buckets) {
    const name = bucket.deviceName || '(不明な機器)';
    const slice = byDevice.get(name);
    if (slice) {
      slice.events += toInt(bucket.eventCount);
    } else {
      byDevice.set(name, {
        name,
        type: bucket.deviceType || '-',
        events: toInt(bucket.eventCount),
      });
    }
  }

  return [...byDevice.values()]
    .sort((a, b) => b.events - a.events || a.name.localeCompare(b.name, 'ja'))
    .slice(0, limit);
}

export interface ActivitySummary {
  events: number;
  buckets: number;
  devices: number;
  days: number;
  /** Distinct ingestion sources seen (`SwitchBotPoll`, `AppCommand`, ...). */
  sources: string[];
  from: Date | null;
  to: Date | null;
}

export function activitySummary(buckets: ActivityRow[]): ActivitySummary {
  const devices = new Set<string>();
  const days = new Set<number>();
  const sources = new Set<string>();
  let events = 0;
  let from: Date | null = null;
  let to: Date | null = null;

  for (const bucket of buckets) {
    events += toInt(bucket.eventCount);
    if (bucket.deviceName) devices.add(bucket.deviceName);
    if (bucket.source) sources.add(bucket.source);

    const start = toDate(bucket.bucketStart);
    if (Number.isNaN(start.getTime())) continue;
    days.add(
      Date.UTC(start.getUTCFullYear(), start.getUTCMonth(), start.getUTCDate())
    );
    if (!from || start < from) from = start;
    if (!to || start > to) to = start;
  }

  return {
    events,
    buckets: buckets.length,
    devices: devices.size,
    days: days.size,
    sources: [...sources].sort(),
    from,
    to,
  };
}

export interface PipelineStats {
  devices: number;
  households: number;
  productionHouseholds: number;
  lineRecipients: number;
  alerts: number;
  failedAlerts: number;
  connectedSwitchBots: number;
  /** Device events ingested into the Fabric activity table. */
  activityEvents: number;
  /** Hourly activity rows stored in Fabric (one per household/device/hour). */
  fabricRows: number;
  /** Most recent device event across all households, or `null` when unknown. */
  lastEvent: Date | null;
  lastSync: Date | null;
}

/**
 * Throughput numbers for the architecture animation. Everything is derived from
 * the same rows the tables render, so the diagram can never disagree with them.
 */
export function pipelineStats(
  rows: HouseholdRow[],
  alerts: AlertRow[],
  activity: ActivityRow[] = []
): PipelineStats {
  let lastEvent: Date | null = null;
  let lastSync: Date | null = null;

  for (const row of rows) {
    const event = row.lastEventUtc ? new Date(row.lastEventUtc) : null;
    if (event && !Number.isNaN(event.getTime()) && (!lastEvent || event > lastEvent)) {
      lastEvent = event;
    }

    const captured = row.capturedAt ? toDate(row.capturedAt) : null;
    if (captured && !Number.isNaN(captured.getTime()) && (!lastSync || captured > lastSync)) {
      lastSync = captured;
    }
  }

  return {
    devices: rows.reduce((sum, row) => sum + toInt(row.deviceCount), 0),
    households: rows.length,
    productionHouseholds: rows.filter((row) => row.dataSourceMode === 'Production').length,
    lineRecipients: rows.reduce((sum, row) => sum + toInt(row.activeLineRecipients), 0),
    alerts: alerts.length,
    failedAlerts: alerts.filter((alert) => !alert.success).length,
    connectedSwitchBots: rows.filter((row) => row.switchBotStatus === 'Connected').length,
    activityEvents: activity.reduce((sum, bucket) => sum + toInt(bucket.eventCount), 0),
    fabricRows: activity.length,
    lastEvent,
    lastSync,
  };
}

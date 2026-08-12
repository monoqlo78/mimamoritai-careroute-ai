import type {
  ActivityRow,
  AiRouterCallRow,
  AlertRow,
  DataOrigin,
  HouseholdRow,
} from './monitoring';

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

/** The offline stub used when no OrcaRouter API key is configured. */
export const MOCK_ROUTER = 'MockAiRouter';

/** Every router value except the local stub reached OrcaRouter. */
export function viaOrcaRouter(row: AiRouterCallRow): boolean {
  return row.router !== MOCK_ROUTER;
}

/**
 * A model OrcaRouter actually served requests with.
 *
 * `autoRouted` separates the two stories the log tells: models OrcaRouter chose
 * on its own (`router = "auto"`) versus the one we pinned because the call site
 * has a deadline or needs JSON mode.
 */
export interface ModelBar {
  model: string;
  calls: number;
  success: number;
  /** Call-weighted mean latency, milliseconds. */
  avgMs: number;
  autoRouted: boolean;
  purposes: string[];
  /**
   * True for the single synthetic bar holding calls that never resolved to a
   * model. It is not a model and must not be drawn or counted as one.
   */
  unresolved?: boolean;
}

/** Label for the synthetic bar. Not a model name; see {@link ModelBar.unresolved}. */
export const UNRESOLVED_BAR = '未応答（失敗）';

/**
 * Collapses the (purpose, router, model) grain down to one bar per model, plus
 * one trailing bar for the calls that never resolved to a model.
 *
 * A call that fails before a model answers still logs `resolvedModel = "auto"`,
 * so there is no model to attribute it to. Dropping it made the bars add up to
 * less than the call count printed above them, which reads as a miscount rather
 * than as a failure. Giving the failures their own bar means the bars total the
 * call count exactly, and the failure is visible instead of inferred.
 */
export function routerModels(rows: AiRouterCallRow[]): ModelBar[] {
  const byModel = new Map<string, ModelBar & { weighted: number }>();
  let unresolvedCalls = 0;
  let unresolvedSuccess = 0;
  let unresolvedWeighted = 0;
  const unresolvedPurposes: string[] = [];

  for (const row of rows) {
    if (!viaOrcaRouter(row)) continue;

    const calls = toInt(row.callCount);
    const model = row.resolvedModel;

    if (!model || model === 'auto') {
      unresolvedCalls += calls;
      unresolvedSuccess += toInt(row.successCount);
      unresolvedWeighted += toInt(row.avgDurationMs) * calls;
      if (!unresolvedPurposes.includes(row.purpose)) unresolvedPurposes.push(row.purpose);
      continue;
    }

    const entry = byModel.get(model) ?? {
      model,
      calls: 0,
      success: 0,
      avgMs: 0,
      autoRouted: false,
      purposes: [],
      weighted: 0,
    };

    entry.calls += calls;
    entry.success += toInt(row.successCount);
    entry.weighted += toInt(row.avgDurationMs) * calls;
    entry.autoRouted = entry.autoRouted || row.router === 'auto';
    if (!entry.purposes.includes(row.purpose)) entry.purposes.push(row.purpose);
    byModel.set(model, entry);
  }

  const bars = [...byModel.values()]
    .map(({ weighted, ...bar }) => ({
      ...bar,
      avgMs: bar.calls > 0 ? Math.round(weighted / bar.calls) : 0,
      purposes: bar.purposes.sort(),
    }))
    .sort((a, b) => b.calls - a.calls);

  if (unresolvedCalls > 0) {
    // Always last: it is the leftover, and sorting it among the models by call
    // count would imply it competes with them.
    bars.push({
      model: UNRESOLVED_BAR,
      calls: unresolvedCalls,
      success: unresolvedSuccess,
      avgMs: Math.round(unresolvedWeighted / unresolvedCalls),
      autoRouted: false,
      purposes: unresolvedPurposes.sort(),
      unresolved: true,
    });
  }

  return bars;
}

export interface RouterSummary {
  /** Calls that went through OrcaRouter. */
  calls: number;
  success: number;
  /** Distinct models OrcaRouter resolved to. */
  models: number;
  /** Calls OrcaRouter's own router assigned a model to. */
  autoCalls: number;
  /** Calls where we pinned the model up front. */
  pinnedCalls: number;
  autoAvgMs: number;
  pinnedAvgMs: number;
  /** Calls served by the offline stub, i.e. never sent to OrcaRouter. */
  mockCalls: number;
  /**
   * Calls that reached OrcaRouter but never resolved to a model name (a failed
   * call still logs `resolvedModel = "auto"`). {@link routerModels} has no bar to
   * put these on, so without showing this number the bars silently fail to add
   * up to {@link calls} and the page looks like it is miscounting.
   */
  unresolvedCalls: number;
  lastCalledAt: Date | null;
}

/** Totals for the diagram and the caption above the model chart. */
export function routerSummary(rows: AiRouterCallRow[]): RouterSummary {
  let calls = 0;
  let success = 0;
  let autoCalls = 0;
  let pinnedCalls = 0;
  let autoWeighted = 0;
  let pinnedWeighted = 0;
  let mockCalls = 0;
  let unresolvedCalls = 0;
  let lastCalledAt: Date | null = null;
  const models = new Set<string>();

  for (const row of rows) {
    const count = toInt(row.callCount);

    if (!viaOrcaRouter(row)) {
      mockCalls += count;
      continue;
    }

    calls += count;
    success += toInt(row.successCount);
    if (row.resolvedModel && row.resolvedModel !== 'auto') {
      models.add(row.resolvedModel);
    } else {
      unresolvedCalls += count;
    }

    const weighted = toInt(row.avgDurationMs) * count;
    if (row.router === 'auto') {
      autoCalls += count;
      autoWeighted += weighted;
    } else {
      pinnedCalls += count;
      pinnedWeighted += weighted;
    }

    const called = row.lastCalledAt ? toDate(row.lastCalledAt) : null;
    if (called && !Number.isNaN(called.getTime()) && (!lastCalledAt || called > lastCalledAt)) {
      lastCalledAt = called;
    }
  }

  return {
    calls,
    success,
    models: models.size,
    autoCalls,
    pinnedCalls,
    autoAvgMs: autoCalls > 0 ? Math.round(autoWeighted / autoCalls) : 0,
    pinnedAvgMs: pinnedCalls > 0 ? Math.round(pinnedWeighted / pinnedCalls) : 0,
    mockCalls,
    unresolvedCalls,
    lastCalledAt,
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
  /** Calls routed through OrcaRouter, and how many distinct models it resolved to. */
  aiCalls: number;
  aiModels: number;
  /**
   * The subset of `aiCalls` that resolved to a named model, i.e. exactly what the
   * model bars below the diagram add up to. The diagram used to read
   * "79 回 / 4 モデル", which says those four models account for all 79 calls --
   * but a call that fails before a model answers is still logged, with no model
   * name to file it under, so the bars only totalled 78. Carrying the resolved
   * count here lets the diagram state both numbers instead of implying one.
   */
  aiResolvedCalls: number;
  /** Of `aiCalls`, the subset OrcaRouter itself picked a model for. */
  aiAutoCalls: number;
  aiAutoAvgMs: number;
  aiPinnedAvgMs: number;
  /** Where the rendered rows came from. Drives the console node's label. */
  origin: DataOrigin;
}

/**
 * Throughput numbers for the architecture animation. Everything is derived from
 * the same rows the tables render, so the diagram can never disagree with them.
 */
export function pipelineStats(
  rows: HouseholdRow[],
  alerts: AlertRow[],
  activity: ActivityRow[] = [],
  origin: DataOrigin = 'fabric',
  aiCalls: AiRouterCallRow[] = []
): PipelineStats {
  let lastEvent: Date | null = null;
  let lastSync: Date | null = null;
  const ai = routerSummary(aiCalls);

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
    aiCalls: ai.calls,
    aiModels: ai.models,
    aiResolvedCalls: ai.calls - ai.unresolvedCalls,
    aiAutoCalls: ai.autoCalls,
    aiAutoAvgMs: ai.autoAvgMs,
    aiPinnedAvgMs: ai.pinnedAvgMs,
    origin,
  };
}

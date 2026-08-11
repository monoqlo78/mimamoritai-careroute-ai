import { describe, expect, it } from 'vitest';

import {
  activitySummary,
  alertsByDay,
  dailyActivity,
  deliveryStats,
  deviceBreakdown,
  hourlyRhythm,
  householdBars,
  pipelineStats,
  riskDistribution,
  routerModels,
  routerSummary,
} from '@/services/analytics';
import type {
  ActivityRow,
  AiRouterCallRow,
  AlertRow,
  HouseholdRow,
} from '@/services/monitoring';

function aiCall(overrides: Partial<AiRouterCallRow> = {}): AiRouterCallRow {
  return {
    id: 'ai1',
    purpose: 'intent',
    router: 'auto',
    resolvedModel: 'deepseek-v4-pro',
    callCount: '7',
    successCount: '7',
    avgDurationMs: '3856',
    lastCalledAt: new Date('2026-08-09T04:03:38.000Z'),
    ...overrides,
  };
}

function bucket(overrides: Partial<ActivityRow> = {}): ActivityRow {
  return {
    id: 'a1',
    householdId: 'hh1',
    householdName: 'テスト世帯',
    deviceName: 'リビング照明',
    deviceType: 'Light',
    bucketStart: new Date('2026-08-10T00:00:00.000Z'),
    eventCount: '3',
    onCount: '2',
    source: 'SwitchBotPoll',
    ...overrides,
  };
}

function household(overrides: Partial<HouseholdRow> = {}): HouseholdRow {
  return {
    id: 'h1',
    householdId: 'hh1',
    name: 'テスト世帯',
    dataSourceMode: 'Production',
    memberCount: '2',
    residentCount: '1',
    deviceCount: '3',
    lastEventUtc: '2026-08-11T00:00:00.000Z',
    switchBotStatus: 'Connected',
    switchBotError: '',
    activeLineRecipients: '2',
    alertsInWindow: '4',
    failedAlertsInWindow: '1',
    latestRiskLevel: 'Medium',
    needsAttention: false,
    capturedAt: new Date('2026-08-11T01:00:00.000Z'),
    ...overrides,
  };
}

function alert(overrides: Partial<AlertRow> = {}): AlertRow {
  return {
    id: 'a1',
    householdId: 'hh1',
    householdName: 'テスト世帯',
    riskLevel: 'Medium',
    score: '35',
    reason: '無反応',
    success: true,
    error: '',
    sentAt: new Date('2026-08-11T09:00:00'),
    ...overrides,
  };
}

const NOW = new Date('2026-08-11T23:00:00');

describe('alertsByDay', () => {
  it('always returns a fixed-width window, oldest first', () => {
    const buckets = alertsByDay([], 7, NOW);

    expect(buckets).toHaveLength(7);
    expect(buckets[6].label).toBe('8/11');
    expect(buckets[0].label).toBe('8/5');
    expect(buckets.every((bucket) => bucket.total === 0)).toBe(true);
  });

  it('counts totals and failures into the matching day', () => {
    const buckets = alertsByDay(
      [
        alert({ sentAt: new Date('2026-08-11T09:00:00') }),
        alert({ id: 'a2', sentAt: new Date('2026-08-11T20:00:00'), success: false }),
        alert({ id: 'a3', sentAt: new Date('2026-08-09T12:00:00') }),
      ],
      7,
      NOW
    );

    expect(buckets[6]).toMatchObject({ total: 2, failed: 1 });
    expect(buckets[4]).toMatchObject({ total: 1, failed: 0 });
  });

  it('ignores alerts outside the window and unparsable timestamps', () => {
    const buckets = alertsByDay(
      [
        alert({ sentAt: new Date('2026-01-01T00:00:00') }),
        alert({ id: 'a2', sentAt: new Date('nope') }),
      ],
      7,
      NOW
    );

    expect(buckets.reduce((sum, bucket) => sum + bucket.total, 0)).toBe(0);
  });

  it('accepts serialised dates', () => {
    const buckets = alertsByDay(
      [alert({ sentAt: '2026-08-11T09:00:00' as unknown as Date })],
      7,
      NOW
    );

    expect(buckets[6].total).toBe(1);
  });
});

describe('riskDistribution', () => {
  it('drops empty levels and keeps High first', () => {
    const slices = riskDistribution([
      alert({ riskLevel: 'High' }),
      alert({ id: 'a2', riskLevel: 'Medium' }),
      alert({ id: 'a3', riskLevel: 'Medium' }),
    ]);

    expect(slices.map((slice) => [slice.level, slice.count])).toEqual([
      ['High', 1],
      ['Medium', 2],
    ]);
  });

  it('buckets unrecognised levels as unknown', () => {
    const slices = riskDistribution([alert({ riskLevel: '' })]);

    expect(slices).toEqual([expect.objectContaining({ level: 'Unknown', count: 1 })]);
  });
});

describe('deliveryStats', () => {
  it('reports a 100% rate when there is nothing to deliver', () => {
    expect(deliveryStats([])).toMatchObject({ total: 0, successRate: 100 });
  });

  it('rounds the success rate', () => {
    const stats = deliveryStats([
      alert(),
      alert({ id: 'a2' }),
      alert({ id: 'a3', success: false }),
    ]);

    expect(stats).toMatchObject({ total: 3, success: 2, failed: 1, successRate: 67 });
  });
});

describe('householdBars', () => {
  it('sorts by device count descending and coerces the string counters', () => {
    const bars = householdBars([
      household({ id: 'a', name: '少ない', deviceCount: '1' }),
      household({ id: 'b', name: '多い', deviceCount: '9', alertsInWindow: 'x' }),
    ]);

    expect(bars.map((bar) => bar.name)).toEqual(['多い', '少ない']);
    expect(bars[0].alerts).toBe(0);
  });
});

describe('pipelineStats', () => {
  it('aggregates the numbers the diagram labels', () => {
    const stats = pipelineStats(
      [
        household({ id: 'a', deviceCount: '3', activeLineRecipients: '2' }),
        household({
          id: 'b',
          deviceCount: '4',
          activeLineRecipients: '1',
          dataSourceMode: 'Sample',
          switchBotStatus: 'NotConfigured',
          lastEventUtc: '2026-08-12T00:00:00.000Z',
        }),
      ],
      [alert(), alert({ id: 'a2', success: false })]
    );

    expect(stats).toMatchObject({
      devices: 7,
      households: 2,
      productionHouseholds: 1,
      lineRecipients: 3,
      alerts: 2,
      failedAlerts: 1,
      connectedSwitchBots: 1,
    });
    expect(stats.lastEvent?.toISOString()).toBe('2026-08-12T00:00:00.000Z');
  });

  it('leaves timestamps null when the source rows have none', () => {
    const stats = pipelineStats([household({ lastEventUtc: '' })], []);

    expect(stats.lastEvent).toBeNull();
  });

  it('counts activity events and rows for the diagram', () => {
    const stats = pipelineStats([household()], [], [
      bucket({ eventCount: '3' }),
      bucket({ id: 'a2', eventCount: '5' }),
    ]);

    expect(stats.activityEvents).toBe(8);
    expect(stats.fabricRows).toBe(2);
  });

  it('carries the data origin so the diagram cannot claim to be live', () => {
    expect(pipelineStats([household()], []).origin).toBe('fabric');
    expect(pipelineStats([household()], [], [], 'snapshot').origin).toBe('snapshot');
  });
});

describe('dailyActivity', () => {
  it('sums buckets per UTC day and fills gaps with zero', () => {
    const points = dailyActivity([
      bucket({ bucketStart: new Date('2026-08-10T01:00:00.000Z'), eventCount: '2', onCount: '1' }),
      bucket({ bucketStart: new Date('2026-08-10T05:00:00.000Z'), eventCount: '3', onCount: '2' }),
      bucket({ bucketStart: new Date('2026-08-12T09:00:00.000Z'), eventCount: '4', onCount: '0' }),
    ]);

    expect(points.map((point) => point.events)).toEqual([5, 0, 4]);
    expect(points[0].onEvents).toBe(3);
    expect(points[1].label).toBe('8/11');
  });

  it('caps the window to the most recent days', () => {
    const points = dailyActivity(
      [
        bucket({ bucketStart: new Date('2026-07-01T00:00:00.000Z') }),
        bucket({ bucketStart: new Date('2026-08-10T00:00:00.000Z') }),
      ],
      3
    );

    expect(points).toHaveLength(3);
    expect(points[points.length - 1].label).toBe('8/10');
  });

  it('returns nothing when there is no activity', () => {
    expect(dailyActivity([])).toEqual([]);
  });
});

describe('hourlyRhythm', () => {
  it('always returns 24 slots shifted into JST', () => {
    const cells = hourlyRhythm([
      bucket({ bucketStart: new Date('2026-08-10T22:00:00.000Z'), eventCount: '4' }),
    ]);

    expect(cells).toHaveLength(24);
    // 22:00 UTC is 07:00 the next day in JST.
    expect(cells[7].events).toBe(4);
    expect(cells[22].events).toBe(0);
  });
});

describe('deviceBreakdown', () => {
  it('totals per device, busiest first', () => {
    const slices = deviceBreakdown([
      bucket({ deviceName: '扇風機', eventCount: '2' }),
      bucket({ deviceName: 'リビング照明', eventCount: '5' }),
      bucket({ deviceName: '扇風機', eventCount: '6' }),
    ]);

    expect(slices.map((slice) => [slice.name, slice.events])).toEqual([
      ['扇風機', 8],
      ['リビング照明', 5],
    ]);
  });
});

describe('activitySummary', () => {
  it('reports totals, coverage and distinct ingestion sources', () => {
    const summary = activitySummary([
      bucket({ eventCount: '2', source: 'SwitchBotPoll' }),
      bucket({
        bucketStart: new Date('2026-08-11T10:00:00.000Z'),
        deviceName: '扇風機',
        eventCount: '3',
        source: 'AppCommand',
      }),
    ]);

    expect(summary.events).toBe(5);
    expect(summary.buckets).toBe(2);
    expect(summary.devices).toBe(2);
    expect(summary.days).toBe(2);
    expect(summary.sources).toEqual(['AppCommand', 'SwitchBotPoll']);
    expect(summary.from?.toISOString()).toBe('2026-08-10T00:00:00.000Z');
  });
});

describe('routerModels', () => {
  it('collapses purposes into one bar per model and weights latency by call count', () => {
    const bars = routerModels([
      aiCall({ purpose: 'intent', resolvedModel: 'qwen3.7-plus', callCount: '6', avgDurationMs: '1000' }),
      aiCall({ purpose: 'summary', resolvedModel: 'qwen3.7-plus', callCount: '2', avgDurationMs: '5000' }),
    ]);

    expect(bars).toHaveLength(1);
    expect(bars[0].calls).toBe(8);
    expect(bars[0].purposes).toEqual(['intent', 'summary']);
    // (6*1000 + 2*5000) / 8 -- not the unweighted 3000.
    expect(bars[0].avgMs).toBe(2000);
    expect(bars[0].autoRouted).toBe(true);
  });

  it('marks pinned models separately from the ones OrcaRouter chose', () => {
    const bars = routerModels([
      aiCall({ router: 'OrcaRouter', resolvedModel: 'gpt-4.1-mini', callCount: '35' }),
      aiCall({ router: 'auto', resolvedModel: 'glm-5.2', callCount: '2' }),
    ]);

    expect(bars.map((bar) => [bar.model, bar.autoRouted])).toEqual([
      ['gpt-4.1-mini', false],
      ['glm-5.2', true],
    ]);
  });

  it('excludes the offline stub and calls whose model never resolved', () => {
    const bars = routerModels([
      aiCall({ router: 'MockAiRouter', resolvedModel: 'mock/local-rules', callCount: '2' }),
      aiCall({ router: 'OrcaRouter', resolvedModel: 'auto', callCount: '1', successCount: '0' }),
    ]);

    expect(bars).toEqual([]);
  });
});

describe('routerSummary', () => {
  it('separates OrcaRouter traffic from the local stub', () => {
    const summary = routerSummary([
      aiCall({ router: 'OrcaRouter', resolvedModel: 'gpt-4.1-mini', callCount: '50', successCount: '50', avgDurationMs: '1800' }),
      aiCall({ router: 'auto', resolvedModel: 'glm-5.2', callCount: '10', successCount: '10', avgDurationMs: '6000' }),
      aiCall({ router: 'MockAiRouter', resolvedModel: 'mock/local-rules', callCount: '2', successCount: '2', avgDurationMs: '1' }),
    ]);

    expect(summary.calls).toBe(60);
    expect(summary.mockCalls).toBe(2);
    expect(summary.autoCalls).toBe(10);
    expect(summary.pinnedCalls).toBe(50);
    expect(summary.models).toBe(2);
    expect(summary.autoAvgMs).toBe(6000);
    expect(summary.pinnedAvgMs).toBe(1800);
  });

  it('counts a failed call even though it has no model to attribute', () => {
    const summary = routerSummary([
      aiCall({ router: 'OrcaRouter', resolvedModel: 'auto', callCount: '1', successCount: '0' }),
    ]);

    expect(summary.calls).toBe(1);
    expect(summary.success).toBe(0);
    expect(summary.models).toBe(0);
  });
});

describe('pipelineStats AI totals', () => {
  it('feeds the diagram only calls that reached OrcaRouter', () => {
    const stats = pipelineStats([household()], [alert()], [], 'fabric', [
      aiCall({ router: 'auto', resolvedModel: 'qwen3.7-plus', callCount: '6' }),
      aiCall({ router: 'MockAiRouter', resolvedModel: 'mock/local-rules', callCount: '2' }),
    ]);

    expect(stats.aiCalls).toBe(6);
    expect(stats.aiModels).toBe(1);
    expect(stats.aiAutoCalls).toBe(6);
  });

  it('reports no AI traffic when the table is empty', () => {
    const stats = pipelineStats([household()], [alert()]);
    expect(stats.aiCalls).toBe(0);
    expect(stats.aiModels).toBe(0);
  });
});

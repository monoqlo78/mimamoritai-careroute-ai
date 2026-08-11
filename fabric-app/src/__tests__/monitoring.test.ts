import { describe, expect, it } from 'vitest';

import {
  sortHouseholds,
  summarize,
  type HouseholdRow,
} from '@/services/monitoring';

function household(overrides: Partial<HouseholdRow>): HouseholdRow {
  return {
    id: crypto.randomUUID(),
    householdId: crypto.randomUUID(),
    name: '家',
    dataSourceMode: 'Sample',
    memberCount: '1',
    residentCount: '1',
    deviceCount: '0',
    lastEventUtc: '',
    switchBotStatus: 'NotConfigured',
    switchBotError: '',
    activeLineRecipients: '0',
    alertsInWindow: '0',
    failedAlertsInWindow: '0',
    latestRiskLevel: '',
    needsAttention: false,
    capturedAt: new Date(),
    ...overrides,
  };
}

describe('summarize', () => {
  it('adds up counters across households', () => {
    const totals = summarize([
      household({ dataSourceMode: 'Production', deviceCount: '6', alertsInWindow: '3', failedAlertsInWindow: '1', needsAttention: true }),
      household({ deviceCount: '4', alertsInWindow: '1' }),
    ]);

    expect(totals).toEqual({
      households: 2,
      production: 1,
      devices: 10,
      alerts: 4,
      failedAlerts: 1,
      needingAttention: 1,
    });
  });

  it('treats unparseable counters as zero instead of NaN', () => {
    const totals = summarize([household({ deviceCount: '', alertsInWindow: 'x' })]);

    expect(totals.devices).toBe(0);
    expect(totals.alerts).toBe(0);
  });
});

describe('sortHouseholds', () => {
  it('puts households needing attention first', () => {
    const sorted = sortHouseholds([
      household({ name: 'あ家' }),
      household({ name: 'ん家', needsAttention: true }),
    ]);

    expect(sorted.map((h) => h.name)).toEqual(['ん家', 'あ家']);
  });

  it('falls back to name order within the same attention state', () => {
    const sorted = sortHouseholds([
      household({ name: 'さ家' }),
      household({ name: 'か家' }),
    ]);

    expect(sorted.map((h) => h.name)).toEqual(['か家', 'さ家']);
  });
});

import { describe, it, expect } from 'vitest';
import {
  npStatusFromExecution,
  STATUS_COLOR_VAR,
  STATUS_BADGE_CLASS,
  STATUS_TEXT_CLASS,
  STATUS_DOT_CLASS,
  type NpStatus,
} from '../../lib/statusTokens';

const ALL_STATUSES: NpStatus[] = [
  'success', 'failed', 'running', 'paused', 'skipped',
  'pending', 'cancelled', 'warning', 'info',
];

describe('npStatusFromExecution', () => {
  it.each([
    ['Succeeded', 'success'],
    ['Failed', 'failed'],
    ['TimedOut', 'failed'],
    ['Running', 'running'],
    ['Paused', 'paused'],
    ['Skipped', 'skipped'],
    ['Pending', 'pending'],
    ['Queued', 'pending'],
    ['Cancelled', 'cancelled'],
    ['Canceled', 'cancelled'],
    ['Warning', 'warning'],
  ] as const)('maps backend status %s → %s', (input, expected) => {
    expect(npStatusFromExecution(input)).toBe(expected);
  });

  it('is case-insensitive', () => {
    expect(npStatusFromExecution('succeeded')).toBe('success');
    expect(npStatusFromExecution('FAILED')).toBe('failed');
    expect(npStatusFromExecution('rUnNiNg')).toBe('running');
  });

  it('falls back to info for unknown, empty, null and undefined', () => {
    expect(npStatusFromExecution('SomethingNew')).toBe('info');
    expect(npStatusFromExecution('')).toBe('info');
    expect(npStatusFromExecution(null)).toBe('info');
    expect(npStatusFromExecution(undefined)).toBe('info');
  });
});

describe('status token records', () => {
  const records: Array<[string, Record<NpStatus, string>]> = [
    ['STATUS_COLOR_VAR', STATUS_COLOR_VAR],
    ['STATUS_BADGE_CLASS', STATUS_BADGE_CLASS],
    ['STATUS_TEXT_CLASS', STATUS_TEXT_CLASS],
    ['STATUS_DOT_CLASS', STATUS_DOT_CLASS],
  ];

  it.each(records)('%s covers every NpStatus with a non-empty value', (_name, record) => {
    for (const status of ALL_STATUSES) {
      expect(record[status], `missing entry for "${status}"`).toBeTruthy();
      expect(record[status].trim().length).toBeGreaterThan(0);
    }
    expect(Object.keys(record).sort()).toEqual([...ALL_STATUSES].sort());
  });

  it('STATUS_COLOR_VAR values are CSS var() references (SVG/canvas-safe)', () => {
    for (const status of ALL_STATUSES) {
      expect(STATUS_COLOR_VAR[status]).toMatch(/^var\(--color-[a-z-]+\)$/);
    }
  });

  it('failed maps onto the M3 error family (single red in the app)', () => {
    expect(STATUS_COLOR_VAR.failed).toBe('var(--color-error)');
    expect(STATUS_BADGE_CLASS.failed).toContain('error-container');
    expect(STATUS_DOT_CLASS.failed).toBe('bg-error');
  });
});

import { describe, it, expect, vi, afterEach } from 'vitest';
import { formatDuration, formatRelative } from '../../lib/format';

afterEach(() => {
  vi.useRealTimers();
});

describe('formatDuration', () => {
  it('returns "–" for null', () => {
    expect(formatDuration(null)).toBe('–');
  });

  it('returns "–" for undefined', () => {
    expect(formatDuration(undefined)).toBe('–');
  });

  it('returns "–" for Infinity', () => {
    expect(formatDuration(Infinity)).toBe('–');
  });

  it('formats sub-second as ms', () => {
    expect(formatDuration(0)).toBe('0 ms');
    expect(formatDuration(500)).toBe('500 ms');
    expect(formatDuration(999)).toBe('999 ms');
  });

  it('formats seconds with one decimal for < 10 s', () => {
    expect(formatDuration(1000)).toBe('1.0 s');
    expect(formatDuration(5500)).toBe('5.5 s');
    expect(formatDuration(9900)).toBe('9.9 s');
  });

  it('formats seconds without decimal for >= 10 s', () => {
    expect(formatDuration(10_000)).toBe('10 s');
    expect(formatDuration(15_000)).toBe('15 s');
  });

  it('formats minutes and seconds', () => {
    expect(formatDuration(60_000)).toBe('1m 0s');
    expect(formatDuration(90_000)).toBe('1m 30s');
    expect(formatDuration(3_661_000)).toBe('61m 1s');
  });
});

describe('formatRelative', () => {
  it('returns "just now" for very recent timestamps', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2024-01-01T12:00:00Z'));
    expect(formatRelative('2024-01-01T11:59:30Z')).toBe('just now');
  });

  it('returns minutes ago', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2024-01-01T12:00:00Z'));
    expect(formatRelative('2024-01-01T11:55:00Z')).toBe('5m ago');
  });

  it('returns hours ago', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2024-01-01T12:00:00Z'));
    expect(formatRelative('2024-01-01T09:00:00Z')).toBe('3h ago');
  });

  it('returns days ago', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2024-01-05T12:00:00Z'));
    expect(formatRelative('2024-01-03T12:00:00Z')).toBe('2d ago');
  });

  it('returns localeDate for timestamps older than 7 days', () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2024-01-20T12:00:00Z'));
    const result = formatRelative('2024-01-01T12:00:00Z');
    // Locale-dependent — just check it is not a relative string
    expect(result).not.toContain('ago');
    expect(result).not.toBe('just now');
  });
});

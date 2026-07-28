import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useOpsClock } from '../../hooks/useOpsClock';

// The clock is what makes timeline bars grow and slide, so pausing it is what actually holds
// the picture still during a display freeze.

describe('useOpsClock', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('advances once per period while running', () => {
    const { result } = renderHook(() => useOpsClock(1000));
    const first = result.current;
    act(() => { vi.advanceTimersByTime(3000); });
    expect(result.current).toBeGreaterThan(first);
  });

  it('holds its last value while paused, however much time passes', () => {
    const { result, rerender } = renderHook(
      ({ paused }) => useOpsClock(1000, paused),
      { initialProps: { paused: false } },
    );
    act(() => { vi.advanceTimersByTime(2000); });
    const atFreeze = result.current;

    rerender({ paused: true });
    act(() => { vi.advanceTimersByTime(60_000); });
    expect(result.current).toBe(atFreeze);
  });

  it('resumes from real time after unpausing — no catch-up burst', () => {
    const { result, rerender } = renderHook(
      ({ paused }) => useOpsClock(1000, paused),
      { initialProps: { paused: true } },
    );
    const frozen = result.current;
    act(() => { vi.advanceTimersByTime(30_000); });
    expect(result.current).toBe(frozen);

    rerender({ paused: false });
    act(() => { vi.advanceTimersByTime(1000); });
    expect(result.current).toBeGreaterThan(frozen);
  });
});

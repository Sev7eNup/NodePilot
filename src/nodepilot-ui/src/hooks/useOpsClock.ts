import { useEffect, useState } from 'react';

/**
 * A second-quantized clock for the Mission-Control view: one interval and one React re-render
 * per period. Between ticks the motion comes from CSS linear transitions on the timeline bars,
 * so there is no per-frame React work.
 */
export function useOpsClock(periodMs: number = 1000, paused: boolean = false): number {
  const [nowMs, setNowMs] = useState(() => Date.now());

  useEffect(() => {
    // While paused, keep the last value in state and run no interval. The clock is what makes
    // the bars grow and slide, so stopping it is what holds the display still.
    if (paused) return;
    const id = setInterval(() => setNowMs(Date.now()), periodMs);
    return () => clearInterval(id);
  }, [periodMs, paused]);

  return nowMs;
}

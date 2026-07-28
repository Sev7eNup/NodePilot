import { useEffect, useState } from 'react';

/**
 * A second-quantized clock for the Mission-Control view: one interval, one React re-render
 * per period. Between ticks, motion is carried by CSS linear transitions on the timeline
 * bars — no per-frame React work.
 */
export function useOpsClock(periodMs: number = 1000, paused: boolean = false): number {
  const [nowMs, setNowMs] = useState(() => Date.now());

  useEffect(() => {
    // Paused: keep the last value (it stays in state) and run no interval at all. Used by the
    // display freeze — the clock is what makes bars grow and slide, so stopping it is what
    // actually holds the picture still.
    if (paused) return;
    const id = setInterval(() => setNowMs(Date.now()), periodMs);
    return () => clearInterval(id);
  }, [periodMs, paused]);

  return nowMs;
}

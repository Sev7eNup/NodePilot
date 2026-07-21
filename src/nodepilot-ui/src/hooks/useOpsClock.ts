import { useEffect, useState } from 'react';

/**
 * A second-quantized clock for the Mission-Control view: one interval, one React re-render
 * per period. Between ticks, motion is carried by CSS linear transitions on the timeline
 * bars — no per-frame React work.
 */
export function useOpsClock(periodMs: number = 1000): number {
  const [nowMs, setNowMs] = useState(() => Date.now());

  useEffect(() => {
    const id = setInterval(() => setNowMs(Date.now()), periodMs);
    return () => clearInterval(id);
  }, [periodMs]);

  return nowMs;
}

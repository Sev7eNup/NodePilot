import { useEffect, useState } from 'react';

/**
 * Returns a `now` timestamp that updates once per minute. Use this when several
 * elements share a relative-time label (e.g. "in 5m") and you want them all to
 * re-render together — pass `now` into format helpers instead of letting each row
 * own its own setInterval.
 *
 * The first re-render fires after 60s; before that, every consumer renders with
 * the timestamp from the moment the hook ran.
 */
export function useMinuteTick(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = globalThis.setInterval(() => setNow(Date.now()), 60_000);
    return () => globalThis.clearInterval(id);
  }, []);
  return now;
}

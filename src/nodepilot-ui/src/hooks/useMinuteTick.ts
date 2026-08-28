import { useEffect, useState } from 'react';

/**
 * Returns a `now` timestamp that updates once per minute. Use it when several elements share a
 * relative-time label and should re-render together, instead of giving each row its own interval.
 * The first update fires after 60s; until then every consumer sees the timestamp taken at mount.
 */
export function useMinuteTick(): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = globalThis.setInterval(() => setNow(Date.now()), 60_000);
    return () => globalThis.clearInterval(id);
  }, []);
  return now;
}

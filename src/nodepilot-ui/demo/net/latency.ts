/**
 * Small, jittered delays so the demo reads like a network app rather than a local object
 * graph: lists fade in, spinners appear briefly, optimistic updates are visible.
 *
 * Deliberately short. The save path in particular stays fast, because the designer arms
 * `beforeunload` until the autosave lands.
 */

const DEFAULT_MIN_MS = 45;
const DEFAULT_MAX_MS = 160;

let enabled = true;

/** Tests turn latency off so they do not wait on timers. */
export function setLatencyEnabled(value: boolean): void {
  enabled = value;
}

export function delay(minMs = DEFAULT_MIN_MS, maxMs = DEFAULT_MAX_MS): Promise<void> {
  if (!enabled) return Promise.resolve();
  const ms = minMs + Math.random() * (maxMs - minMs);
  return new Promise((resolve) => { globalThis.setTimeout(resolve, ms); });
}

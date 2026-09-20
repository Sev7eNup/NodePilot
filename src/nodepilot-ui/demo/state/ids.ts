/**
 * Deterministic GUIDs for the demo world.
 *
 * Every id is derived from a stable seed string, so a workflow keeps the same id across
 * reloads and a deep link a visitor shares still resolves. Random ids would break that.
 */

/** FNV-1a over a string, returned as an unsigned 32-bit value. */
function fnv1a(input: string): number {
  let hash = 0x811c9dc5;
  for (let i = 0; i < input.length; i++) {
    hash ^= input.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return hash >>> 0;
}

/** xorshift32, used to stretch one 32-bit seed into the 16 bytes a GUID needs. */
function nextState(state: number): number {
  let x = state;
  x ^= x << 13;
  x ^= x >>> 17;
  x ^= x << 5;
  return x >>> 0;
}

/**
 * A stable RFC-4122-shaped v4 GUID for `seed`. Not cryptographically random — the demo only
 * needs ids that look right, stay unique within the world and never change.
 */
export function demoId(seed: string): string {
  const bytes = new Uint8Array(16);
  let state = fnv1a(seed) || 1;
  for (let i = 0; i < 16; i++) {
    state = nextState(state);
    bytes[i] = state & 0xff;
  }
  // Version 4 and the RFC variant bits, so consumers that parse the shape accept it.
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;

  const hex: string[] = [];
  for (const byte of bytes) hex.push(byte.toString(16).padStart(2, '0'));
  return [
    hex.slice(0, 4).join(''),
    hex.slice(4, 6).join(''),
    hex.slice(6, 8).join(''),
    hex.slice(8, 10).join(''),
    hex.slice(10, 16).join(''),
  ].join('-');
}

/**
 * Ids for entities the visitor creates during a session. These need only be unique, not
 * stable, but they stay deterministic per session so a run started right after a create
 * still references the same node.
 */
let runtimeCounter = 0;
export function runtimeId(prefix: string): string {
  runtimeCounter += 1;
  return demoId(`runtime:${prefix}:${runtimeCounter}`);
}

/** Resets the runtime counter so `resetWorld()` reproduces the same ids as a fresh load. */
export function resetRuntimeIds(): void {
  runtimeCounter = 0;
}

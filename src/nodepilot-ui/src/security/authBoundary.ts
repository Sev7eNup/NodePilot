import { clearSensitiveBrowserState } from './sensitiveBrowserState';

const CHANNEL_NAME = 'nodepilot.auth-boundary.v1';

/**
 * localStorage is only a compatibility transport for browsers without BroadcastChannel. The
 * value is removed immediately and contains no SQL, prompt, credential, or cached API data.
 */
export const AUTH_BOUNDARY_STORAGE_KEY = 'nodepilot.authBoundary.event';

export type AuthBoundaryEvent =
  | {
      version: 1;
      type: 'identity';
      userId: string;
      sourceId: string;
      eventId: string;
    }
  | {
      version: 1;
      type: 'logout';
      phase: 'started' | 'succeeded' | 'failed';
      sourceId: string;
      eventId: string;
    }
  | {
      version: 1;
      type: 'unauthorized';
      sourceId: string;
      eventId: string;
    }
  | {
      version: 1;
      /** A stale auth response may still have changed the shared browser cookie jar. */
      type: 'cookie-changed';
      sourceId: string;
      eventId: string;
    };

type BoundaryClearer = () => void;
type BoundaryListener = (event: AuthBoundaryEvent) => void;

const liveStateClearers = new Set<BoundaryClearer>();
const queryCacheClearers = new Set<BoundaryClearer>();
const listeners = new Set<BoundaryListener>();
const identityReprobers = new Set<BoundaryClearer>();
const seenEventIds = new Set<string>();

let channel: BroadcastChannel | null | undefined;
let transportListening = false;
let authBoundaryGeneration = 0;

/** Raised when a network result belongs to an identity which has already been replaced. */
export class AuthBoundaryChangedError extends Error {
  constructor() {
    super('Request result discarded because the authentication context changed.');
    this.name = 'AbortError';
  }
}

/** Capture before starting async work which must not survive the next authentication boundary. */
export function captureAuthBoundaryGeneration(): number {
  return authBoundaryGeneration;
}

/** Validate an async result immediately before it reads or persists user-specific state. */
export function isAuthBoundaryGenerationCurrent(generation: number): boolean {
  return generation === authBoundaryGeneration;
}

export function assertAuthBoundaryGenerationCurrent(generation: number): void {
  if (!isAuthBoundaryGenerationCurrent(generation)) throw new AuthBoundaryChangedError();
}

function randomId(): string {
  try {
    return globalThis.crypto.randomUUID();
  } catch {
    // This is only a same-origin message de-duplication id, not a credential or security token.
    return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  }
}

// A module instance maps to one browser tab. Events carrying this id came from this tab and must
// not be applied twice (the caller already performed its local cleanup synchronously).
const sourceId = randomId();

function getChannel(): BroadcastChannel | null {
  if (channel !== undefined) return channel;
  try {
    channel = typeof globalThis.BroadcastChannel === 'function'
      ? new globalThis.BroadcastChannel(CHANNEL_NAME)
      : null;
  } catch {
    channel = null;
  }
  return channel;
}

function rememberEvent(eventId: string): boolean {
  if (seenEventIds.has(eventId)) return false;
  seenEventIds.add(eventId);
  // Bound memory even if a hostile same-origin page floods the channel.
  if (seenEventIds.size > 100) {
    const oldest = seenEventIds.values().next().value as string | undefined;
    if (oldest) seenEventIds.delete(oldest);
  }
  return true;
}

function parseEvent(value: unknown): AuthBoundaryEvent | null {
  if (!value || typeof value !== 'object') return null;
  const candidate = value as Partial<AuthBoundaryEvent> & Record<string, unknown>;
  if (candidate.version !== 1
    || typeof candidate.sourceId !== 'string'
    || typeof candidate.eventId !== 'string'
    || candidate.sourceId.length > 200
    || candidate.eventId.length > 200) return null;

  if (candidate.type === 'identity') {
    return typeof candidate.userId === 'string' && candidate.userId.length > 0 && candidate.userId.length <= 256
      ? candidate as AuthBoundaryEvent
      : null;
  }
  if (candidate.type === 'logout') {
    if (candidate.phase === 'started'
      || candidate.phase === 'succeeded'
      || candidate.phase === 'failed') return candidate as AuthBoundaryEvent;
    // Fail closed for a tab still running the previous bundle, whose ambiguous `settled` event
    // was emitted on both success and failure. It may suppress a harmless success re-probe, but
    // must never remount an identity after a failed logout.
    return candidate.phase === 'settled'
      ? { ...candidate, phase: 'failed' } as AuthBoundaryEvent
      : null;
  }
  return candidate.type === 'unauthorized' || candidate.type === 'cookie-changed'
    ? candidate as AuthBoundaryEvent
    : null;
}

function deliverRemoteEvent(value: unknown): void {
  const event = parseEvent(value);
  if (!event || event.sourceId === sourceId || !rememberEvent(event.eventId)) return;
  for (const listener of listeners) listener(event);
}

function handleChannelMessage(event: MessageEvent<unknown>): void {
  deliverRemoteEvent(event.data);
}

function handleStorageEvent(event: StorageEvent): void {
  if (event.key !== AUTH_BOUNDARY_STORAGE_KEY || !event.newValue || event.newValue.length > 2048) return;
  try {
    deliverRemoteEvent(JSON.parse(event.newValue));
  } catch {
    // Ignore malformed messages. They are never trusted as authentication proof.
  }
}

function startTransport(): void {
  if (transportListening) return;
  const broadcastChannel = getChannel();
  if (broadcastChannel) broadcastChannel.addEventListener('message', handleChannelMessage);
  else if (typeof window !== 'undefined') window.addEventListener('storage', handleStorageEvent);
  transportListening = true;
}

function stopTransport(): void {
  if (!transportListening) return;
  if (channel) {
    channel.removeEventListener('message', handleChannelMessage);
    channel.close();
  } else if (typeof window !== 'undefined') {
    window.removeEventListener('storage', handleStorageEvent);
  }
  channel = undefined;
  transportListening = false;
}

function publish(event: AuthBoundaryEvent): void {
  // App.tsx installs a long-lived listener before authentication starts. Isolated consumers (unit
  // tests or a future lightweight entry point) still need to publish without leaking a channel.
  if (!transportListening && channel === undefined && typeof globalThis.BroadcastChannel === 'function') {
    try {
      const transientChannel = new globalThis.BroadcastChannel(CHANNEL_NAME);
      transientChannel.postMessage(event);
      transientChannel.close();
      return;
    } catch {
      // Constructor/publish blocked: use the storage-event fallback below.
    }
  }

  const broadcastChannel = getChannel();
  if (broadcastChannel) {
    try {
      broadcastChannel.postMessage(event);
      return;
    } catch {
      // Fall through to the storage-event transport if browser policy disables the channel.
    }
  }

  try {
    globalThis.localStorage.setItem(AUTH_BOUNDARY_STORAGE_KEY, JSON.stringify(event));
    globalThis.localStorage.removeItem(AUTH_BOUNDARY_STORAGE_KEY);
  } catch {
    // Browser storage can be disabled. The initiating tab has already been cleared locally.
  }
}

function envelope<T extends Omit<AuthBoundaryEvent, 'version' | 'sourceId' | 'eventId'>>(
  payload: T,
): AuthBoundaryEvent {
  return {
    ...payload,
    version: 1,
    sourceId,
    eventId: randomId(),
  } as AuthBoundaryEvent;
}

/** Register non-persisted sensitive stores (AI state, auth UI state, legacy clipboard). */
export function registerAuthBoundaryLiveStateClearer(clearer: BoundaryClearer): () => void {
  liveStateClearers.add(clearer);
  return () => liveStateClearers.delete(clearer);
}

/** Register the application's singleton React Query client without importing it into auth code. */
export function registerAuthBoundaryQueryCacheClearer(clearer: BoundaryClearer): () => void {
  queryCacheClearers.add(clearer);
  return () => queryCacheClearers.delete(clearer);
}

/**
 * Register the auth store's authoritative `/auth/me` probe without importing the store into the
 * API client. App startup owns the registration lifetime, avoiding an api-client/store cycle.
 */
export function registerAuthBoundaryIdentityReprober(reprober: BoundaryClearer): () => void {
  identityReprobers.add(reprober);
  return () => identityReprobers.delete(reprober);
}

/**
 * Synchronous local half of every authentication boundary. Live stores are emptied before their
 * persistence keys are removed, then all user-derived server caches are discarded.
 */
export function clearLocalAuthBoundary(): void {
  // Invalidate in-flight callbacks first. Even if an individual cleanup hook fails, no response
  // which started under the previous identity may commit after this point.
  authBoundaryGeneration++;
  for (const clear of liveStateClearers) {
    try { clear(); } catch { /* Continue clearing the other independent stores. */ }
  }
  clearSensitiveBrowserState();
  for (const clear of queryCacheClearers) {
    try { clear(); } catch { /* A cache failure must not retain the browser state above. */ }
  }
}

/** Clear only registered server-data caches after an authenticated identity changes. */
export function clearAuthBoundaryQueryCaches(): void {
  for (const clear of queryCacheClearers) {
    try { clear(); } catch { /* Best effort; identity state is still replaced by the caller. */ }
  }
}

export function publishAuthenticatedIdentity(userId: string): void {
  publish(envelope({ type: 'identity', userId }));
}

export function publishLogoutBoundary(phase: 'started' | 'succeeded' | 'failed'): void {
  publish(envelope({ type: 'logout', phase }));
}

/** Called by the API client before any 401 redirect or error is surfaced. */
export function handleUnauthorizedAuthBoundary(broadcast = true): void {
  clearLocalAuthBoundary();
  if (broadcast) publish(envelope({ type: 'unauthorized' }));
}

/**
 * Browsers apply `Set-Cookie` before resolving fetch. Therefore discarding a stale login/SSO/
 * refresh/logout body is insufficient: the shared cookie jar may already represent another
 * identity. Clear synchronously, notify sibling tabs, then let every tab ask `/auth/me` who the
 * cookie belongs to now.
 */
export function handleStaleAuthCookieResponseBoundary(): void {
  clearLocalAuthBoundary();
  publish(envelope({ type: 'cookie-changed' }));
  for (const reprobe of identityReprobers) {
    try { reprobe(); } catch { /* The local tab remains safely cleared if probing cannot start. */ }
  }
}

export function subscribeToAuthBoundaryEvents(listener: BoundaryListener): () => void {
  listeners.add(listener);
  startTransport();
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0) stopTransport();
  };
}

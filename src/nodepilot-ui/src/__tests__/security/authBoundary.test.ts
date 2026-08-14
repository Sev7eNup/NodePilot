import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  AUTH_BOUNDARY_STORAGE_KEY,
  captureAuthBoundaryGeneration,
  clearLocalAuthBoundary,
  handleStaleAuthCookieResponseBoundary,
  isAuthBoundaryGenerationCurrent,
  publishAuthenticatedIdentity,
  registerAuthBoundaryIdentityReprober,
  registerAuthBoundaryLiveStateClearer,
  registerAuthBoundaryQueryCacheClearer,
  subscribeToAuthBoundaryEvents,
  type AuthBoundaryEvent,
} from '../../security/authBoundary';
import { DB_ADMIN_QUERY_DRAFT_KEY } from '../../security/sensitiveBrowserState';

class FakeBroadcastChannel {
  static instances: FakeBroadcastChannel[] = [];

  readonly name: string;
  readonly postMessage = vi.fn();
  readonly close = vi.fn();
  private readonly messageListeners = new Set<(event: MessageEvent<unknown>) => void>();

  constructor(name: string) {
    this.name = name;
    FakeBroadcastChannel.instances.push(this);
  }

  addEventListener(type: string, listener: EventListenerOrEventListenerObject): void {
    if (type === 'message') this.messageListeners.add(listener as (event: MessageEvent<unknown>) => void);
  }

  removeEventListener(type: string, listener: EventListenerOrEventListenerObject): void {
    if (type === 'message') this.messageListeners.delete(listener as (event: MessageEvent<unknown>) => void);
  }

  emitRemote(data: AuthBoundaryEvent): void {
    for (const listener of this.messageListeners) listener({ data } as MessageEvent<unknown>);
  }
}

describe('authBoundary', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    FakeBroadcastChannel.instances = [];
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('uses BroadcastChannel and accepts only validated remote events', () => {
    vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
    const listener = vi.fn();
    const unsubscribe = subscribeToAuthBoundaryEvents(listener);
    const channel = FakeBroadcastChannel.instances[0];

    channel.emitRemote({
      version: 1,
      type: 'identity',
      userId: 'user-b',
      sourceId: 'another-tab',
      eventId: 'event-1',
    });
    channel.emitRemote({
      version: 1,
      type: 'identity',
      userId: 'user-b',
      sourceId: 'another-tab',
      eventId: 'event-1',
    });

    expect(listener).toHaveBeenCalledTimes(1);
    publishAuthenticatedIdentity('user-a');
    expect(channel.postMessage).toHaveBeenCalledWith(expect.objectContaining({
      version: 1,
      type: 'identity',
      userId: 'user-a',
    }));

    unsubscribe();
    expect(channel.close).toHaveBeenCalled();
  });

  it('falls back safely to a transient storage event when BroadcastChannel is unavailable', () => {
    vi.stubGlobal('BroadcastChannel', undefined);
    const listener = vi.fn();
    const unsubscribe = subscribeToAuthBoundaryEvents(listener);

    const remote: AuthBoundaryEvent = {
      version: 1,
      type: 'unauthorized',
      sourceId: 'another-tab',
      eventId: 'event-2',
    };
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(remote),
    }));

    expect(listener).toHaveBeenCalledWith(remote);
    unsubscribe();
  });

  it('clears live state, sensitive storage and registered query caches in one boundary', () => {
    const clearLive = vi.fn();
    const clearQueries = vi.fn();
    const unregisterLive = registerAuthBoundaryLiveStateClearer(clearLive);
    const unregisterQueries = registerAuthBoundaryQueryCacheClearer(clearQueries);
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT secret');
    const previousGeneration = captureAuthBoundaryGeneration();

    clearLocalAuthBoundary();

    expect(clearLive).toHaveBeenCalledOnce();
    expect(clearQueries).toHaveBeenCalledOnce();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(isAuthBoundaryGenerationCurrent(previousGeneration)).toBe(false);
    unregisterLive();
    unregisterQueries();
  });

  it('clears, broadcasts and requests an authoritative identity after a stale auth cookie response', () => {
    vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
    const unsubscribe = subscribeToAuthBoundaryEvents(vi.fn());
    const reprobe = vi.fn();
    const unregisterReprobe = registerAuthBoundaryIdentityReprober(reprobe);
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT old_user_secret');

    handleStaleAuthCookieResponseBoundary();

    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(reprobe).toHaveBeenCalledOnce();
    expect(FakeBroadcastChannel.instances[0].postMessage).toHaveBeenCalledWith(
      expect.objectContaining({ version: 1, type: 'cookie-changed' }),
    );
    unregisterReprobe();
    unsubscribe();
  });
});

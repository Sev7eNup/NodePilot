import type { HubConnection } from '@microsoft/signalr';
import { useDbHealthStore } from '../stores/dbHealthStore';

// String values of HubConnectionState, written as literals instead of importing the enum: tests
// mock '@microsoft/signalr' wholesale, where a value import is undefined. The enum is a string
// enum, so these literals are its values.
const DISCONNECTED = 'Disconnected';
const CONNECTED = 'Connected';

export interface PersistentConnection {
  /** Stops the loop and clears any pending retry timer. Does not stop the connection itself. */
  dispose: () => void;
  /** Skips the current backoff delay and tries immediately (used as a recovery accelerator). */
  retryNow: () => void;
}

/**
 * Keeps a SignalR connection alive for the lifetime of its owner, covering two gaps in
 * `withAutomaticReconnect()`: a failed initial `start()` is never retried, because `onclose`
 * fires only for connections that once succeeded, and the default policy gives up after a few
 * attempts, so a longer outage would end live updates for good.
 *
 * Health state is never a precondition for an attempt, only an accelerator through `retryNow()`;
 * a stale "ok" would otherwise make the loop conclude there is nothing to wait for.
 * `afterConnect` runs on every fresh connect and every automatic reconnect, because after a full
 * close and `start()` the `onreconnected` hook stays silent and group memberships are gone.
 */
export function connectPersistently(
  connection: HubConnection,
  afterConnect: () => void | Promise<void>,
  onLost?: () => void,
  afterDatabaseRecovery?: () => void | Promise<void>,
): PersistentConnection {
  // Capped backoff with jitter: three hooks x N open tabs would otherwise re-negotiate in
  // lockstep against a backend that just came back.
  const delaysMs = [0, 2_000, 5_000, 10_000, 20_000, 30_000];
  let attempt = 0;
  let disposed = false;
  let inFlight = false;
  let timer: ReturnType<typeof setTimeout> | null = null;

  // Lifecycle hooks are best-effort repair work. A rejected group join must not become an
  // unhandled promise rejection or make a connected transport look like a failed start().
  const runSafely = async (callback: () => void | Promise<void>) => {
    try {
      await callback();
    } catch {
      // The owner handles/logs individual operations; the persistent transport loop stays alive.
    }
  };

  const jitter = (ms: number) => (ms === 0 ? 0 : ms * (0.8 + Math.random() * 0.4));

  const schedule = (delayMs: number) => {
    if (disposed) return;
    if (timer !== null) clearTimeout(timer);
    timer = setTimeout(() => {
      timer = null;
      void tryStart();
    }, jitter(delayMs));
  };

  const tryStart = async () => {
    if (disposed || inFlight) return;
    // withAutomaticReconnect owns the Connecting/Reconnecting states; this loop only ever acts on
    // a connection that is fully down. An undefined state (bare test mock) counts as down.
    const state = (connection as { state?: unknown }).state;
    if (state !== undefined && state !== DISCONNECTED) return;
    inFlight = true;
    try {
      await connection.start();
      attempt = 0;
      await runSafely(afterConnect);
    } catch {
      attempt += 1;
      schedule(delaysMs[Math.min(attempt, delaysMs.length - 1)]);
    } finally {
      inFlight = false;
    }
  };

  // Fires when the automatic-reconnect policy gives up for good; from there on only this loop
  // can bring the connection back. Guarded because tests use minimal connection doubles, while
  // a real HubConnection always has both hooks.
  if (typeof connection.onclose === 'function') {
    connection.onclose(() => {
      if (disposed) return;
      onLost?.();
      attempt = 0;
      schedule(delaysMs[1]);
    });
  }

  // Automatic reconnect cycles transports for a while before onclose fires. Report the loss at
  // the start of that window so callers do not show a connected indicator while no live events
  // can arrive.
  if (typeof connection.onreconnecting === 'function') {
    connection.onreconnecting(() => {
      if (!disposed) onLost?.();
    });
  }

  // Short blips are still handled by withAutomaticReconnect; group re-join etc. must run there too.
  if (typeof connection.onreconnected === 'function') {
    connection.onreconnected(() => {
      if (!disposed) void runSafely(afterConnect);
    });
  }

  // Recovery accelerator: when the health probe reports the database back, stop waiting out the
  // backoff. It is never a precondition; see the doc comment above.
  const unsubscribeHealth = useDbHealthStore.subscribe((state, previous) => {
    if (disposed) return;
    if (state.status === 'ok' && previous.status !== 'ok') {
      // A database outage does not necessarily disconnect an established WebSocket. Then start()
      // has nothing to do, but group joins the hub filter rejected during the outage still need
      // replaying.
      if ((connection as { state?: unknown }).state === CONNECTED && afterDatabaseRecovery)
        void runSafely(afterDatabaseRecovery);
      retryNowInternal();
    }
  });

  const retryNowInternal = () => {
    if (disposed) return;
    if (timer !== null) {
      clearTimeout(timer);
      timer = null;
    }
    void tryStart();
  };

  void tryStart();

  return {
    dispose: () => {
      disposed = true;
      unsubscribeHealth();
      if (timer !== null) {
        clearTimeout(timer);
        timer = null;
      }
    },
    retryNow: retryNowInternal,
  };
}

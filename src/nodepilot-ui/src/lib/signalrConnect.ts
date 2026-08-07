import type { HubConnection } from '@microsoft/signalr';
import { useDbHealthStore } from '../stores/dbHealthStore';

// The string value of HubConnectionState.Disconnected, spelled as a literal instead of importing
// the enum: this module runs under tests that mock '@microsoft/signalr' wholesale, and a value
// import from a mocked module is undefined there — which turned every state comparison into a
// crash. @microsoft/signalr declares the enum as a string enum, so the literal IS the value.
const DISCONNECTED = 'Disconnected';
const CONNECTED = 'Connected';

export interface PersistentConnection {
  /** Stops the loop and clears any pending retry timer. Does NOT stop the connection itself. */
  dispose: () => void;
  /** Skips the current backoff delay and tries immediately (used as a recovery accelerator). */
  retryNow: () => void;
}

/**
 * Keeps a SignalR connection alive for the lifetime of its owner, closing the two holes the bare
 * `withAutomaticReconnect()` setup leaves open:
 *
 * 1. A failed INITIAL `start()` was never retried at all — `onclose` only fires for connections
 *    that once succeeded, so loading a page during an outage left it permanently dead.
 * 2. The default automatic-reconnect policy gives up for good after ~4 attempts (~40 s). Any
 *    database or backend outage longer than that permanently killed live updates, which directly
 *    contradicts "resumes on its own when the database returns" — the connection is the very
 *    channel that carries the recovery moment to the UI.
 *
 * The loop deliberately NEVER reads health state as a precondition for trying: a stale "ok" during
 * a process restart would make it conclude there is nothing to wait for. Health is wired the other
 * way round — a transition back to "ok" only ACCELERATES the next attempt via `retryNow()`.
 *
 * `afterConnect` runs on every fresh connect AND every automatic reconnect. Merging the two
 * call sites is a deliberate behaviour change, not deduplication: after a full close + `start()`,
 * `onreconnected` never fires and hub group memberships are gone, so everything the old
 * `onreconnected` handler did must also happen on a fresh connect.
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

  // Lifecycle hooks are best-effort repair work. A rejected group join must not become an unhandled
  // promise rejection, nor should it make a successfully connected transport look like start() failed.
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

  // Fires when the automatic-reconnect policy gives up for good — from here on this loop is the
  // only thing that will ever bring the connection back. Guarded because the test suites drive
  // this with minimal connection doubles; the real HubConnection always has both hooks.
  if (typeof connection.onclose === 'function') {
    connection.onclose(() => {
      if (disposed) return;
      onLost?.();
      attempt = 0;
      schedule(delaysMs[1]);
    });
  }

  // Automatic reconnect can spend ~40 seconds cycling transports before onclose fires. Surface the
  // loss at the beginning of that window so callers never keep a green "connected" indicator while
  // no live events can arrive.
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

  // Recovery accelerator: the moment the health probe reports the database back, stop waiting out
  // the backoff. Never a precondition — see the doc comment.
  const unsubscribeHealth = useDbHealthStore.subscribe((state, previous) => {
    if (disposed) return;
    if (state.status === 'ok' && previous.status !== 'ok') {
      // A database outage does not necessarily disconnect an established WebSocket. In that case
      // start() correctly has nothing to do, but group joins rejected by the hub filter during the
      // outage still need replaying.
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

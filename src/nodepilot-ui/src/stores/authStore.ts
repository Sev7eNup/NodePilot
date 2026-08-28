import { create } from 'zustand';
import { api, ApiError } from '../api/client';
import type { LoginResponse } from '../types/api';
import { useAiChatStore } from './aiChatStore';
import { useDbHealthStore } from './dbHealthStore';
import {
  bindSensitiveBrowserStateToUser,
  clearLegacySensitiveLocalStorage,
} from '../security/sensitiveBrowserState';
import {
  type AuthBoundaryEvent,
  captureAuthBoundaryGeneration,
  clearAuthBoundaryQueryCaches,
  clearLocalAuthBoundary,
  isAuthBoundaryGenerationCurrent,
  publishAuthenticatedIdentity,
  publishLogoutBoundary,
  registerAuthBoundaryIdentityReprober,
  registerAuthBoundaryLiveStateClearer,
  subscribeToAuthBoundaryEvents,
} from '../security/authBoundary';

const LEGACY_WORKFLOW_CLIPBOARD_KEY = 'np_clipboard';

function clearLegacyWorkflowClipboard(): void {
  // A legacy key that can hold complete workflow nodes, including inline credentials, in
  // sessionStorage. Clipboard data is memory-only now, but an already-open tab may still carry
  // such a value. Clearing it at every authentication boundary keeps the residue from reaching
  // the next identity.
  try {
    sessionStorage.removeItem(LEGACY_WORKFLOW_CLIPBOARD_KEY);
  } catch {
    // Storage can be disabled by browser policy. The clipboard never writes browser storage,
    // so there is no persisted secret to clean in that environment.
  }
}

interface AcceptIdentityOptions {
  /** Remote re-probes suppress this to avoid echoing a cross-tab event back to its sender. */
  broadcastIdentity?: boolean;
  /** Explicit sign-in endpoints are boundaries even if they return the same stable user id. */
  forceBoundary?: boolean;
  /** Internal guard: a response from an older remote probe must never restore stale auth. */
  expectedBoundaryGeneration?: number;
}

/**
 * Auth store. The JWT lives in an httpOnly `np_auth` cookie that JS cannot read, so this store
 * holds only the user-facing fields (username, role) plus an `isAuthenticated` tri-state, letting
 * the router render a loading shell during the initial `/auth/me` probe instead of flashing the
 * login page for a signed-in user. No auth flow touches localStorage and no auth endpoint returns
 * the JWT in its body, so an XSS has no path to exfiltrate a long-lived admin token.
 */
interface AuthState {
  /** Stable user id from `/auth/me`, compared by the edit-lock UI against
   *  `Workflow.checkedOutByUserId`. Null while initializing or anonymous. */
  userId: string | null;
  username: string | null;
  role: string | null;
  /** `null` = still initializing, `true` = signed in, `false` = anonymous */
  isAuthenticated: boolean | null;
  /** Changes on every auth boundary; keys the protected tree so component-local state remounts. */
  authBoundaryEpoch: number;
  /** `setupToken` is only used on a fresh installation: the server's one-shot admin
   *  bootstrap gate (AdminBootstrap) expects it as the `X-Setup-Token` header. */
  login: (username: string, password: string, setupToken?: string) => Promise<void>;
  /** The sole successful-identity transition used by local login, SSO, init and refresh. */
  acceptAuthenticatedIdentity: (identity: LoginResponse, options?: AcceptIdentityOptions) => void;
  /** Clear locally immediately, then revoke server-side. Awaitable for callers that need it. */
  logout: () => Promise<void>;
  /** Probe `/auth/me` via cookie and set authenticated/anonymous accordingly. */
  initialize: (options?: AcceptIdentityOptions) => Promise<void>;
  /** Rotate the cookie; server issues a fresh JWT + CSRF token. */
  refresh: () => Promise<void>;
  /** Best-effort: trigger a rotation so long-lived sessions don't cold-expire. */
  maybeRefresh: () => Promise<void>;
}

export const useAuthStore = create<AuthState>((set, get) => ({
  userId: null,
  username: null,
  role: null,
  isAuthenticated: null,
  authBoundaryEpoch: captureAuthBoundaryGeneration(),

  login: async (username: string, password: string, setupToken?: string) => {
    // Reaching the login form without a clean logout must not carry the previous identity's
    // SQL/AI state into the next session.
    clearLocalAuthBoundary();
    const expectedBoundaryGeneration = captureAuthBoundaryGeneration();
    const response = setupToken
      ? await api.postWithHeaders<LoginResponse>(
          '/auth/login',
          { username, password },
          { 'X-Setup-Token': setupToken },
        )
      : await api.post<LoginResponse>('/auth/login', { username, password });
    // The server set the np_auth and np_csrf cookies on this response. The body carries only the
    // identity (userId/username/role), never the JWT. Bearer callers such as the CLI receive the
    // token when they opt in; the SPA relies solely on the httpOnly cookie.
    get().acceptAuthenticatedIdentity(response, { expectedBoundaryGeneration });
  },

  acceptAuthenticatedIdentity: (identity, options) => {
    if (options?.expectedBoundaryGeneration !== undefined
      && !isAuthBoundaryGenerationCurrent(options.expectedBoundaryGeneration)) return;

    const previous = get();
    const identityChanged = previous.userId !== null && previous.userId !== identity.userId;
    const displayedClaimsChanged = previous.isAuthenticated === true
      && (previous.role !== identity.role || previous.username !== identity.username);
    const requiresFullBoundary = identityChanged || displayedClaimsChanged || options?.forceBoundary;
    if (requiresFullBoundary) {
      // This clears SQL/session keys as well as hydrated AI/query data. Re-bind only after the
      // owner marker has been removed, so subsequent same-user refreshes can preserve safe state.
      clearLocalAuthBoundary();
      bindSensitiveBrowserStateToUser(identity.userId);
    } else if (bindSensitiveBrowserStateToUser(identity.userId)) {
      // The persistence binder removed SQL/AI keys before assigning the new owner. Empty the
      // hydrated store and all server-derived cache entries within the same identity transition.
      useAiChatStore.getState().clearAll();
      clearLegacyWorkflowClipboard();
      clearAuthBoundaryQueryCaches();
    }
    set({
      userId: identity.userId,
      username: identity.username,
      role: identity.role,
      isAuthenticated: true,
    });
    if (options?.broadcastIdentity !== false) publishAuthenticatedIdentity(identity.userId);
  },

  logout: async () => {
    // The click is the boundary: clear locally before waiting for server revocation. Two phases
    // keep another tab from racing its identity probe against the in-flight logout request.
    set({ userId: null, username: null, role: null, isAuthenticated: false });
    clearLocalAuthBoundary();
    publishLogoutBoundary('started');
    // Server revocation remains best effort; local confidentiality does not depend on latency.
    let serverLogoutSucceeded = false;
    try {
      await api.post('/auth/logout');
      serverLogoutSucceeded = true;
    } catch (err) {
      // The server is unreachable. Local cleanup has already run, but the cookie may stay valid
      // server-side until it expires. Warn so the failure is not silent.
      console.warn(
        '[auth] Logout request failed — state cleared locally but cookie may remain valid server-side until it expires (~12h).',
        err,
      );
    } finally {
      publishLogoutBoundary(serverLogoutSucceeded ? 'succeeded' : 'failed');
    }
  },

  initialize: async (options) => {
    const expectedBoundaryGeneration = options?.expectedBoundaryGeneration
      ?? captureAuthBoundaryGeneration();
    const commitOptions: AcceptIdentityOptions = {
      ...options,
      expectedBoundaryGeneration,
    };
    clearLegacyWorkflowClipboard();
    // New builds no longer write sensitive content to localStorage. Remove residue without
    // discarding valid same-user sessionStorage state before /auth/me has identified its owner.
    clearLegacySensitiveLocalStorage();
    // Ask the server who we are; the browser attaches np_auth if present. Success means signed
    // in, 401 means anonymous. The api client intercepts 401s and redirects to /login, but only
    // when not already there, so the LoginPage renders cleanly on first load.
    try {
      const me = commitOptions.broadcastIdentity === false
        ? await api.get<{ id: string; username: string; role: string }>(
            '/auth/me',
            { broadcastUnauthorized: false },
          )
        : await api.get<{ id: string; username: string; role: string }>('/auth/me');
      get().acceptAuthenticatedIdentity(
        { userId: me.id, username: me.username, role: me.role },
        commitOptions,
      );
    } catch (err) {
      // A response belonging to an older auth generation has no authority over the current UI,
      // whether it is a stale 200, 401, or infrastructure failure.
      if (!isAuthBoundaryGenerationCurrent(expectedBoundaryGeneration)) return;
      // A database outage or an unreachable process says nothing about this user's session; the
      // cookie may still be valid. Stay in the loading shell (isAuthenticated stays null) and
      // re-ask as soon as the health probe reports recovery.
      const outage = (err instanceof ApiError && (err.status === 503 || err.code?.startsWith('DATABASE_')))
        || err instanceof TypeError; // fetch network failure: process unreachable or restarting
      if (outage) {
        const unsubscribe = useDbHealthStore.subscribe((state) => {
          if (state.status !== 'ok') return;
          unsubscribe();
          if (!isAuthBoundaryGenerationCurrent(expectedBoundaryGeneration)) return;
          void useAuthStore.getState().initialize(commitOptions);
        });
        return;
      }
      // The real API client has already executed this boundary for a 401. Repeating it is
      // intentionally idempotent and also covers mocked/non-HTTP anonymous initialization.
      clearLocalAuthBoundary();
    }
  },

  refresh: async () => {
    const expectedBoundaryGeneration = captureAuthBoundaryGeneration();
    try {
      const response = await api.post<LoginResponse>('/auth/refresh');
      get().acceptAuthenticatedIdentity(response, { expectedBoundaryGeneration });
    } catch {
      // On a 401, client.ts has already redirected to /login. Nothing to do here.
    }
  },

  maybeRefresh: async () => {
    // The JWT expiry sits in an httpOnly cookie, so the SPA cannot schedule a just-in-time
    // refresh. This stays a no-op: the api client's 401 handler redirects on expiry. Wiring a
    // lazy refresh here would require the backend to emit a plain-text `np_auth_exp` cookie.
    return;
  },
}));

// The API client can observe a 401 without importing this store (which would create a module
// cycle). Register the in-memory half here; the boundary coordinator owns browser + query cleanup.
registerAuthBoundaryLiveStateClearer(() => {
  useAuthStore.setState({
    userId: null,
    username: null,
    role: null,
    isAuthenticated: false,
    authBoundaryEpoch: captureAuthBoundaryGeneration(),
  });
  clearLegacyWorkflowClipboard();
});
registerAuthBoundaryLiveStateClearer(() => useAiChatStore.getState().clearAll());

let synchronizationReferences = 0;
let stopSynchronization: (() => void) | null = null;
let stopCookieIdentityReprober: (() => void) | null = null;
let remoteProbeInFlight: Promise<void> | null = null;
let remoteProbeQueued = false;
let remoteLogoutPending = false;

function reProbeRemoteIdentity(): void {
  if (remoteProbeInFlight) {
    remoteProbeQueued = true;
    return;
  }

  // Protected content (including QueryPane component-local state) unmounts before /auth/me can
  // issue a request or accept the next identity. Defer one task so React can commit that external
  // store update and unmount protected component-local state before any next-user request starts.
  useAuthStore.setState({ userId: null, username: null, role: null, isAuthenticated: null });
  const expectedBoundaryGeneration = captureAuthBoundaryGeneration();
  const probe = new Promise<void>((resolve) => globalThis.setTimeout(resolve, 0)).then(async () => {
    if (remoteLogoutPending || !isAuthBoundaryGenerationCurrent(expectedBoundaryGeneration)) return;
    await useAuthStore.getState().initialize({
      broadcastIdentity: false,
      expectedBoundaryGeneration,
    });
  });
  remoteProbeInFlight = probe.finally(() => {
    remoteProbeInFlight = null;
    if (remoteLogoutPending) {
      // A probe started before another tab clicked logout must not restore its old identity.
      clearLocalAuthBoundary();
      return;
    }
    if (remoteProbeQueued) {
      remoteProbeQueued = false;
      reProbeRemoteIdentity();
    }
  });
}

function handleRemoteAuthBoundary(event: AuthBoundaryEvent): void {
  if (event.type === 'identity') {
    // Always treat a successful login as a boundary, even for the same user id: roles and the
    // server-side security stamp may have changed. Clear synchronously, then trust only /auth/me.
    remoteLogoutPending = false;
    clearLocalAuthBoundary();
    reProbeRemoteIdentity();
    return;
  }

  if (event.type === 'logout' && event.phase === 'started') {
    remoteLogoutPending = true;
    clearLocalAuthBoundary();
    return;
  }

  if (event.type === 'cookie-changed') {
    // Another tab received a stale auth response whose Set-Cookie side effect cannot be undone.
    // During a two-phase logout, wait for explicit success/failure; otherwise trust only /auth/me.
    clearLocalAuthBoundary();
    if (!remoteLogoutPending) reProbeRemoteIdentity();
    return;
  }

  if (event.type === 'logout' && event.phase === 'failed') {
    // The shared cookie may still be valid, but logout intent wins: release the two-phase lock
    // without asking /auth/me to authenticate the old identity again.
    remoteLogoutPending = false;
    clearLocalAuthBoundary();
    return;
  }

  // A successful logout or a 401 discards local data, while the httpOnly cookie remains the source
  // of truth. Re-probe once without echoing its success/failure back into a tab-to-tab event loop.
  remoteLogoutPending = false;
  clearLocalAuthBoundary();
  reProbeRemoteIdentity();
}

/** Start one cross-tab listener; reference counting keeps tests/HMR from duplicating handlers. */
export function startAuthBoundarySynchronization(): () => void {
  synchronizationReferences++;
  if (!stopSynchronization) {
    stopSynchronization = subscribeToAuthBoundaryEvents(handleRemoteAuthBoundary);
    stopCookieIdentityReprober = registerAuthBoundaryIdentityReprober(reProbeRemoteIdentity);
  }

  let released = false;
  return () => {
    if (released) return;
    released = true;
    synchronizationReferences--;
    if (synchronizationReferences === 0) {
      stopSynchronization?.();
      stopSynchronization = null;
      stopCookieIdentityReprober?.();
      stopCookieIdentityReprober = null;
      remoteProbeQueued = false;
      remoteLogoutPending = false;
    }
  };
}


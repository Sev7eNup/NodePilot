import { create } from 'zustand';
import { api, ApiError } from '../api/client';
import type { LoginResponse } from '../types/api';
import { useAiChatStore } from './aiChatStore';
import { useDbHealthStore } from './dbHealthStore';

const LEGACY_WORKFLOW_CLIPBOARD_KEY = 'np_clipboard';

function clearLegacyWorkflowClipboard(): void {
  // Releases before 1.2.5 persisted complete workflow nodes (including inline credentials)
  // in sessionStorage. New clipboard data is memory-only, but an upgrade can leave an old
  // value behind in an already-open tab. Treat every authentication boundary as a migration
  // point so that residue can never cross to the next identity.
  try {
    sessionStorage.removeItem(LEGACY_WORKFLOW_CLIPBOARD_KEY);
  } catch {
    // Storage can be disabled by browser policy. The current clipboard implementation never
    // writes browser storage, so there is no new persisted secret to clean in that environment.
  }
}

/**
 * Auth store (rewritten by a security-audit fix): the JWT now lives in an httpOnly `np_auth` cookie
 * that JS cannot read. This store holds only the user-facing fields (username, role)
 * plus an `isAuthenticated` tri-state so the router can render a loading shell during
 * the initial `/auth/me` probe without flashing the login page for a signed-in user.
 *
 * No localStorage is touched anywhere in the auth flow, AND no auth endpoint returns the
 * JWT in its response body for the browser (login/refresh/windows all yield identity only)
 * — so a future XSS has no path to exfiltrate a long-lived, off-host admin token.
 */
interface AuthState {
  /** Stable user id from `/auth/me` — used by the edit-lock UI to compare against
   *  `Workflow.checkedOutByUserId`. Null while initializing or anonymous. */
  userId: string | null;
  username: string | null;
  role: string | null;
  /** `null` = still initializing, `true` = signed in, `false` = anonymous */
  isAuthenticated: boolean | null;
  /** `setupToken` is only used on a fresh installation: the server's one-shot admin
   *  bootstrap gate (AdminBootstrap) expects it as the `X-Setup-Token` header. */
  login: (username: string, password: string, setupToken?: string) => Promise<void>;
  /** Revoke server-side THEN clear local state. Awaitable. */
  logout: () => Promise<void>;
  /** Probe `/auth/me` via cookie and set authenticated/anonymous accordingly. */
  initialize: () => Promise<void>;
  /** Rotate the cookie; server issues a fresh JWT + CSRF token. */
  refresh: () => Promise<void>;
  /** Best-effort: trigger a rotation so long-lived sessions don't cold-expire. */
  maybeRefresh: () => Promise<void>;
}

export const useAuthStore = create<AuthState>((set) => ({
  userId: null,
  username: null,
  role: null,
  isAuthenticated: null,

  login: async (username: string, password: string, setupToken?: string) => {
    clearLegacyWorkflowClipboard();
    const response = setupToken
      ? await api.postWithHeaders<LoginResponse>(
          '/auth/login',
          { username, password },
          { 'X-Setup-Token': setupToken },
        )
      : await api.post<LoginResponse>('/auth/login', { username, password });
    // The server set np_auth + np_csrf cookies on this response. The body carries only our
    // identity (userId/username/role) — never the JWT. The token reaches Bearer callers
    // (CLI/API) only, and only when they opt in; the SPA relies solely on the httpOnly cookie.
    set({
      userId: response.userId,
      username: response.username,
      role: response.role,
      isAuthenticated: true,
    });
  },

  logout: async () => {
    // Revoke server-side first so a stolen cookie copy can't be used after the user
    // clicks "sign out". If the call fails (offline), fall through and clear local
    // state anyway — the user shouldn't be stuck "signed in" because of network.
    try {
      await api.post('/auth/logout');
    } catch (err) {
      // Server unreachable — local cleanup below still runs so the UI is usable, but the
      // cookie may remain valid server-side until it expires (~12h). Warn so this isn't silent.
      console.warn(
        '[auth] Logout request failed — state cleared locally but cookie may remain valid server-side until it expires (~12h).',
        err,
      );
    }
    set({ userId: null, username: null, role: null, isAuthenticated: false });
    clearLegacyWorkflowClipboard();
    // AI chat histories are per-user — clear them from memory on logout so the next
    // person to sign in on this browser never sees someone else's conversation.
    useAiChatStore.getState().clearAll();
  },

  initialize: async () => {
    clearLegacyWorkflowClipboard();
    // Ask the server who we are. The browser auto-attaches np_auth if present.
    // Success → signed in. 401 → anonymous (the api client intercepts 401s and triggers
    // a /login redirect, but only when we're not already on /login, so the LoginPage
    // renders cleanly on first load).
    try {
      const me = await api.get<{ id: string; username: string; role: string }>('/auth/me');
      set({ userId: me.id, username: me.username, role: me.role, isAuthenticated: true });
    } catch (err) {
      // A database outage or an unreachable process says NOTHING about this user's session — the
      // cookie may be perfectly valid. The old bare catch signed the user out, so a page reload
      // during an outage ejected them to a login form that itself answers 503 (and, before it
      // carried a third branch, blamed their credentials). Instead: stay in the loading shell
      // (isAuthenticated stays null) and re-ask the moment the health probe reports recovery.
      const outage = (err instanceof ApiError && (err.status === 503 || err.code?.startsWith('DATABASE_')))
        || err instanceof TypeError; // fetch network failure — process unreachable/restarting
      if (outage) {
        const unsubscribe = useDbHealthStore.subscribe((state) => {
          if (state.status !== 'ok') return;
          unsubscribe();
          void useAuthStore.getState().initialize();
        });
        return;
      }
      set({ userId: null, username: null, role: null, isAuthenticated: false });
    }
  },

  refresh: async () => {
    try {
      const response = await api.post<LoginResponse>('/auth/refresh');
      set({
        userId: response.userId,
        username: response.username,
        role: response.role,
        isAuthenticated: true,
      });
    } catch {
      // Refresh failure → client.ts already redirected to /login on 401. Nothing to do.
    }
  },

  maybeRefresh: async () => {
    // Without access to the JWT expiry (it's in an httpOnly cookie), the SPA can no longer
    // schedule a just-in-time refresh. A no-op is the honest behavior: 12 h JWT lifetime
    // covers most sessions, and the api client's 401 handler redirects cleanly on expiry.
    // If a future need emerges (workflows with 12+ h sessions), the backend can emit a
    // plain-text `np_auth_exp` cookie and we wire a lazy refresh here.
    return;
  },
}));

// Re-export a void helper so existing call sites that treat `isAuthenticated` as boolean
// get the right fallback when init hasn't completed yet (treat "unknown" as "not yet signed in"
// for guard purposes; ProtectedRoute renders a loader for null instead).
export function isAuthResolved(): boolean {
  return useAuthStore.getState().isAuthenticated !== null;
}

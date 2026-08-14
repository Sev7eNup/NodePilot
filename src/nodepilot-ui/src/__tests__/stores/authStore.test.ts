import { afterEach, describe, it, expect, beforeEach, vi } from 'vitest';
import { startAuthBoundarySynchronization, useAuthStore } from '../../stores/authStore';
import { useAiChatStore, aiChatScopeKey } from '../../stores/aiChatStore';
import { queryClient } from '../../queryClient';
import {
  AUTH_BOUNDARY_STORAGE_KEY,
  clearLocalAuthBoundary,
  type AuthBoundaryEvent,
} from '../../security/authBoundary';
import {
  AI_CHAT_STORAGE_KEY,
  DB_ADMIN_QUERY_DRAFT_KEY,
  DB_ADMIN_QUERY_HISTORY_KEY,
  DB_ADMIN_QUERY_MODE_KEY,
} from '../../security/sensitiveBrowserState';

// Mock the api module — `post`, `get` and `postWithHeaders` (setup-token login) are
// used by the auth flow now.
vi.mock('../../api/client', () => {
  // The store branches on `err instanceof ApiError`, so the mock must export a real class —
  // an auto-mocked undefined would crash the instanceof check itself.
  class ApiError extends Error {
    status: number;
    code?: string;
    constructor(message: string, status: number, code?: string) {
      super(message);
      this.status = status;
      this.code = code;
    }
  }
  return {
    api: {
      post: vi.fn(),
      get: vi.fn(),
      postWithHeaders: vi.fn(),
    },
    ApiError,
    isDatabaseOutageError: (err: unknown) =>
      err instanceof ApiError && (err.code === 'DATABASE_UNAVAILABLE' || err.code === 'DATABASE_TIMEOUT'),
  };
});

import { api } from '../../api/client';

describe('authStore (cookie-based, audit H-5)', () => {
  const synchronizationStops: Array<() => void> = [];

  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    useAiChatStore.setState({ messagesByThread: {}, threadsByScope: {}, activeThreadByScope: {} });
    sessionStorage.clear();
    queryClient.clear();
    // Reset store state to a known "pre-init" shape (matches production bundle load).
    useAuthStore.setState({
      userId: null,
      username: null,
      role: null,
      isAuthenticated: null,
    });
  });

  afterEach(() => {
    while (synchronizationStops.length > 0) synchronizationStops.pop()?.();
    vi.unstubAllGlobals();
  });

  it('login_success_setsAuthenticated', async () => {
    // The browser login response carries identity only — never the JWT. The SPA stores
    // username + role and relies on the httpOnly np_auth cookie for auth.
    const mockResponse = { userId: 'u-1', username: 'admin', role: 'Admin' };
    vi.mocked(api.post).mockResolvedValueOnce(mockResponse);

    await useAuthStore.getState().login('admin', 'password');

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(true);
    expect(state.username).toBe('admin');
    expect(state.role).toBe('Admin');
    // No token field in state — the cookie is the source of truth.
    expect((state as unknown as { token?: string }).token).toBeUndefined();
  });

  it('login_withSetupToken_sendsXSetupTokenHeader', async () => {
    // First-login bootstrap: the one-shot token travels as the X-Setup-Token header,
    // never inside the JSON body.
    const mockResponse = { userId: 'u-1', username: 'admin', role: 'Admin' };
    vi.mocked(api.postWithHeaders).mockResolvedValueOnce(mockResponse);

    await useAuthStore.getState().login('admin', 'password', 'one-shot-token');

    expect(api.postWithHeaders).toHaveBeenCalledWith(
      '/auth/login',
      { username: 'admin', password: 'password' },
      { 'X-Setup-Token': 'one-shot-token' },
    );
    expect(api.post).not.toHaveBeenCalled();
    expect(useAuthStore.getState().isAuthenticated).toBe(true);
  });

  it('logout_clearsStateAndPostsToServer', async () => {
    useAuthStore.setState({
      username: 'admin',
      role: 'Admin',
      isAuthenticated: true,
    });
    vi.mocked(api.post).mockResolvedValueOnce(undefined);

    await useAuthStore.getState().logout();

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(false);
    expect(state.username).toBeNull();
    expect(state.role).toBeNull();
    expect(api.post).toHaveBeenCalledWith('/auth/logout');
  });

  it('logout_clearsUiSensitiveStateAndQueryCache_beforeServerRequestSettles', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-1',
      username: 'admin',
      role: 'Admin',
    });
    const scope = aiChatScopeKey('u-1', 'wf-1');
    const threadId = useAiChatStore.getState().newThread(scope, 'Sensitive');
    useAiChatStore.getState().updateMessages(scope, threadId, () => [
      { role: 'user', content: 'customer secret' },
    ]);
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT token FROM integrations');
    queryClient.setQueryData(['previous-user'], { secret: true });

    let settleLogout!: () => void;
    vi.mocked(api.post).mockReturnValueOnce(new Promise<void>((resolve) => {
      settleLogout = resolve;
    }));

    const pendingLogout = useAuthStore.getState().logout();

    expect(useAuthStore.getState().isAuthenticated).toBe(false);
    expect(useAuthStore.getState().userId).toBeNull();
    expect(useAiChatStore.getState().messagesByThread).toEqual({});
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(queryClient.getQueryData(['previous-user'])).toBeUndefined();
    expect(api.post).toHaveBeenCalledWith('/auth/logout');

    settleLogout();
    await pendingLogout;
  });

  it('logout_removesLegacyWorkflowClipboardFromSessionStorage', async () => {
    sessionStorage.setItem('np_clipboard', JSON.stringify({
      nodes: [{ data: { config: { apiKey: 'legacy-inline-secret' } } }],
      edges: [],
    }));
    vi.mocked(api.post).mockResolvedValueOnce(undefined);

    await useAuthStore.getState().logout();

    expect(sessionStorage.getItem('np_clipboard')).toBeNull();
  });

  it('logout_clearsPersistedSqlAndAiStateFromMemoryAndBrowserStorage', async () => {
    const scope = aiChatScopeKey('u-1', 'wf-1');
    const threadId = useAiChatStore.getState().newThread(scope, 'Chat 1');
    useAiChatStore.getState().updateMessages(scope, threadId, () => [
      { role: 'user', content: 'customer secret' },
    ]);
    sessionStorage.setItem(DB_ADMIN_QUERY_HISTORY_KEY, '["SELECT password FROM users"]');
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT token FROM integrations');
    sessionStorage.setItem(DB_ADMIN_QUERY_MODE_KEY, 'write');
    // Residue from releases which used localStorage must be removed during the same boundary.
    localStorage.setItem(AI_CHAT_STORAGE_KEY, 'legacy-chat');
    localStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'legacy-sql');
    vi.mocked(api.post).mockResolvedValueOnce(undefined);

    await useAuthStore.getState().logout();

    expect(useAiChatStore.getState().messagesByThread).toEqual({});
    for (const key of [AI_CHAT_STORAGE_KEY, DB_ADMIN_QUERY_HISTORY_KEY, DB_ADMIN_QUERY_DRAFT_KEY, DB_ADMIN_QUERY_MODE_KEY]) {
      expect(sessionStorage.getItem(key)).toBeNull();
      expect(localStorage.getItem(key)).toBeNull();
    }
  });

  it('login_removesLegacyWorkflowClipboardEvenWhenPriorLogoutWasMissed', async () => {
    sessionStorage.setItem('np_clipboard', JSON.stringify({
      nodes: [{ data: { config: { apiKey: 'previous-user-secret' } } }],
      edges: [],
    }));
    vi.mocked(api.post).mockResolvedValueOnce({ userId: 'u-2', username: 'next', role: 'Viewer' });

    await useAuthStore.getState().login('next', 'password');

    expect(sessionStorage.getItem('np_clipboard')).toBeNull();
  });

  it('logout_serverUnreachable_stillClearsLocalState', async () => {
    useAuthStore.setState({
      username: 'admin',
      role: 'Admin',
      isAuthenticated: true,
    });
    vi.stubGlobal('BroadcastChannel', undefined);
    const storageWrite = vi.spyOn(Storage.prototype, 'setItem');
    vi.mocked(api.post).mockRejectedValueOnce(new Error('network down'));
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

    await useAuthStore.getState().logout();

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(false);
    expect(warnSpy).toHaveBeenCalled();
    const logoutPhases = storageWrite.mock.calls
      .filter(([key]) => key === AUTH_BOUNDARY_STORAGE_KEY)
      .map(([, value]) => JSON.parse(String(value)) as AuthBoundaryEvent)
      .filter((event) => event.type === 'logout')
      .map((event) => event.phase);
    expect(logoutPhases).toEqual(['started', 'failed']);
    warnSpy.mockRestore();
    storageWrite.mockRestore();
  });

  it('initialize_withValidCookie_restoresState', async () => {
    // Backend /auth/me returns the current user when the np_auth cookie validates.
    vi.mocked(api.get).mockResolvedValueOnce({ id: 'u-test', username: 'testuser', role: 'Operator' });

    await useAuthStore.getState().initialize();

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(true);
    expect(state.username).toBe('testuser');
    expect(state.role).toBe('Operator');
    expect(api.get).toHaveBeenCalledWith('/auth/me');
  });

  it('initialize_noCookie_setsAnonymous', async () => {
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT previous_user_secret');
    localStorage.setItem(AI_CHAT_STORAGE_KEY, 'legacy-chat');
    vi.mocked(api.get).mockRejectedValueOnce(new Error('Unauthorized'));

    await useAuthStore.getState().initialize();

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(false);
    expect(state.username).toBeNull();
    expect(state.role).toBeNull();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(localStorage.getItem(AI_CHAT_STORAGE_KEY)).toBeNull();
  });

  it('initialize_secondUser_discardsFirstUsersSqlAndAiState', async () => {
    vi.mocked(api.get)
      .mockResolvedValueOnce({ id: 'u-1', username: 'first', role: 'Admin' })
      .mockResolvedValueOnce({ id: 'u-2', username: 'second', role: 'Viewer' });

    await useAuthStore.getState().initialize();
    const scope = aiChatScopeKey('u-1', 'wf-1');
    const threadId = useAiChatStore.getState().newThread(scope, 'Chat 1');
    useAiChatStore.getState().updateMessages(scope, threadId, () => [
      { role: 'user', content: 'first-user-only' },
    ]);
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT first_user_only');
    sessionStorage.setItem(DB_ADMIN_QUERY_HISTORY_KEY, '["SELECT first_user_only"]');
    queryClient.setQueryData(['first-user-api-result'], { secret: 'cached' });

    await useAuthStore.getState().initialize();

    expect(useAuthStore.getState().userId).toBe('u-2');
    expect(useAiChatStore.getState().messagesByThread).toEqual({});
    expect(sessionStorage.getItem(AI_CHAT_STORAGE_KEY) ?? '').not.toContain('first-user-only');
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_HISTORY_KEY)).toBeNull();
    expect(queryClient.getQueryData(['first-user-api-result'])).toBeUndefined();
  });

  it('initialize_staleInitialProbeCannotOverwriteANewerRemoteIdentity', async () => {
    let resolveInitial!: (identity: { id: string; username: string; role: string }) => void;
    const staleInitialResponse = new Promise<{ id: string; username: string; role: string }>((resolve) => {
      resolveInitial = resolve;
    });
    vi.mocked(api.get)
      .mockReturnValueOnce(staleInitialResponse)
      .mockResolvedValueOnce({ id: 'u-b', username: 'bob', role: 'Operator' });

    vi.stubGlobal('BroadcastChannel', undefined);
    synchronizationStops.push(startAuthBoundarySynchronization());
    const initialProbe = useAuthStore.getState().initialize();
    expect(api.get).toHaveBeenCalledWith('/auth/me');

    const remoteIdentity: AuthBoundaryEvent = {
      version: 1,
      type: 'identity',
      userId: 'u-b',
      sourceId: 'another-tab',
      eventId: 'newer-login-during-initial-probe',
    };
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(remoteIdentity),
    }));

    await vi.waitFor(() => expect(useAuthStore.getState()).toMatchObject({
      userId: 'u-b',
      username: 'bob',
      isAuthenticated: true,
    }));
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT user_b_data');

    resolveInitial({ id: 'u-a', username: 'alice', role: 'Admin' });
    await initialProbe;

    expect(useAuthStore.getState()).toMatchObject({
      userId: 'u-b',
      username: 'bob',
      role: 'Operator',
      isAuthenticated: true,
    });
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBe('SELECT user_b_data');
  });

  it('initialize_staleFailureCannotClearANewerRemoteIdentity', async () => {
    let rejectInitial!: (error: Error) => void;
    const staleInitialFailure = new Promise<never>((_, reject) => {
      rejectInitial = reject;
    });
    vi.mocked(api.get)
      .mockReturnValueOnce(staleInitialFailure)
      .mockResolvedValueOnce({ id: 'u-b', username: 'bob', role: 'Operator' });

    vi.stubGlobal('BroadcastChannel', undefined);
    synchronizationStops.push(startAuthBoundarySynchronization());
    const initialProbe = useAuthStore.getState().initialize();
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify({
        version: 1,
        type: 'identity',
        userId: 'u-b',
        sourceId: 'another-tab',
        eventId: 'newer-login-before-stale-failure',
      } satisfies AuthBoundaryEvent),
    }));
    await vi.waitFor(() => expect(useAuthStore.getState().userId).toBe('u-b'));
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT still_user_b');

    rejectInitial(new Error('Unauthorized'));
    await initialProbe;

    expect(useAuthStore.getState()).toMatchObject({ userId: 'u-b', isAuthenticated: true });
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBe('SELECT still_user_b');
  });

  it('login_responseCannotCommitAfterANewerBoundary', async () => {
    let resolveLogin!: (identity: { userId: string; username: string; role: string }) => void;
    const staleLoginResponse = new Promise<{ userId: string; username: string; role: string }>((resolve) => {
      resolveLogin = resolve;
    });
    vi.mocked(api.post).mockReturnValueOnce(staleLoginResponse);

    const login = useAuthStore.getState().login('alice', 'password');
    clearLocalAuthBoundary();
    resolveLogin({ userId: 'u-a', username: 'alice', role: 'Admin' });
    await login;

    expect(useAuthStore.getState().isAuthenticated).toBe(false);
    expect(useAuthStore.getState().userId).toBeNull();
  });

  it('forcedSameUserSignIn_discardsSqlStateDespiteMatchingOwnerMarker', () => {
    const identity = { userId: 'u-1', username: 'alice', role: 'Admin' };
    useAuthStore.getState().acceptAuthenticatedIdentity(identity);
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT same_user_previous_session');
    sessionStorage.setItem(DB_ADMIN_QUERY_HISTORY_KEY, '["SELECT same_user_previous_session"]');

    useAuthStore.getState().acceptAuthenticatedIdentity(identity, { forceBoundary: true });

    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_HISTORY_KEY)).toBeNull();
  });

  it('remoteIdentity_alwaysClearsStateAndReprobes_withoutRebroadcasting', async () => {
    // Same user id is deliberate: a new login can still carry a different role/security stamp.
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-1',
      username: 'alice',
      role: 'Admin',
    });
    const scope = aiChatScopeKey('u-1', 'wf-1');
    const threadId = useAiChatStore.getState().newThread(scope, 'Admin work');
    useAiChatStore.getState().updateMessages(scope, threadId, () => [
      { role: 'user', content: 'admin-only prompt' },
    ]);
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'DELETE FROM audit_log');
    queryClient.setQueryData(['admin-only'], { rows: ['secret'] });
    vi.mocked(api.get).mockResolvedValueOnce({ id: 'u-1', username: 'alice', role: 'Viewer' });

    vi.stubGlobal('BroadcastChannel', undefined);
    synchronizationStops.push(startAuthBoundarySynchronization());
    const storageWrite = vi.spyOn(Storage.prototype, 'setItem');
    storageWrite.mockClear();
    const remoteEvent: AuthBoundaryEvent = {
      version: 1,
      type: 'identity',
      userId: 'u-1',
      sourceId: 'another-tab',
      eventId: 'same-user-new-session',
    };
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(remoteEvent),
    }));

    // The protected tree is unmounted synchronously, before the async identity response lands.
    expect(useAuthStore.getState().isAuthenticated).toBeNull();
    expect(api.get).not.toHaveBeenCalled();
    expect(useAiChatStore.getState().messagesByThread).toEqual({});
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(queryClient.getQueryData(['admin-only'])).toBeUndefined();

    await vi.waitFor(() => {
      expect(useAuthStore.getState()).toMatchObject({
        userId: 'u-1',
        role: 'Viewer',
        isAuthenticated: true,
      });
    });
    expect(api.get).toHaveBeenCalledWith('/auth/me', { broadcastUnauthorized: false });
    expect(storageWrite).not.toHaveBeenCalledWith(
      AUTH_BOUNDARY_STORAGE_KEY,
      expect.any(String),
    );
  });

  it('remoteLogout_clearsImmediately_thenReprobesOnlyAfterServerLogoutSettles', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-1',
      username: 'alice',
      role: 'Admin',
    });
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT private_data');
    vi.mocked(api.get).mockRejectedValueOnce(new Error('Unauthorized'));

    vi.stubGlobal('BroadcastChannel', undefined);
    synchronizationStops.push(startAuthBoundarySynchronization());
    const started: AuthBoundaryEvent = {
      version: 1,
      type: 'logout',
      phase: 'started',
      sourceId: 'another-tab',
      eventId: 'logout-started',
    };
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(started),
    }));

    expect(useAuthStore.getState().isAuthenticated).toBe(false);
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(api.get).not.toHaveBeenCalled();

    const settled: AuthBoundaryEvent = {
      ...started,
      phase: 'succeeded',
      eventId: 'logout-succeeded',
    };
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(settled),
    }));

    await vi.waitFor(() => expect(api.get).toHaveBeenCalledWith(
      '/auth/me',
      { broadcastUnauthorized: false },
    ));
    await vi.waitFor(() => expect(useAuthStore.getState().isAuthenticated).toBe(false));
  });

  it('remoteLogout_failed_releasesPendingStateButNeverReauthenticatesTheOldCookie', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-1',
      username: 'alice',
      role: 'Admin',
    });
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT private_data');

    vi.stubGlobal('BroadcastChannel', undefined);
    synchronizationStops.push(startAuthBoundarySynchronization());
    const started: AuthBoundaryEvent = {
      version: 1,
      type: 'logout',
      phase: 'started',
      sourceId: 'another-tab',
      eventId: 'failed-logout-started',
    };
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(started),
    }));

    expect(useAuthStore.getState().isAuthenticated).toBe(false);
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(api.get).not.toHaveBeenCalled();

    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify({
        ...started,
        phase: 'failed',
        eventId: 'failed-logout-finished',
      } satisfies AuthBoundaryEvent),
    }));

    // A failed server logout can leave the old cookie valid. The explicit logout intent wins:
    // releasing the cross-tab pending flag must not probe /auth/me and remount that old user.
    await new Promise<void>((resolve) => globalThis.setTimeout(resolve, 0));
    expect(api.get).not.toHaveBeenCalled();
    expect(useAuthStore.getState()).toMatchObject({
      userId: null,
      username: null,
      role: null,
      isAuthenticated: false,
    });
  });

  it('remoteUnauthorized_clearsThenReprobesSoAStillValidCookieIsNotLoggedOut', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-1',
      username: 'alice',
      role: 'Operator',
    });
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT private_data');
    vi.mocked(api.get).mockResolvedValueOnce({ id: 'u-1', username: 'alice', role: 'Operator' });

    vi.stubGlobal('BroadcastChannel', undefined);
    synchronizationStops.push(startAuthBoundarySynchronization());
    const unauthorized: AuthBoundaryEvent = {
      version: 1,
      type: 'unauthorized',
      sourceId: 'another-tab',
      eventId: 'remote-401',
    };
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(unauthorized),
    }));

    expect(useAuthStore.getState().isAuthenticated).toBeNull();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    await vi.waitFor(() => expect(useAuthStore.getState().isAuthenticated).toBe(true));
    expect(useAuthStore.getState().userId).toBe('u-1');
  });

  it('remoteCookieChanged_clearsThenAcceptsOnlyTheSharedCookiesAuthoritativeIdentity', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-a',
      username: 'alice',
      role: 'Admin',
    });
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT user_a_private_data');
    queryClient.setQueryData(['user-a-private'], { secret: true });
    vi.mocked(api.get).mockResolvedValueOnce({
      id: 'u-cookie-owner',
      username: 'cookie-owner',
      role: 'Viewer',
    });

    vi.stubGlobal('BroadcastChannel', undefined);
    synchronizationStops.push(startAuthBoundarySynchronization());
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify({
        version: 1,
        type: 'cookie-changed',
        sourceId: 'another-tab',
        eventId: 'stale-login-set-cookie',
      } satisfies AuthBoundaryEvent),
    }));

    expect(useAuthStore.getState().isAuthenticated).toBeNull();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(queryClient.getQueryData(['user-a-private'])).toBeUndefined();
    await vi.waitFor(() => expect(useAuthStore.getState()).toMatchObject({
      userId: 'u-cookie-owner',
      username: 'cookie-owner',
      role: 'Viewer',
      isAuthenticated: true,
    }));
    expect(api.get).toHaveBeenCalledWith('/auth/me', { broadcastUnauthorized: false });
  });

  it('remoteCookieChanged_waitsForAnInFlightRemoteLogoutToSettleBeforeReprobing', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-a', username: 'alice', role: 'Admin',
    });
    vi.mocked(api.get).mockRejectedValueOnce(new Error('Unauthorized'));
    vi.stubGlobal('BroadcastChannel', undefined);
    synchronizationStops.push(startAuthBoundarySynchronization());

    const dispatch = (event: AuthBoundaryEvent) => window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(event),
    }));
    dispatch({
      version: 1,
      type: 'logout',
      phase: 'started',
      sourceId: 'another-tab',
      eventId: 'logout-started-before-cookie-event',
    });
    dispatch({
      version: 1,
      type: 'cookie-changed',
      sourceId: 'another-tab',
      eventId: 'stale-logout-cookie-event',
    });

    expect(api.get).not.toHaveBeenCalled();
    expect(useAuthStore.getState().isAuthenticated).toBe(false);

    dispatch({
      version: 1,
      type: 'logout',
      phase: 'succeeded',
      sourceId: 'another-tab',
      eventId: 'logout-succeeded-after-cookie-event',
    });
    await vi.waitFor(() => expect(api.get).toHaveBeenCalledWith(
      '/auth/me',
      { broadcastUnauthorized: false },
    ));
  });

  it('remoteLogout_startedRejectsAnOlderInFlightIdentityProbe', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-a',
      username: 'alice',
      role: 'Admin',
    });
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT must_not_return');
    queryClient.setQueryData(['stale-probe'], { secret: true });

    let resolveIdentity!: (identity: { id: string; username: string; role: string }) => void;
    const pendingIdentity = new Promise<{ id: string; username: string; role: string }>((resolve) => {
      resolveIdentity = resolve;
    });
    vi.mocked(api.get).mockReturnValueOnce(pendingIdentity);
    vi.stubGlobal('BroadcastChannel', undefined);
    synchronizationStops.push(startAuthBoundarySynchronization());

    const identityEvent: AuthBoundaryEvent = {
      version: 1,
      type: 'identity',
      userId: 'u-b',
      sourceId: 'another-tab',
      eventId: 'identity-before-logout',
    };
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(identityEvent),
    }));
    expect(useAuthStore.getState().isAuthenticated).toBeNull();
    await vi.waitFor(() => expect(api.get).toHaveBeenCalledWith(
      '/auth/me',
      { broadcastUnauthorized: false },
    ));

    const logoutStarted: AuthBoundaryEvent = {
      version: 1,
      type: 'logout',
      phase: 'started',
      sourceId: 'another-tab',
      eventId: 'logout-during-probe',
    };
    window.dispatchEvent(new StorageEvent('storage', {
      key: AUTH_BOUNDARY_STORAGE_KEY,
      newValue: JSON.stringify(logoutStarted),
    }));
    expect(useAuthStore.getState().isAuthenticated).toBe(false);

    resolveIdentity({ id: 'u-b', username: 'bob', role: 'Operator' });
    await pendingIdentity;
    await Promise.resolve();
    await Promise.resolve();

    expect(useAuthStore.getState().isAuthenticated).toBe(false);
    expect(useAuthStore.getState().userId).toBeNull();
    expect(useAuthStore.getState().username).toBeNull();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(queryClient.getQueryData(['stale-probe'])).toBeUndefined();
  });

  it('login_failure_throwsError', async () => {
    vi.mocked(api.post).mockRejectedValueOnce(new Error('Invalid credentials'));

    await expect(useAuthStore.getState().login('admin', 'wrong')).rejects.toThrow('Invalid credentials');

    const state = useAuthStore.getState();
    // isAuthenticated stays at its pre-login value (null or false); critically, no auth granted.
    expect(state.isAuthenticated).not.toBe(true);
  });

  it('refresh_success_updatesState', async () => {
    const mockResponse = { userId: 'u-1', username: 'admin', role: 'Admin' };
    vi.mocked(api.post).mockResolvedValueOnce(mockResponse);

    await useAuthStore.getState().refresh();

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(true);
    expect(state.username).toBe('admin');
    expect(api.post).toHaveBeenCalledWith('/auth/refresh');
  });

  it('refresh_sameUserRoleChange_appliesFullBoundaryIncludingSqlAndQueryCache', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-1',
      username: 'admin',
      role: 'Admin',
    });
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'DELETE FROM users');
    sessionStorage.setItem(DB_ADMIN_QUERY_HISTORY_KEY, '["DELETE FROM users"]');
    queryClient.setQueryData(['admin-users'], [{ id: 'u-2' }]);
    vi.mocked(api.post).mockResolvedValueOnce({
      userId: 'u-1',
      username: 'admin',
      role: 'Viewer',
    });

    await useAuthStore.getState().refresh();

    expect(useAuthStore.getState().role).toBe('Viewer');
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_HISTORY_KEY)).toBeNull();
    expect(queryClient.getQueryData(['admin-users'])).toBeUndefined();
  });

  it('refresh_responseCannotRestoreThePreviousIdentityAfterABoundary', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-a',
      username: 'alice',
      role: 'Admin',
    });
    let resolveRefresh!: (identity: { userId: string; username: string; role: string }) => void;
    const staleRefresh = new Promise<{ userId: string; username: string; role: string }>((resolve) => {
      resolveRefresh = resolve;
    });
    vi.mocked(api.post).mockReturnValueOnce(staleRefresh);

    const refresh = useAuthStore.getState().refresh();
    clearLocalAuthBoundary();
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-b',
      username: 'bob',
      role: 'Operator',
    });
    resolveRefresh({ userId: 'u-a', username: 'alice', role: 'Admin' });
    await refresh;

    expect(useAuthStore.getState()).toMatchObject({
      userId: 'u-b',
      username: 'bob',
      role: 'Operator',
      isAuthenticated: true,
    });
  });

  it('maybeRefresh_isNoOp', async () => {
    // maybeRefresh is intentionally a no-op in the cookie-based flow — JS cannot
    // introspect the JWT exp claim from an httpOnly cookie, so proactive refresh
    // would require a separate server-set expiry cookie (deferred).
    await expect(useAuthStore.getState().maybeRefresh()).resolves.toBeUndefined();
    expect(api.post).not.toHaveBeenCalled();
  });
});

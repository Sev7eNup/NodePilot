import { describe, it, expect, beforeEach, vi } from 'vitest';
import { useAuthStore } from '../../stores/authStore';

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
  beforeEach(() => {
    vi.clearAllMocks();
    sessionStorage.clear();
    // Reset store state to a known "pre-init" shape (matches production bundle load).
    useAuthStore.setState({
      username: null,
      role: null,
      isAuthenticated: null,
    });
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

  it('logout_removesLegacyWorkflowClipboardFromSessionStorage', async () => {
    sessionStorage.setItem('np_clipboard', JSON.stringify({
      nodes: [{ data: { config: { apiKey: 'legacy-inline-secret' } } }],
      edges: [],
    }));
    vi.mocked(api.post).mockResolvedValueOnce(undefined);

    await useAuthStore.getState().logout();

    expect(sessionStorage.getItem('np_clipboard')).toBeNull();
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
    vi.mocked(api.post).mockRejectedValueOnce(new Error('network down'));
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});

    await useAuthStore.getState().logout();

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(false);
    expect(warnSpy).toHaveBeenCalled();
    warnSpy.mockRestore();
  });

  it('initialize_withValidCookie_restoresState', async () => {
    // Backend /auth/me returns the current user when the np_auth cookie validates.
    vi.mocked(api.get).mockResolvedValueOnce({ username: 'testuser', role: 'Operator' });

    await useAuthStore.getState().initialize();

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(true);
    expect(state.username).toBe('testuser');
    expect(state.role).toBe('Operator');
    expect(api.get).toHaveBeenCalledWith('/auth/me');
  });

  it('initialize_noCookie_setsAnonymous', async () => {
    vi.mocked(api.get).mockRejectedValueOnce(new Error('Unauthorized'));

    await useAuthStore.getState().initialize();

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(false);
    expect(state.username).toBeNull();
    expect(state.role).toBeNull();
  });

  it('login_failure_throwsError', async () => {
    vi.mocked(api.post).mockRejectedValueOnce(new Error('Invalid credentials'));

    await expect(useAuthStore.getState().login('admin', 'wrong')).rejects.toThrow('Invalid credentials');

    const state = useAuthStore.getState();
    // isAuthenticated stays at its pre-login value (null or false); critically, no auth granted.
    expect(state.isAuthenticated).not.toBe(true);
  });

  it('refresh_success_updatesState', async () => {
    const mockResponse = { token: 'new-jwt', username: 'admin', role: 'Admin' };
    vi.mocked(api.post).mockResolvedValueOnce(mockResponse);

    await useAuthStore.getState().refresh();

    const state = useAuthStore.getState();
    expect(state.isAuthenticated).toBe(true);
    expect(state.username).toBe('admin');
    expect(api.post).toHaveBeenCalledWith('/auth/refresh');
  });

  it('maybeRefresh_isNoOp', async () => {
    // maybeRefresh is intentionally a no-op in the cookie-based flow — JS cannot
    // introspect the JWT exp claim from an httpOnly cookie, so proactive refresh
    // would require a separate server-set expiry cookie (deferred).
    await expect(useAuthStore.getState().maybeRefresh()).resolves.toBeUndefined();
    expect(api.post).not.toHaveBeenCalled();
  });
});

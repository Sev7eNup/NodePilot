import { describe, it, expect, beforeEach, vi } from 'vitest';
import { useAuthStore } from '../../stores/authStore';

// Mock the api module — both `post` and `get` are used by the auth flow now.
vi.mock('../../api/client', () => ({
  api: {
    post: vi.fn(),
    get: vi.fn(),
  },
}));

import { api } from '../../api/client';

describe('authStore (cookie-based, audit H-5)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
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

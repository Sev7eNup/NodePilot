import { describe, it, expect, beforeAll, beforeEach, afterAll, afterEach, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';
import {
  AI_CHAT_STORAGE_KEY,
  DB_ADMIN_QUERY_DRAFT_KEY,
  DB_ADMIN_QUERY_HISTORY_KEY,
} from '../../security/sensitiveBrowserState';
import { queryClient } from '../../queryClient';
import { aiChatScopeKey, useAiChatStore } from '../../stores/aiChatStore';
import { startAuthBoundarySynchronization, useAuthStore } from '../../stores/authStore';
import { clearLocalAuthBoundary } from '../../security/authBoundary';

const BASE = 'http://localhost';
const server = setupServer();

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' });
});

beforeEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  queryClient.clear();
  useAiChatStore.setState({ messagesByThread: {}, threadsByScope: {}, activeThreadByScope: {} });
  useAuthStore.setState({ userId: null, username: null, role: null, isAuthenticated: null });
  // Clear cookies between tests. `document.cookie` accepts one entry at a time,
  // so walk the current string and expire each entry.
  if (typeof document !== 'undefined') {
    document.cookie.split(';').forEach((c) => {
      const name = c.split('=')[0].trim();
      if (name) document.cookie = `${name}=; Max-Age=0; path=/`;
    });
  }
  Object.defineProperty(window, 'location', {
    value: { href: '', pathname: '/' },
    writable: true,
  });
});

afterEach(() => {
  server.resetHandlers();
  vi.restoreAllMocks();
});

afterAll(() => {
  server.close();
});

function patchFetch() {
  const originalFetch = globalThis.fetch;
  vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
    if (typeof input === 'string' && input.startsWith('/')) {
      return originalFetch(`${BASE}${input}`, init);
    }
    return originalFetch(input, init);
  });
}

describe('API Client', () => {
  it('get_success_returnsData', async () => {
    server.use(
      http.get(`${BASE}/api/workflows`, () => {
        return HttpResponse.json([{ id: '1', name: 'Test' }]);
      })
    );
    patchFetch();

    const { api } = await import('../../api/client');
    const result = await api.get<Array<{ id: string; name: string }>>('/workflows');
    expect(result).toEqual([{ id: '1', name: 'Test' }]);
  });

  it('get_sendsCredentialsInclude', async () => {
    // The browser attaches the np_auth httpOnly cookie via `credentials: 'include'`
    // instead of an Authorization header.
    let capturedCredentials: RequestCredentials | undefined;
    const originalFetch = globalThis.fetch;
    vi.spyOn(globalThis, 'fetch').mockImplementation((input, init) => {
      capturedCredentials = init?.credentials;
      if (typeof input === 'string' && input.startsWith('/')) {
        return originalFetch(`${BASE}${input}`, init);
      }
      return originalFetch(input, init);
    });
    server.use(
      http.get(`${BASE}/api/test`, () => HttpResponse.json({ ok: true }))
    );

    const { api } = await import('../../api/client');
    await api.get('/test');

    expect(capturedCredentials).toBe('include');
  });

  it('post_sendsCsrfHeaderFromCookie', async () => {
    // Double-submit pattern: a mutating request must echo the np_csrf cookie value
    // back in the X-CSRF-Token header, so set the cookie before sending.
    document.cookie = 'np_csrf=test-csrf-value; path=/';

    let capturedCsrf: string | null = null;
    server.use(
      http.post(`${BASE}/api/test`, ({ request }) => {
        capturedCsrf = request.headers.get('X-CSRF-Token');
        return HttpResponse.json({ ok: true });
      })
    );
    patchFetch();

    const { api } = await import('../../api/client');
    await api.post('/test', { a: 1 });

    expect(capturedCsrf).toBe('test-csrf-value');
  });

  it('get_doesNotSendCsrfHeader', async () => {
    // Safe methods skip the CSRF header. The server ignores it, and leaving it off keeps
    // cached GETs free of a user-specific value.
    document.cookie = 'np_csrf=some-value; path=/';

    let capturedCsrf: string | null = null;
    server.use(
      http.get(`${BASE}/api/test`, ({ request }) => {
        capturedCsrf = request.headers.get('X-CSRF-Token');
        return HttpResponse.json({ ok: true });
      })
    );
    patchFetch();

    const { api } = await import('../../api/client');
    await api.get('/test');

    expect(capturedCsrf).toBeNull();
  });

  it('post_sendsJsonBody', async () => {
    let capturedBody: unknown = null;
    server.use(
      http.post(`${BASE}/api/auth/login`, async ({ request }) => {
        capturedBody = await request.json();
        return HttpResponse.json({ token: 'new-token', username: 'admin', role: 'Admin' });
      })
    );
    patchFetch();

    const { api } = await import('../../api/client');
    const result = await api.post('/auth/login', { username: 'admin', password: 'secret' });

    expect(capturedBody).toEqual({ username: 'admin', password: 'secret' });
    expect(result).toHaveProperty('username', 'admin');
  });

  it('get_401_redirectsToLogin', async () => {
    // No token lives in localStorage, so there is nothing to remove. The redirect alone
    // signals an expired or revoked cookie, and the backend scrubs the np_auth cookie
    // from subsequent responses.
    server.use(
      http.get(`${BASE}/api/protected`, () => {
        return new HttpResponse(null, { status: 401 });
      })
    );
    patchFetch();

    sessionStorage.setItem(AI_CHAT_STORAGE_KEY, 'sensitive chat');
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT secret');
    localStorage.setItem(DB_ADMIN_QUERY_HISTORY_KEY, '["legacy query"]');
    const scope = aiChatScopeKey('u-1', 'wf-1');
    const threadId = useAiChatStore.getState().newThread(scope, 'Sensitive');
    useAiChatStore.getState().updateMessages(scope, threadId, () => [
      { role: 'user', content: 'live secret' },
    ]);
    useAuthStore.setState({ userId: 'u-1', username: 'alice', role: 'Admin', isAuthenticated: true });
    queryClient.setQueryData(['user-private'], { secret: true });

    const { api } = await import('../../api/client');

    await expect(api.get('/protected')).rejects.toThrow('Unauthorized');
    expect(window.location.href).toBe('/login');
    expect(sessionStorage.getItem(AI_CHAT_STORAGE_KEY)).toBeNull();
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
    expect(localStorage.getItem(DB_ADMIN_QUERY_HISTORY_KEY)).toBeNull();
    expect(useAiChatStore.getState().messagesByThread).toEqual({});
    expect(useAuthStore.getState().isAuthenticated).toBe(false);
    expect(queryClient.getQueryData(['user-private'])).toBeUndefined();
  });

  it('get_stale401FromPreviousIdentity_doesNotClearOrRedirectCurrentIdentity', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-a',
      username: 'alice',
      role: 'Admin',
    });
    let resolveResponse!: (response: Response) => void;
    const staleResponse = new Promise<Response>((resolve) => {
      resolveResponse = resolve;
    });
    vi.spyOn(globalThis, 'fetch').mockReturnValueOnce(staleResponse);

    const { api } = await import('../../api/client');
    const previousUserRequest = api.get('/protected');
    clearLocalAuthBoundary();
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-b',
      username: 'bob',
      role: 'Operator',
    });
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT user_b_data');
    queryClient.setQueryData(['user-b-private'], { secret: 'belongs-to-b' });

    resolveResponse(new Response(null, { status: 401, statusText: 'Unauthorized' }));
    await expect(previousUserRequest).rejects.toMatchObject({ name: 'AbortError' });

    expect(useAuthStore.getState()).toMatchObject({
      userId: 'u-b',
      username: 'bob',
      isAuthenticated: true,
    });
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBe('SELECT user_b_data');
    expect(queryClient.getQueryData(['user-b-private'])).toEqual({ secret: 'belongs-to-b' });
    expect(window.location.href).toBe('');
  });

  it('stale login response re-probes after its Set-Cookie may have replaced the newer identity', async () => {
    let resolveStaleLogin!: (response: Response) => void;
    const staleLoginResponse = new Promise<Response>((resolve) => {
      resolveStaleLogin = resolve;
    });
    const fetchMock = vi.spyOn(globalThis, 'fetch')
      .mockReturnValueOnce(staleLoginResponse)
      .mockResolvedValueOnce(Response.json({
        id: 'u-cookie-owner',
        username: 'cookie-owner',
        role: 'Viewer',
      }));
    const stopSynchronization = startAuthBoundarySynchronization();

    try {
      const { api } = await import('../../api/client');
      const pendingUserALogin = api.post('/auth/login', {
        username: 'alice',
        password: 'secret',
      });

      clearLocalAuthBoundary();
      useAuthStore.getState().acceptAuthenticatedIdentity({
        userId: 'u-b',
        username: 'bob',
        role: 'Admin',
      });
      sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT user_b_data');
      queryClient.setQueryData(['user-b-private'], { secret: 'belongs-to-b' });

      resolveStaleLogin(Response.json({
        userId: 'u-a',
        username: 'alice',
        role: 'Admin',
      }));
      await expect(pendingUserALogin).rejects.toMatchObject({ name: 'AbortError' });

      await vi.waitFor(() => expect(useAuthStore.getState()).toMatchObject({
        userId: 'u-cookie-owner',
        username: 'cookie-owner',
        role: 'Viewer',
        isAuthenticated: true,
      }));
      expect(fetchMock).toHaveBeenCalledWith('/api/auth/me', expect.objectContaining({
        credentials: 'include',
      }));
      expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
      expect(queryClient.getQueryData(['user-b-private'])).toBeUndefined();
    } finally {
      stopSynchronization();
    }
  });

  it('get_staleSuccessCannotPopulateCurrentUsersQueryCache', async () => {
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-a',
      username: 'alice',
      role: 'Admin',
    });
    let resolveResponse!: (response: Response) => void;
    const staleResponse = new Promise<Response>((resolve) => {
      resolveResponse = resolve;
    });
    vi.spyOn(globalThis, 'fetch').mockReturnValueOnce(staleResponse);

    const { api } = await import('../../api/client');
    const staleCacheWrite = api.get<{ secret: string }>('/slow-user-a-data').then((data) => {
      queryClient.setQueryData(['late-user-a-result'], data);
    });
    clearLocalAuthBoundary();
    useAuthStore.getState().acceptAuthenticatedIdentity({
      userId: 'u-b',
      username: 'bob',
      role: 'Operator',
    });
    queryClient.setQueryData(['user-b-private'], { secret: 'belongs-to-b' });

    resolveResponse(Response.json({ secret: 'belongs-to-a' }));
    await expect(staleCacheWrite).rejects.toMatchObject({ name: 'AbortError' });

    expect(queryClient.getQueryData(['late-user-a-result'])).toBeUndefined();
    expect(queryClient.getQueryData(['user-b-private'])).toEqual({ secret: 'belongs-to-b' });
  });

  it('download_staleSuccessDoesNotCreateOrClickAnAnchor', async () => {
    let resolveResponse!: (response: Response) => void;
    const staleResponse = new Promise<Response>((resolve) => {
      resolveResponse = resolve;
    });
    vi.spyOn(globalThis, 'fetch').mockReturnValueOnce(staleResponse);
    const originalCreateObjectUrl = URL.createObjectURL;
    const originalRevokeObjectUrl = URL.revokeObjectURL;
    const createObjectUrl = vi.fn(() => 'blob:stale');
    URL.createObjectURL = createObjectUrl;
    URL.revokeObjectURL = vi.fn();
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});

    try {
      const { downloadFromApi } = await import('../../api/client');
      const staleDownload = downloadFromApi('/slow-export', 'export.zip');
      clearLocalAuthBoundary();
      resolveResponse(new Response('User A export', {
        status: 200,
        headers: { 'Content-Disposition': 'attachment; filename="user-a.zip"' },
      }));

      await expect(staleDownload).rejects.toMatchObject({ name: 'AbortError' });
      expect(createObjectUrl).not.toHaveBeenCalled();
      expect(click).not.toHaveBeenCalled();
    } finally {
      URL.createObjectURL = originalCreateObjectUrl;
      URL.revokeObjectURL = originalRevokeObjectUrl;
    }
  });

  it('post_401_onLoginPage_surfacesServerPayloadWithoutRedirect', async () => {
    // LoginPage needs the server's 401 payload to bubble up so it can tell a wrong
    // password from the SETUP_TOKEN_REQUIRED bootstrap gate. Redirecting from /login
    // to /login would swallow the error and can loop.
    Object.defineProperty(window, 'location', {
      value: { href: '/login', pathname: '/login' },
      writable: true,
    });
    server.use(
      http.post(`${BASE}/api/auth/login`, () =>
        HttpResponse.json(
          { code: 'SETUP_TOKEN_REQUIRED', message: 'Admin bootstrap required.' },
          { status: 401 },
        ))
    );
    patchFetch();
    sessionStorage.setItem(DB_ADMIN_QUERY_DRAFT_KEY, 'SELECT previous_user_secret');

    const { api } = await import('../../api/client');
    await expect(api.post('/auth/login', {})).rejects.toThrow(/SETUP_TOKEN_REQUIRED/);
    expect(window.location.href).toBe('/login'); // unchanged
    expect(sessionStorage.getItem(DB_ADMIN_QUERY_DRAFT_KEY)).toBeNull();
  });

  it('delete_204_returnsUndefined', async () => {
    server.use(
      http.delete(`${BASE}/api/workflows/123`, () => {
        return new HttpResponse(null, { status: 204 });
      })
    );
    patchFetch();

    const { api } = await import('../../api/client');
    const result = await api.delete('/workflows/123');
    expect(result).toBeUndefined();
  });

  it('get_serverError_throwsWithMessage', async () => {
    server.use(
      http.get(`${BASE}/api/broken`, () => {
        return new HttpResponse('Internal Server Error', { status: 500 });
      })
    );
    patchFetch();

    const { api } = await import('../../api/client');
    await expect(api.get('/broken')).rejects.toThrow('Internal Server Error');
  });

  it('get_503WithCode_throwsApiErrorCarryingStatusCodeAndRetryAfter', async () => {
    // The outage UX branches on these fields instead of matching message text, so the banner,
    // the login page and the auth re-probe all depend on them being carried.
    server.use(
      http.get(`${BASE}/api/workflows`, () => {
        return HttpResponse.json(
          { code: 'DATABASE_UNAVAILABLE', message: 'The database is not reachable right now.' },
          { status: 503, headers: { 'Retry-After': '15' } },
        );
      })
    );
    patchFetch();

    const { api, ApiError, isDatabaseOutageError } = await import('../../api/client');
    const err = await api.get('/workflows').catch((e: unknown) => e);

    expect(err).toBeInstanceOf(ApiError);
    const apiErr = err as InstanceType<typeof ApiError>;
    expect(apiErr.status).toBe(503);
    expect(apiErr.code).toBe('DATABASE_UNAVAILABLE');
    expect(apiErr.retryAfterSeconds).toBe(15);
    // The display string keeps the "message (CODE)" shape for callers that render err.message.
    expect(apiErr.message).toContain('DATABASE_UNAVAILABLE');
    expect(isDatabaseOutageError(apiErr)).toBe(true);
  });

  it('get_404_throwsApiErrorWithStatus', async () => {
    server.use(
      http.get(`${BASE}/api/workflows/nope/contract`, () => {
        return new HttpResponse('Not Found', { status: 404 });
      })
    );
    patchFetch();

    const { api, ApiError } = await import('../../api/client');
    const err = await api.get('/workflows/nope/contract').catch((e: unknown) => e);

    expect(err).toBeInstanceOf(ApiError);
    expect((err as InstanceType<typeof ApiError>).status).toBe(404);
  });
});

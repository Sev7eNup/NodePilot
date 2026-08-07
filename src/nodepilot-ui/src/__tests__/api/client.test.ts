import { describe, it, expect, beforeAll, beforeEach, afterAll, afterEach, vi } from 'vitest';
import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';

const BASE = 'http://localhost';
const server = setupServer();

beforeAll(() => {
  server.listen({ onUnhandledRequest: 'error' });
});

beforeEach(() => {
  // Clear cookies between tests. `document.cookie` accepts one entry at a time;
  // walking the current string and expiring each entry is the jsdom idiom.
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
    // Audit H-5: the browser now attaches the np_auth httpOnly cookie via
    // `credentials: 'include'` instead of an Authorization header.
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
    // Double-submit pattern: the mutating request must echo the np_csrf cookie
    // value back in the X-CSRF-Token header. Plant the cookie before firing.
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
    // Safe methods skip the CSRF header — the server would ignore it anyway but
    // keeping reads clean avoids stamping every cached GET with a user-specific value.
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
    // H-5 migration: no token lives in localStorage, so there's nothing to remove —
    // the redirect alone signals an expired/revoked cookie, and the backend already
    // scrubs the np_auth cookie from subsequent responses.
    server.use(
      http.get(`${BASE}/api/protected`, () => {
        return new HttpResponse(null, { status: 401 });
      })
    );
    patchFetch();

    const { api } = await import('../../api/client');

    await expect(api.get('/protected')).rejects.toThrow('Unauthorized');
    expect(window.location.href).toBe('/login');
  });

  it('post_401_onLoginPage_surfacesServerPayloadWithoutRedirect', async () => {
    // LoginPage needs the server's 401 payload to bubble up so it can tell a wrong
    // password from the SETUP_TOKEN_REQUIRED bootstrap gate. Redirecting from /login
    // to /login would swallow the error and potentially infinite-loop.
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

    const { api } = await import('../../api/client');
    await expect(api.post('/auth/login', {})).rejects.toThrow(/SETUP_TOKEN_REQUIRED/);
    expect(window.location.href).toBe('/login'); // unchanged
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
    // The whole outage UX branches on these fields instead of substring-matching prose - if they
    // stop being carried, the banner, the login third-branch and the auth re-probe all regress
    // to guessing from display strings.
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
    // The display string keeps its established "message (CODE)" shape for everything that still
    // renders err.message.
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

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

  it('get_401_onLoginPage_doesNotRedirect', async () => {
    // LoginPage needs the 401 error bubble up (wrong password). Redirecting from /login
    // to /login would swallow the error and potentially infinite-loop.
    Object.defineProperty(window, 'location', {
      value: { href: '/login', pathname: '/login' },
      writable: true,
    });
    server.use(
      http.post(`${BASE}/api/auth/login`, () => new HttpResponse(null, { status: 401 }))
    );
    patchFetch();

    const { api } = await import('../../api/client');
    await expect(api.post('/auth/login', {})).rejects.toThrow('Unauthorized');
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
});

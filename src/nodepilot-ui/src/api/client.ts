const BASE_URL = '/api';

const MUTATING_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

/**
 * Reads the CSRF token from the JS-readable `np_csrf` cookie. The backend sets this
 * cookie on every login/refresh; the server-side CsrfMiddleware requires the header
 * value to match the cookie on any mutating request, defeating cross-origin form
 * submission attacks against the httpOnly auth cookie.
 */
function readCsrfToken(): string {
  if (typeof document === 'undefined') return '';
  const match = /(?:^|;\s*)np_csrf=([^;]+)/.exec(document.cookie);
  return match ? decodeURIComponent(match[1]) : '';
}

/**
 * Shared auth + error-handling shell for every API call (introduced by a security-audit
 * fix that moved the JWT out of client-readable storage and into an httpOnly cookie).
 *
 * Auth model: the JWT lives in an httpOnly cookie (`np_auth`) that the browser attaches
 * automatically when we pass `credentials: 'include'`. Mutating requests additionally
 * echo the CSRF cookie back in the `X-CSRF-Token` header; the server rejects mismatches.
 * No token is ever stored in localStorage, so a future XSS cannot exfiltrate it.
 */
async function authedFetch(path: string, options?: RequestInit): Promise<Response> {
  const method = (options?.method ?? 'GET').toUpperCase();
  const headers: Record<string, string> = {
    // FormData must NOT carry an explicit Content-Type — the browser sets the multipart
    // boundary itself. Only JSON string bodies get the application/json header.
    ...(options?.body !== undefined && !(options.body instanceof FormData)
      ? { 'Content-Type': 'application/json' }
      : {}),
  };
  if (MUTATING_METHODS.has(method)) {
    const csrf = readCsrfToken();
    if (csrf) headers['X-CSRF-Token'] = csrf;
  }

  const response = await fetch(`${BASE_URL}${path}`, {
    ...options,
    credentials: 'include', // attach np_auth + np_csrf cookies on every request
    headers: { ...headers, ...options?.headers },
  });

  if (response.status === 401) {
    // Cookie expired / revoked / missing. Redirect to login, but stay put if we're
    // already on the login page so the form can surface its own 401 error normally
    // (e.g. wrong password) instead of looping.
    if (typeof window !== 'undefined' && !globalThis.location.pathname.startsWith('/login')) {
      globalThis.location.href = '/login';
    }
    throw new Error('Unauthorized');
  }

  if (!response.ok) {
    // Cap body + strip probable stack-frame artifacts so that leaked server exceptions
    // don't blow up toast UIs or expose internal paths to end users.
    let error = await response.text();

    // Structured server errors have the shape `{code, message, bodyExcerpt?}` (see
    // AiController, MapLlmException, etc.). If the body parses as JSON and matches that
    // shape, format only the fields relevant to the user — otherwise they'd see the raw
    // JSON string, brackets/quotes and all, as the error message.
    if (error.startsWith('{')) {
      try {
        const parsed = JSON.parse(error) as {
          code?: string;
          message?: string;
          bodyExcerpt?: string;
          error?: string;
          detail?: string;
          title?: string;
        };
        const msg = parsed.detail ?? parsed.message ?? parsed.error ?? parsed.title;
        if (msg) {
          const display = parsed.code && parsed.code !== msg ? `${msg} (${parsed.code})` : msg;
          error = parsed.bodyExcerpt
            ? `${display}\n\nUpstream: ${parsed.bodyExcerpt}`
            : display;
        }
      } catch {
        // Body looked like JSON but wasn't — pass the raw text through as-is.
      }
    }

    if (error && error.length > 500) error = error.slice(0, 500) + '... [truncated]';
    error = error.replaceAll(/(?:\s+at\s+[^\n]+\n?)+/g, ' [stack hidden] ');
    error = error.replaceAll(/System\.\w+Exception:[^\n]+/g, '[exception hidden]');
    throw new Error(error || response.statusText);
  }

  return response;
}

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const response = await authedFetch(path, options);
  if (response.status === 204) return undefined as T;
  return response.json();
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body ? JSON.stringify(body) : undefined }),
  put: <T>(path: string, body: unknown) =>
    request<T>(path, { method: 'PUT', body: JSON.stringify(body) }),
  patch: <T>(path: string, body: unknown) =>
    request<T>(path, { method: 'PATCH', body: JSON.stringify(body) }),
  delete: <T>(path: string) => request<T>(path, { method: 'DELETE' }),
  // Raw-body POST with an explicit Content-Type. The main entrypoint is the SCOrch
  // import which ships the .ois_export XML payload verbatim.
  postRaw: <T>(path: string, body: string, contentType: string) =>
    request<T>(path, { method: 'POST', body, headers: { 'Content-Type': contentType } }),
  // POST with extra per-call headers — used by the admin SQL console to send
  // the X-Confirm-Write confirmation gesture alongside a write-mode statement.
  postWithHeaders: <T>(path: string, body: unknown, headers: Record<string, string>) =>
    request<T>(path, { method: 'POST', body: JSON.stringify(body), headers }),
  // Multipart POST — used by the backup restore/preview which upload a .npbackup file
  // alongside form fields. No Content-Type header: the browser sets the boundary itself.
  postForm: <T>(path: string, form: FormData) =>
    request<T>(path, { method: 'POST', body: form }),
};

/**
 * POSTs a JSON body and returns the raw {@link Response} for Server-Sent-Events streaming
 * (the AI chat + script-generation endpoints). Reuses {@link authedFetch} so cookie auth,
 * CSRF and pre-stream error handling (503/400 throw before any byte streams) are identical to
 * every other call. The caller reads `response.body` as an event stream. Pass an
 * `AbortSignal` to cancel (Stop button / dialog close) — the reader then throws `AbortError`.
 */
export async function postEventStream(path: string, body: unknown, signal?: AbortSignal): Promise<Response> {
  return authedFetch(path, {
    method: 'POST',
    body: JSON.stringify(body),
    headers: { Accept: 'text/event-stream' },
    signal,
  });
}

/**
 * POSTs a JSON body and triggers a browser download of the (binary) response — the
 * counterpart to {@link downloadFromApi} for endpoints that take a request body, like the
 * backup export. Honors the server-supplied Content-Disposition filename.
 */
export async function downloadFromApiPost(path: string, body: unknown, fallbackName: string): Promise<void> {
  const response = await authedFetch(path, { method: 'POST', body: JSON.stringify(body) });

  let filename = fallbackName;
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  if (match) filename = decodeURIComponent(match[1]);

  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

// Triggers a browser download of the response body. Honors the server-supplied
// Content-Disposition filename; falls back to `fallbackName` if missing.
export async function downloadFromApi(path: string, fallbackName: string): Promise<void> {
  const response = await authedFetch(path);

  let filename = fallbackName;
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  if (match) filename = decodeURIComponent(match[1]);

  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

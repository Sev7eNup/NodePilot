import { csrfHeaders } from './csrf';
import { reportDatabaseOutageSuspected } from '../stores/dbHealthStore';

const BASE_URL = '/api';

/**
 * Error thrown for every non-OK API response, carrying the machine-readable parts the display
 * string used to swallow: HTTP status, the server's stable `code` (ADR 0007) and the Retry-After
 * hint. `super(message)` receives the exact string `new Error(...)` used to carry, so every
 * existing `instanceof Error` guard and `.message` read keeps working unchanged — but callers can
 * now branch on `status`/`code` instead of substring-matching prose.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly retryAfterSeconds?: number;

  constructor(message: string, status: number, code?: string, retryAfterSeconds?: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

/**
 * The breaker is open: the database is gone and the outage banner is on screen owning that message.
 * Deliberately NOT including DATABASE_TIMEOUT — see isDatabaseSlowError.
 */
export function isDatabaseOutageError(err: unknown): boolean {
  return err instanceof ApiError && err.code === 'DATABASE_UNAVAILABLE';
}

/**
 * The database answered too slowly while the breaker stayed CLOSED (one slow query, not an outage).
 * No banner is shown for this state by design, so it must stay visible as an ordinary error: the
 * bug this whole area exists to prevent is a busy database rendering as an empty installation.
 */
export function isDatabaseSlowError(err: unknown): boolean {
  return err instanceof ApiError && err.code === 'DATABASE_TIMEOUT';
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
    ...csrfHeaders(method),
  };

  const response = await fetch(`${BASE_URL}${path}`, {
    ...options,
    credentials: 'include', // attach np_auth + np_csrf cookies on every request
    headers: { ...headers, ...options?.headers },
  });

  if (response.status === 401 && typeof window !== 'undefined' && !globalThis.location.pathname.startsWith('/login')) {
    // Cookie expired / revoked / missing. Redirect to login. On the login page itself we
    // instead fall through to the generic error parser below so the form can distinguish
    // the server's 401 payloads (wrong password vs. the SETUP_TOKEN_REQUIRED bootstrap
    // gate) instead of seeing an opaque "Unauthorized".
    globalThis.location.href = '/login';
    throw new ApiError('Unauthorized', 401);
  }

  if (!response.ok) {
    // Cap body + strip probable stack-frame artifacts so that leaked server exceptions
    // don't blow up toast UIs or expose internal paths to end users.
    let error = await response.text();
    let code: string | undefined;

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
        code = parsed.code;
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

    const retryAfterRaw = Number(response.headers.get('Retry-After'));
    const apiError = new ApiError(
      error || response.statusText || `HTTP ${response.status}`,
      response.status,
      code,
      Number.isFinite(retryAfterRaw) && retryAfterRaw > 0 ? retryAfterRaw : undefined);

    // Central observation point: every DATABASE_* 503 flows through here, no matter which page or
    // mutation triggered it. BOTH codes raise suspicion — a timeout is the earliest hint that
    // something is wrong, often before the breaker has decided anything, which is exactly when a
    // faster poll pays off. Suspicion only accelerates the health poll; the poll's own probe stays
    // the single authority on whether the banner goes up (one slow query must not raise it).
    if (isDatabaseOutageError(apiError) || isDatabaseSlowError(apiError)) reportDatabaseOutageSuspected();

    throw apiError;
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

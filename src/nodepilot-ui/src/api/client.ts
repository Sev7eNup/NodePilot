import { csrfHeaders } from './csrf';
import { reportDatabaseOutageSuspected } from '../stores/dbHealthStore';
import {
  AuthBoundaryChangedError,
  assertAuthBoundaryGenerationCurrent,
  captureAuthBoundaryGeneration,
  handleStaleAuthCookieResponseBoundary,
  handleUnauthorizedAuthBoundary,
  isAuthBoundaryGenerationCurrent,
} from '../security/authBoundary';

const BASE_URL = '/api';
const COOKIE_MUTATING_AUTH_PATHS = new Set([
  '/auth/login',
  '/auth/windows',
  '/auth/refresh',
  '/auth/logout',
]);

/**
 * Error thrown for every non-OK API response. Carries the HTTP status, the server's stable
 * `code` (ADR 0007) and the Retry-After hint, not just a display string, so callers can branch
 * on `status`/`code` instead of matching on message text. `super(message)` keeps the same
 * message string, so existing `instanceof Error` guards and `.message` reads still work.
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
 * True when the breaker is open: the database is unreachable and the outage banner owns the
 * message on screen. Deliberately excludes DATABASE_TIMEOUT — see isDatabaseSlowError.
 */
export function isDatabaseOutageError(err: unknown): boolean {
  return err instanceof ApiError && err.code === 'DATABASE_UNAVAILABLE';
}

/**
 * True when the database answered too slowly but the breaker stayed closed (a single slow
 * query, not an outage). No banner is shown for this case, so it must surface as an ordinary
 * error — otherwise a busy database would look like an empty installation.
 */
export function isDatabaseSlowError(err: unknown): boolean {
  return err instanceof ApiError && err.code === 'DATABASE_TIMEOUT';
}

/**
 * Shared auth and error-handling wrapper for every API call.
 *
 * The JWT lives in an httpOnly cookie (`np_auth`) that the browser attaches automatically
 * when `credentials: 'include'` is set. Mutating requests also echo the CSRF cookie back
 * in the `X-CSRF-Token` header; the server rejects mismatches. The token is never stored
 * in localStorage, so XSS cannot read it.
 */
interface AuthBoundaryRequestPolicy {
  /** Remote cross-tab identity probes handle their own result and must not echo a 401. */
  broadcastUnauthorized?: boolean;
}

async function authedFetch(
  path: string,
  options?: RequestInit,
  authBoundaryPolicy?: AuthBoundaryRequestPolicy,
  requestBoundaryGeneration = captureAuthBoundaryGeneration(),
): Promise<Response> {
  // Bind every request, not just auth endpoints, to the identity it started under. A delayed
  // 401 for user A must not clear or redirect a newer session for user B.
  const method = (options?.method ?? 'GET').toUpperCase();
  const headers: Record<string, string> = {
    // FormData must not carry an explicit Content-Type — the browser sets the multipart
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

  // Discard stale successes and failures before a caller can cache, display, download or persist
  // them. This is the global defense; feature-level generation checks are additional defense.
  if (!isAuthBoundaryGenerationCurrent(requestBoundaryGeneration)) {
    // Set-Cookie already happened as a browser side effect before fetch resolved. A stale
    // response on the four SPA auth endpoints can still replace or clear a newer tab's cookie
    // even though its JSON body is rejected, so force every tab back through /auth/me.
    if (COOKIE_MUTATING_AUTH_PATHS.has(path)) handleStaleAuthCookieResponseBoundary();
    throw new AuthBoundaryChangedError();
  }
  let responseBoundaryGeneration = requestBoundaryGeneration;
  if (response.status === 401) {
    // A 401 is a full local boundary before redirect/error handling: in-memory AI/auth state,
    // SQL/AI persistence and React Query data are all discarded here. Credential rejection on
    // the login endpoints stays local to that attempt; every other 401 is broadcast for re-probe.
    const isRejectedLoginAttempt = path === '/auth/login' || path === '/auth/windows';
    handleUnauthorizedAuthBoundary(
      authBoundaryPolicy?.broadcastUnauthorized ?? !isRejectedLoginAttempt,
    );
    // The 401 above starts a new anonymous boundary. /login still needs to parse its structured
    // error payload, so bind that parsing to the newly created generation.
    responseBoundaryGeneration = captureAuthBoundaryGeneration();
  }

  if (response.status === 401
    && typeof window !== 'undefined'
    && !globalThis.location.pathname.startsWith('/login')) {
    // Cookie expired, revoked, or missing: redirect to login. On the login page itself, fall
    // through to the generic error parser below instead, so the form can tell a wrong password
    // apart from the SETUP_TOKEN_REQUIRED bootstrap gate rather than showing "Unauthorized".
    globalThis.location.href = '/login';
    throw new ApiError('Unauthorized', 401);
  }

  if (!response.ok) {
    // Cap body + strip probable stack-frame artifacts so that leaked server exceptions
    // don't blow up toast UIs or expose internal paths to end users.
    let error = await response.text();
    assertAuthBoundaryGenerationCurrent(responseBoundaryGeneration);
    let code: string | undefined;

    // Structured server errors have the shape `{code, message, bodyExcerpt?}` (see AiController,
    // MapLlmException, etc.). When the body parses as that shape, format only the fields the
    // user needs — otherwise they would see the raw JSON string as the error message.
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

    // Every DATABASE_* 503 flows through here, no matter which page or mutation triggered it.
    // Both codes raise suspicion: a timeout is often the earliest hint of trouble, before the
    // breaker has decided anything, so it speeds up the health poll. The poll's own probe stays
    // the only authority on whether the outage banner goes up; one slow query must not raise it.
    if (isDatabaseOutageError(apiError) || isDatabaseSlowError(apiError)) reportDatabaseOutageSuspected();

    throw apiError;
  }

  return response;
}

async function request<T>(
  path: string,
  options?: RequestInit,
  authBoundaryPolicy?: AuthBoundaryRequestPolicy,
): Promise<T> {
  const requestBoundaryGeneration = captureAuthBoundaryGeneration();
  const response = await authedFetch(
    path,
    options,
    authBoundaryPolicy,
    requestBoundaryGeneration,
  );
  if (response.status === 204) return undefined as T;
  const result = await response.json() as T;
  assertAuthBoundaryGenerationCurrent(requestBoundaryGeneration);
  return result;
}

export const api = {
  get: <T>(path: string, authBoundaryPolicy?: AuthBoundaryRequestPolicy) =>
    request<T>(path, undefined, authBoundaryPolicy),
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
  const requestBoundaryGeneration = captureAuthBoundaryGeneration();
  return authedFetch(path, {
    method: 'POST',
    body: JSON.stringify(body),
    headers: { Accept: 'text/event-stream' },
    signal,
  }, undefined, requestBoundaryGeneration);
}

/**
 * POSTs a JSON body and triggers a browser download of the (binary) response — the
 * counterpart to {@link downloadFromApi} for endpoints that take a request body, like the
 * backup export. Honors the server-supplied Content-Disposition filename.
 */
export async function downloadFromApiPost(path: string, body: unknown, fallbackName: string): Promise<void> {
  const requestBoundaryGeneration = captureAuthBoundaryGeneration();
  const response = await authedFetch(
    path,
    { method: 'POST', body: JSON.stringify(body) },
    undefined,
    requestBoundaryGeneration,
  );

  let filename = fallbackName;
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  if (match) filename = decodeURIComponent(match[1]);

  const blob = await response.blob();
  assertAuthBoundaryGenerationCurrent(requestBoundaryGeneration);
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
  const requestBoundaryGeneration = captureAuthBoundaryGeneration();
  const response = await authedFetch(path, undefined, undefined, requestBoundaryGeneration);

  let filename = fallbackName;
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  if (match) filename = decodeURIComponent(match[1]);

  const blob = await response.blob();
  assertAuthBoundaryGenerationCurrent(requestBoundaryGeneration);
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

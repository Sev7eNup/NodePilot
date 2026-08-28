/**
 * CSRF double-submit helpers, shared by every caller that talks to the API.
 *
 * The backend's CsrfMiddleware requires mutating cookie-authenticated requests to echo the
 * JS-readable `np_csrf` cookie back in the `X-CSRF-Token` header; a mismatch is rejected. This
 * defeats cross-origin form submission against the httpOnly `np_auth` cookie. The cookie name
 * and header name are a contract with the server, so they live in exactly one place.
 */

/** Cookie the backend sets on every login/refresh. */
const CSRF_COOKIE = 'np_csrf';

/** Header the backend's CsrfMiddleware compares against that cookie. */
export const CSRF_HEADER = 'X-CSRF-Token';

/** Methods the middleware treats as state-changing, i.e. the ones that must carry the header. */
export const MUTATING_METHODS: ReadonlySet<string> = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

/** Reads the token from the cookie. Returns '' when absent or outside a browser (SSR/tests). */
export function readCsrfToken(): string {
  if (typeof document === 'undefined') return '';
  const match = new RegExp(String.raw`(?:^|;\s*)${CSRF_COOKIE}=([^;]+)`).exec(document.cookie);
  return match ? decodeURIComponent(match[1]) : '';
}

/**
 * Header bag for a request with the given method: the CSRF header for mutating methods,
 * nothing for safe ones.
 */
export function csrfHeaders(method: string): Record<string, string> {
  if (!MUTATING_METHODS.has(method.toUpperCase())) return {};
  const token = readCsrfToken();
  return token ? { [CSRF_HEADER]: token } : {};
}

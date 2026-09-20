/**
 * Route table for the demo backend.
 *
 * Patterns use `:name` segments and a trailing `*` for prefix matches. Routes are matched in
 * registration order, so a specific path registered before a wildcard wins.
 */

export interface RequestContext {
  method: string;
  /** Path without the `/api` prefix, e.g. `/workflows/{id}`. */
  path: string;
  params: Record<string, string>;
  query: URLSearchParams;
  request: Request;
  /** Parsed JSON body, or `undefined` when the request carried none. */
  body: <T = unknown>() => Promise<T | undefined>;
}

export type Handler = (ctx: RequestContext) => Response | Promise<Response>;

export interface Route {
  method: string;
  pattern: string;
  handler: Handler;
}

export function route(method: string, pattern: string, handler: Handler): Route {
  return { method: method.toUpperCase(), pattern, handler };
}

interface MatchResult {
  route: Route;
  params: Record<string, string>;
}

function matchPattern(pattern: string, path: string): Record<string, string> | null {
  const patternParts = pattern.split('/').filter(Boolean);
  const pathParts = path.split('/').filter(Boolean);
  const params: Record<string, string> = {};

  for (let i = 0; i < patternParts.length; i++) {
    const expected = patternParts[i];
    if (expected === '*') return params; // prefix match: everything below is accepted
    const actual = pathParts[i];
    if (actual === undefined) return null;
    if (expected.startsWith(':')) {
      params[expected.slice(1)] = decodeURIComponent(actual);
      continue;
    }
    if (expected.toLowerCase() !== actual.toLowerCase()) return null;
  }
  return patternParts.length === pathParts.length ? params : null;
}

export function matchRoute(routes: Route[], method: string, path: string): MatchResult | null {
  const wanted = method.toUpperCase();
  for (const candidate of routes) {
    if (candidate.method !== wanted && candidate.method !== 'ANY') continue;
    const params = matchPattern(candidate.pattern, path);
    if (params) return { route: candidate, params };
  }
  return null;
}

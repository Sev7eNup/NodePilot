/**
 * Installs the demo backend by patching `globalThis.fetch`.
 *
 * Chosen over a Service Worker for three reasons: no `mockServiceWorker.js` artifact to ship
 * and keep in step with the MSW version, no worker lifecycle to manage on a static site
 * (a registered worker outlives a redeploy and can serve stale mocks), and no registration
 * to order against `src/main.tsx`, which fires a request at module-evaluation time.
 *
 * Coverage is complete: the app has exactly five `fetch(` call sites in non-test source and
 * no other network API — no `EventSource`, no `WebSocket`, no `sendBeacon`.
 */
import { matchRoute, type RequestContext, type Route } from './router';
import { json, problem } from './respond';
import { delay } from './latency';

/** Prefixes the demo answers. Everything else goes to the real fetch (assets, fonts). */
const HANDLED_PREFIXES = ['/api/', '/healthz/', '/hubs/'];

let originalFetch: typeof globalThis.fetch | null = null;
let routes: Route[] = [];

/**
 * In-flight responses the demo is still producing, mirrored onto `window`.
 *
 * Playwright's `networkidle` is blind here: the patched fetch never reaches the network, so the
 * page reads as idle while React is still waiting on data — a crash that renders a moment later
 * goes unseen. This counter gives the smoke test a real settle signal instead of a sleep.
 */
let pending = 0;
let completed = 0;

function publishPending(): void {
  const target = globalThis as { __npDemoPending?: number; __npDemoCompleted?: number };
  target.__npDemoPending = pending;
  target.__npDemoCompleted = completed;
}

/** Paths the fallback has already reported, so one polling endpoint logs once. */
const reportedUnhandled = new Set<string>();

function baseHref(): string {
  return globalThis.location?.href ?? 'http://localhost';
}

function requestOf(input: RequestInfo | URL, init?: RequestInit): { url: string; method: string; request: Request } {
  if (input instanceof Request) {
    // A Request carries its own method and body; `init` still wins when both are given.
    return { url: input.url, method: (init?.method ?? input.method).toUpperCase(), request: input };
  }
  const raw = input instanceof URL ? input.toString() : String(input);
  // `Request` needs an absolute URL. A browser resolves a relative one against the document
  // on its own, but the constructor does not, and every call site here passes `/api/...`.
  const url = new URL(raw, baseHref()).toString();
  return { url, method: (init?.method ?? 'GET').toUpperCase(), request: new Request(url, init) };
}

function isHandled(pathname: string): boolean {
  return HANDLED_PREFIXES.some((prefix) => pathname.startsWith(prefix));
}

/**
 * Answer for a path no route matched. Returning a bare `[]` everywhere is what breaks demo
 * pages, because plenty of endpoints return an object and the UI reads into it. The shape is
 * guessed from the path, and every miss is logged so the smoke test can fail on it.
 */
function fallback(method: string, pathname: string): Response {
  const key = `${method} ${pathname}`;
  if (!reportedUnhandled.has(key)) {
    reportedUnhandled.add(key);
    console.warn(`[demo] unhandled ${key}`);
  }
  if (method !== 'GET') {
    // Never answer an unimplemented change with success. A 200 made deleting a user look like
    // it worked while all three users stayed exactly where they were — the worst kind of demo,
    // because the visitor believes something that is not true.
    return problem(
      501,
      'DEMO_NOT_AVAILABLE',
      'This change is not available in the demo, which runs entirely in your browser.',
    );
  }
  // Collection-shaped paths answer with a list, everything else with an object: an empty
  // array passed where an object is expected reads as truthy and then throws on first access.
  return json(/(s|list|names|options)$/i.test(pathname) ? [] : {});
}

async function handle(url: string, method: string, request: Request): Promise<Response> {
  const parsed = new URL(url, baseHref());
  const pathname = parsed.pathname;

  if (pathname.startsWith('/hubs/')) {
    // The fake hub is injected directly, so nothing should negotiate over HTTP. Answering
    // 404 keeps a stray call from hanging.
    return problem(404, 'NOT_FOUND', 'SignalR is simulated in the demo.');
  }

  // `/healthz/*` sits outside `/api`, and the app polls it every 15 s from boot.
  const path = pathname.startsWith('/api') ? pathname.slice('/api'.length) : pathname;

  const match = matchRoute(routes, method, path);
  if (!match) return fallback(method, pathname);

  const ctx: RequestContext = {
    method,
    path,
    params: match.params,
    query: parsed.searchParams,
    request,
    body: async <T,>() => {
      try {
        const raw = await request.clone().text();
        return raw ? (JSON.parse(raw) as T) : undefined;
      } catch {
        return undefined;
      }
    },
  };

  pending += 1;
  publishPending();
  try {
    await delay();
    return await match.route.handler(ctx);
  } catch (error) {
    console.error('[demo] handler failed', path, error);
    return problem(500, 'DEMO_HANDLER_FAILED', 'The demo backend failed to answer this request.');
  } finally {
    pending -= 1;
    completed += 1;
    publishPending();
  }
}

/** Patches `globalThis.fetch`. Safe to call twice; the second call replaces the route table. */
export function installDemoBackend(table: Route[]): void {
  routes = table;
  if (originalFetch) return;

  publishPending();
  originalFetch = globalThis.fetch.bind(globalThis);
  const passthrough = originalFetch;

  globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const { url, method, request } = requestOf(input, init);
    const pathname = new URL(url, baseHref()).pathname;
    if (!isHandled(pathname)) return passthrough(input, init);
    return handle(url, method, request);
  };
}

/** Restores the original `fetch`. Used by tests. */
export function uninstallDemoBackend(): void {
  if (!originalFetch) return;
  globalThis.fetch = originalFetch;
  originalFetch = null;
  routes = [];
  reportedUnhandled.clear();
}

/** Paths the fallback reported, for assertions in tests and the smoke spec. */
export function unhandledPaths(): string[] {
  return [...reportedUnhandled];
}

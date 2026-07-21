import type { Page, Route } from '@playwright/test';

/**
 * API mocks shared across E2E tests. Built on `page.route()` so every test gets
 * its own deterministic backend without spinning up the real ASP.NET Core host.
 *
 * Add new mocks by composing the helpers below — each one matches a specific
 * URL pattern and returns canned JSON. Tests that need to override a default
 * (e.g. simulate a 500) call `page.route()` AFTER `installDefaultMocks()` so
 * Playwright's last-installed-wins routing replaces only that endpoint.
 */

export const MOCK_USER = {
  // `id` is what authStore.initialize() reads (`me.id`) to populate userId — the field
  // the edit-lock UI compares against Workflow.checkedOutByUserId.
  id: '00000000-0000-0000-0000-000000000099',
  username: 'e2e-admin',
  role: 'Admin',
};

// Host identity surfaced by the TopBar chip (GET /api/system/host-info). Exported so specs
// can assert against the same values they're mocked with.
export const MOCK_HOST = {
  machineName: 'NPSRV01',
  fqdn: 'npsrv01.corp.example.local',
  domain: 'corp.example.local',
};

export async function installDefaultMocks(page: Page) {
  // Pin the designer to the CLASSIC look for the whole hermetic suite. The Atelier design
  // (designStore.designerTheme, default 'atelier') re-tokenises colors/geometry the visual
  // assertions in these specs were written against; the classic look must stay byte-identical,
  // so the entire existing suite keeps running against it. Atelier gets its own dedicated
  // specs (designer-atelier.spec.ts) that seed 'atelier' explicitly.
  //
  // Init scripts re-run on EVERY navigation (including page.reload) — an unconditional
  // setItem would stomp state the app itself persisted mid-test (e.g. after clicking the
  // Atelier toggle) and make persistence untestable. An app write always contains the full
  // designStore state (nodeStyle & friends); seeds only carry 1-2 keys — use that to only
  // seed fresh contexts.
  await page.addInitScript(() => {
    const raw = localStorage.getItem('nodepilot-design');
    let appWritten = false;
    try { appWritten = !!raw && JSON.parse(raw).state?.nodeStyle !== undefined; } catch { /* reseed */ }
    if (!appWritten) {
      localStorage.setItem('nodepilot-design', JSON.stringify({ state: { designerTheme: 'classic' }, version: 1 }));
    }
  });
  // Hermetic catch-all for any REST endpoint a test doesn't explicitly mock: return an
  // empty 200 array instead of falling through to the real backend, where the cookie-less
  // Playwright context gets a 401 → the client's interceptor redirects to /login and the
  // page under test never mounts.
  //
  // Match on `pathname.startsWith('/api/')` via a predicate — NOT the glob '**/api/**'.
  // The glob also matches Vite's own source modules served at '/src/api/*.ts' in dev, so it
  // would answer those JS module requests with `application/json`, triggering a strict-MIME
  // "Failed to load module script" error that white-screens every lazy-loaded page chunk.
  // `[]` (not 204) keeps list consumers' `.map` working; object consumers see harmless
  // `undefined`. Registered FIRST so every specific mock below — and every per-test
  // `page.route` — wins (Playwright resolves the most-recently-added matching route first).
  await page.route(
    (url) => url.pathname.startsWith('/api/'),
    (route) => emptyArray(route),
  );

  // Auth — mimic a logged-in admin via the cookie-based H-5 flow.
  await page.route('**/api/auth/me', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(MOCK_USER) }),
  );

  // Host identity for the TopBar chip — every authenticated page renders it. Without an
  // explicit object the catch-all returns `[]` and the chip (correctly) hides itself.
  await page.route('**/api/system/host-info', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(MOCK_HOST) }),
  );

  // Workflows list — empty by default; tests that need a specific workflow
  // override this with `page.route('**/api/workflows', ...)` after install.
  await page.route('**/api/workflows', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );

  // Machines / credentials / globals — empty fleet so dropdowns render but
  // don't surprise the test with stale data.
  await page.route('**/api/machines', (route) => emptyArray(route));
  await page.route('**/api/credentials', (route) => emptyArray(route));
  await page.route('**/api/global-variables', (route) => emptyArray(route));

  // Audit / executions also empty by default. (The catch-all already covers query-string
  // variants like /executions?workflowId=… — these explicit entries just document intent.)
  await page.route('**/api/executions', (route) => emptyArray(route));
  await page.route('**/api/audit', (route) => emptyArray(route));

  // SignalR negotiation — return a 404 so the client falls back to long-polling
  // and immediately gives up. Without this the editor sits in a perpetual
  // "connecting..." state and breaks the redirect-after-mount expectations.
  await page.route('**/hubs/**', (route) =>
    route.fulfill({ status: 404, body: 'mocked: SignalR disabled' }),
  );
}

function emptyArray(route: Route) {
  return route.fulfill({ status: 200, contentType: 'application/json', body: '[]' });
}

/**
 * Seed the designer into "expert" mode before the SPA boots. The default ("standard") mode hides
 * power-user affordances — node-context-menu breakpoints, the Debug-run toolbar button, and most
 * view-toggles — behind `designerMode === 'expert'` (designStore, persisted under the key
 * 'nodepilot-design', schema version 1). Specs that exercise those features must run in expert
 * mode. Call this BEFORE `page.goto(...)` so the init script wins over the store's default.
 */
export async function seedExpertMode(page: Page) {
  // Init scripts run in addition order and the LAST setItem wins — this seed replaces the
  // whole 'nodepilot-design' key, so it must re-assert the classic pin from
  // installDefaultMocks or expert-mode specs would silently flip to the Atelier design.
  await page.addInitScript(() =>
    localStorage.setItem(
      'nodepilot-design',
      JSON.stringify({ state: { designerMode: 'expert', designerTheme: 'classic' }, version: 1 }),
    ),
  );
}

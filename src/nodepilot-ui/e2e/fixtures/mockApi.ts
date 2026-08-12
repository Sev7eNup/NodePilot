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

/** Frontend mirror of GET /api/ai/knowledge/capabilities (kept inline to avoid importing src/).
 *  `llm` is the raw "LLM endpoint usable" flag that gates every AI button's visibility;
 *  `enabled` additionally requires the AiKnowledge master switch and gates the AI-Chat nav. */
export interface KnowledgeCapabilities {
  enabled: boolean;
  llm: boolean;
  docs: boolean;
  operational: boolean;
  sourceCode: boolean;
  db: boolean;
}

/** Default caps: everything on (Admin/Operator view of a fully enabled install). */
export function capsJson(overrides: Partial<KnowledgeCapabilities> = {}): KnowledgeCapabilities {
  return { enabled: true, llm: true, docs: true, operational: true, sourceCode: true, db: true, ...overrides };
}

/** Mocks GET /api/ai/knowledge/capabilities with a JSON object — overrides the suite default
 *  from `installDefaultMocks` (Playwright resolves the most-recently-added route first). */
export async function mockCaps(page: Page, caps: KnowledgeCapabilities) {
  await page.route('**/api/ai/knowledge/capabilities**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(caps) }),
  );
}

export async function installDefaultMocks(page: Page) {
  // Pin the designer to the CLASSIC look AND the small node scale for the whole hermetic suite.
  // Both are geometry knobs the canvas assertions in these specs were written against:
  //  - designerTheme (default 'atelier') re-tokenises colors/geometry; the classic look must stay
  //    byte-identical, so the entire existing suite keeps running against it. Atelier gets its own
  //    dedicated specs (designer-atelier.spec.ts) that seed 'atelier' explicitly.
  //  - nodeScaleIndex (default 3 = `lg` since the v2 store bump) changes how much room a node
  //    occupies, and `fitView` turns that into a different pan/zoom for the same seeded positions.
  //    At `lg`, ai-assistant's step-b slid under the bottom-right minimap, which then swallowed the
  //    click (the hazard e2e/README.md already warns about). Pin `sm` so a future size tweak can
  //    never reshuffle unrelated specs' canvas coordinates; the scale itself is covered by unit
  //    tests (designStore.test.ts, CanvasSettings.test.tsx).
  // `version: 2` matches the store's current persist version so the seed is taken as-is — at
  // version 1 the store's own migration would read this pinned `sm` as "still on the old default"
  // and lift it straight back to `lg`.
  //
  // Init scripts re-run on EVERY navigation (including page.reload) — an unconditional
  // setItem would stomp state the app itself persisted mid-test (e.g. after clicking the
  // Atelier toggle) and make persistence untestable. An app write always contains the full
  // designStore state (nodeStyle & friends); seeds only carry a few keys — use that to only
  // seed fresh contexts.
  await page.addInitScript(() => {
    const raw = localStorage.getItem('nodepilot-design');
    let appWritten = false;
    try { appWritten = !!raw && JSON.parse(raw).state?.nodeStyle !== undefined; } catch { /* reseed */ }
    if (!appWritten) {
      localStorage.setItem('nodepilot-design', JSON.stringify({
        state: { designerTheme: 'classic', nodeScaleIndex: 1 }, version: 2,
      }));
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

  // Database health — polled app-wide by useDatabaseHealth (mounted once in App). It lives
  // under /healthz, NOT /api, so the predicate catch-all never answers it; without this mock
  // the vite preview serves index.html (200, text/html) for the path, the probe's
  // content-type guard reads that as "process unreachable", and the whole suite renders the
  // TopBar pill red. Outage specs override this route after install (last-registered wins).
  await page.route('**/healthz/database', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ status: 'ok', sinceUtc: null, reason: null }),
    }),
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

  // AI capabilities — an OBJECT endpoint the catch-all would answer with `[]`. The default is
  // deliberately `llm: true, enabled: false`: `llm` gates the designer KI-Assistent, the
  // script-editor KI button and "New AI Workflow" (all unconditionally visible before the
  // gating existed → this keeps every spec's DOM unchanged), while `enabled` gates the AI-Chat
  // nav entry, which the catch-all's `[]` always kept hidden. Do NOT "improve" this to
  // all-true — the AI-Chat nav entry would appear suite-wide and break sidebar assertions.
  // Override per test with `mockCaps(page, capsJson({...}))`.
  await page.route('**/api/ai/knowledge/capabilities**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify(capsJson({ enabled: false, docs: false, operational: false, sourceCode: false, db: false })),
    }),
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

  // Dashboard aggregate — the ONE endpoint the landing page ('/') is built from, and the source
  // of the sidebar nav badges. It must be an object: the catch-all's `[]` is truthy, so the page
  // gets past its `!stats` guard and then dies on `stats.last24h.total` inside the router's error
  // boundary. Any spec that merely passes through '/' was racing that crash and only stayed green
  // by asserting fast enough. Empty-but-valid, like the list mocks above; specs wanting real
  // numbers override this after install (Playwright resolves the most-recently-added route first).
  await page.route('**/api/stats/dashboard**', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        workflowsTotal: 0, workflowsEnabled: 0, machinesTotal: 0, machinesReachable: 0,
        executionsTotal: 0,
        last24h: { total: 0, succeeded: 0, failed: 0, running: 0, cancelled: 0 },
        last24hBuckets: [], topWorkflows: [], running: [], recent: [], armedTriggers: [],
        pendingCount: 0, runningCount: 0, longRunningCount: 0,
        failingWorkflows: [], editLocks: [], healthHeartbeats: [],
        databaseProvider: 'postgres', clusterRole: null, recentAudit: null, llmEnabled: false,
      }),
    }),
  );

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
 * 'nodepilot-design', schema version 2). Specs that exercise those features must run in expert
 * mode. Call this BEFORE `page.goto(...)` so the init script wins over the store's default.
 */
export async function seedExpertMode(page: Page) {
  // Init scripts run in addition order and the LAST setItem wins — this seed replaces the
  // whole 'nodepilot-design' key, so it must re-assert BOTH pins from installDefaultMocks
  // (classic look + small node scale) or expert-mode specs would silently flip to the Atelier
  // design and the large node geometry.
  await page.addInitScript(() =>
    localStorage.setItem(
      'nodepilot-design',
      JSON.stringify({
        state: { designerMode: 'expert', designerTheme: 'classic', nodeScaleIndex: 1 },
        version: 2,
      }),
    ),
  );
}

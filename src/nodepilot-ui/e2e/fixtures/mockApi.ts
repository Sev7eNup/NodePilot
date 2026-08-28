import type { Page, Route } from '@playwright/test';

/**
 * API mocks shared across E2E tests. Built on `page.route()` so every test gets its own
 * deterministic backend without starting the real ASP.NET Core host.
 *
 * Add new mocks by composing the helpers below; each matches a URL pattern and returns
 * canned JSON. To override a default (for example to simulate a 500), call `page.route()`
 * after `installDefaultMocks()`: Playwright resolves the most recently added route first.
 */

export const MOCK_USER = {
  // authStore.initialize() reads `me.id` into userId, the field the edit-lock UI compares
  // against Workflow.checkedOutByUserId.
  id: '00000000-0000-0000-0000-000000000099',
  username: 'e2e-admin',
  role: 'Admin',
};

// Host identity shown by the TopBar chip (GET /api/system/host-info). Exported so specs can
// assert against the same values they are mocked with.
export const MOCK_HOST = {
  machineName: 'NPSRV01',
  fqdn: 'npsrv01.corp.example.local',
  domain: 'corp.example.local',
};

/** Frontend mirror of GET /api/ai/knowledge/capabilities, inline to avoid importing src/.
 *  `llm` reports that the LLM endpoint is usable and gates every AI button's visibility;
 *  `enabled` also requires the AiKnowledge master switch and gates the AI-Chat nav entry. */
export interface KnowledgeCapabilities {
  enabled: boolean;
  llm: boolean;
  docs: boolean;
  operational: boolean;
  sourceCode: boolean;
  db: boolean;
}

/** Default caps: everything on, the global-admin view of a fully enabled install. */
export function capsJson(overrides: Partial<KnowledgeCapabilities> = {}): KnowledgeCapabilities {
  return { enabled: true, llm: true, docs: true, operational: true, sourceCode: true, db: true, ...overrides };
}

/** Mocks GET /api/ai/knowledge/capabilities with a JSON object, overriding the suite default
 *  from `installDefaultMocks`: Playwright resolves the most recently added route first. */
export async function mockCaps(page: Page, caps: KnowledgeCapabilities) {
  await page.route('**/api/ai/knowledge/capabilities**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(caps) }),
  );
}

export async function installDefaultMocks(page: Page) {
  // Pin the designer to the classic look and the small node scale for the whole hermetic suite.
  // Both are geometry knobs the canvas assertions rely on:
  //  - designerTheme re-tokenises colors and geometry; the Atelier look has its own specs
  //    (designer-atelier.spec.ts) that seed 'atelier' explicitly.
  //  - nodeScaleIndex changes how much room a node occupies, and `fitView` turns that into a
  //    different pan/zoom for the same seeded positions, which can move nodes under the minimap.
  //    The scale itself is covered by unit tests (designStore.test.ts, CanvasSettings.test.tsx).
  // `version: 3` matches the store's current persist version, so the seed is taken as-is
  // instead of being migrated back to the current default.
  //
  // Init scripts re-run on every navigation, page.reload included, so an unconditional setItem
  // would stomp state the app itself persisted mid-test and make persistence untestable. An app
  // write always contains the full designStore state (nodeStyle and friends) while a seed
  // carries only a few keys; use that to seed fresh contexts only.
  await page.addInitScript(() => {
    const raw = localStorage.getItem('nodepilot-design');
    let appWritten = false;
    try { appWritten = !!raw && JSON.parse(raw).state?.nodeStyle !== undefined; } catch { /* reseed */ }
    if (!appWritten) {
      localStorage.setItem('nodepilot-design', JSON.stringify({
        state: { designerTheme: 'classic', nodeScaleIndex: 1 }, version: 3,
      }));
    }
  });
  // Catch-all for any REST endpoint a test does not mock explicitly: return an empty 200
  // array instead of falling through to the real backend, where the cookie-less Playwright
  // context gets a 401 and the client's interceptor redirects to /login.
  //
  // The match is a `pathname.startsWith('/api/')` predicate, not the glob '**/api/**': that
  // glob also matches Vite's own source modules served at '/src/api/*.ts' in dev and would
  // answer them as `application/json`, which fails the strict MIME check and white-screens
  // every lazy-loaded page chunk. `[]` rather than 204 keeps list consumers' `.map` working.
  // Registered first, so every specific mock below and every per-test `page.route` wins.
  await page.route(
    (url) => url.pathname.startsWith('/api/'),
    (route) => emptyArray(route),
  );

  // Database health, polled app-wide by useDatabaseHealth. The path sits under /healthz, not
  // /api, so the predicate catch-all never answers it. Unmocked, the vite preview serves
  // index.html for it and the probe's content-type guard reads that as unreachable, turning
  // the TopBar pill red suite-wide. Outage specs override this route after install.
  await page.route('**/healthz/database', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify({ status: 'ok', sinceUtc: null, reason: null }),
    }),
  );

  // Auth: mimic a logged-in admin through the cookie-based login flow.
  await page.route('**/api/auth/me', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(MOCK_USER) }),
  );

  // Host identity for the TopBar chip, which every authenticated page renders. Without an
  // explicit object the catch-all returns `[]` and the chip hides itself.
  await page.route('**/api/system/host-info', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(MOCK_HOST) }),
  );

  // AI capabilities: an object endpoint the catch-all would answer with `[]`. The default is
  // `llm: true, enabled: false`. `llm` gates the designer AI assistant, the script-editor AI
  // button and "New AI Workflow"; `enabled` gates the AI-Chat nav entry, which has to stay
  // hidden here or sidebar assertions across the suite fail. Override per test with
  // `mockCaps(page, capsJson({...}))`.
  await page.route('**/api/ai/knowledge/capabilities**', (route) =>
    route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify(capsJson({ enabled: false, docs: false, operational: false, sourceCode: false, db: false })),
    }),
  );

  // Workflows list, empty by default; tests that need a specific workflow
  // override this with `page.route('**/api/workflows', ...)` after install.
  await page.route('**/api/workflows', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );

  // Machines, credentials and globals: an empty fleet, so dropdowns render
  // without pulling in unrelated data.
  await page.route('**/api/machines', (route) => emptyArray(route));
  await page.route('**/api/credentials', (route) => emptyArray(route));
  await page.route('**/api/global-variables', (route) => emptyArray(route));

  // Audit and executions are empty by default. The catch-all already covers query-string
  // variants such as /executions?workflowId=x; these explicit entries document intent.
  await page.route('**/api/executions', (route) => emptyArray(route));
  await page.route('**/api/audit', (route) => emptyArray(route));

  // Dashboard aggregate: the single endpoint the landing page ('/') is built from and the
  // source of the sidebar nav badges. It must be an object, because the catch-all's `[]` is
  // truthy, so the page passes its `!stats` guard and then fails on `stats.last24h.total`.
  // Empty but valid, like the list mocks above; specs that need real numbers override this
  // after install.
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

  // SignalR negotiation: a 404 makes the client fall back to long-polling and give up at
  // once. Without it the editor stays in a connecting state, which breaks the
  // redirect-after-mount expectations.
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
 * 'nodepilot-design', schema version 3). Specs that exercise those features must run in expert
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
        version: 3,
      }),
    ),
  );
}

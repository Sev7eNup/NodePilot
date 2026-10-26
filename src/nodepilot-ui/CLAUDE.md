# nodepilot-ui — Frontend Conventions

Applies to `src/nodepilot-ui/`. Project-wide rules are in the root `CLAUDE.md`.

## Structure

- **New page:** in `src/pages/`, route in `App.tsx`
- **New custom node:** in `src/components/designer/nodes/`, register in the `nodeTypes` map
- **New activity (UI side):** entry in `library/activityCategories.ts` (`buildActivityCategories`) + `*Config` component under `properties/activities|triggers/` + one line in `properties/activityConfigMap.ts` — `PropertiesPanel.tsx` is **not** edited. Maintain the catalogue mirror `lib/activityCatalog.generated.ts` by hand (no codegen script); `ActivityCatalogFrontendSyncTests` enforces parity with the backend catalogue. The mirror carries **no** `prompt` field any more — there is no prompt exclusion list, every activity is known to the AI. Downstream outputs in `describeNodeOutputs` in `lib/upstreamVariables.ts`.

## Stack Rules

- UI strings via `react-i18next` (namespaces per `src/i18n/locales/{de,en}/`, default DE) — new visible strings in **both** languages.
- **Colours always via design tokens/CSS variables** — never hardcode Tailwind colour literals (`text-gray-900`, `bg-white` break the dark skins). Native `<select>`: `option:hover` is not stylable in Chromium → use the custom dropdown component.
- Client state via Zustand stores (`src/stores/`), server state via TanStack React Query (`refetchOnWindowFocus:false`, SignalR invalidates caches).
- **Typography:** `IBM Plex Sans Variable` (`--font-headline`/`--font-body`/`--font-label`, all three identical) + `IBM Plex Mono` (`--font-mono`), declared in the `@theme` of `index.css`. Both are self-hosted via fontsource — **no external font requests**: the prod CSP has no `font-src` and falls back to `default-src 'self'`, so a CDN font would be blocked in production. Body carries a `font-size-adjust` as the only density compensation (Plex has a smaller x-height than the previously used Inter); monospace is exempt. Monaco cannot use a CSS variable and keeps the stack as `MONO_FONT_STACK` in `lib/monacoSetup.ts` — `fontTokens.test.ts` keeps both sides identical. The docs website deliberately runs its own type system (Geist + JetBrains Mono).

- **Design tokens in JS:** The prod build minifies the CSS with Lightning CSS and shortens colours **inside** custom properties (`#ffffff` → `#fff`, `#ff0000` → `red`); the dev server does not — a raw token value is therefore not a reliable hex colour. Every JS reader of a `--color-*` token therefore normalises via `cssColorToHex` from `lib/cssColor.ts`: the Monaco theme bridge in `designer/ScriptEditorDialog.tsx` (Monaco accepts only 6-/8-digit hex and **throws** otherwise) and the ECharts tokens in `lib/chartTheme.ts`. The helper handles hex, `rgb()/rgba()` and named colours; `oklch()`/`color-mix()` deliberately return `null` → fallback of the caller. Regression protection is `e2e/script-editor.spec.ts`, because only the E2E suite runs against the **built** bundle.

- **Form fields — two levels, not one.** Which one applies is decided by the surface underneath:
  - **`.input-field`** (index.css) for fields on a raised surface: designer property panels, `.np-card`. The style is *sunken* (darker than the container + inset shadow) and **presupposes this raising**. Hand-built `border …` chains are wrong there.
  - **Outline chain** `px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500` (+ `w-full`, for `<select>` additionally `bg-surface-lowest`) for **page dialogs** — `ModalShell` sits on `surface-lowest`, and the panel raise `.np-modal-panel` exists **only in the dark skins**. An `.input-field` paints itself in panel colour there: on `light-grey` a flat beige box on white that becomes *lighter* than the panel on focus and loses its outline. This is how GlobalVariablesPage, MachinesPage, UsersPage, MaintenanceWindowsPage, the alerting editors and CustomActivitiesPage do it.
  - `focus:ring-blue-500` is **not** a colour-literal violation here: index.css remaps the blue ring utilities via `np-accent-remap` to `--np-accent-ring`, so the ring follows the skin accent.
  - **Never bake width into a shared field constant.** Two competing utilities of the same Tailwind layer are resolved by their order in the generated stylesheet, not by the order in the class attribute — `w-full` in the constant and `w-28` on the field is a coin toss. Likewise: `.input-field` is **unlayered** and therefore beats *every* utility from `@layer utilities`; a `w-28 text-xs` next to it has no effect.

## E2E (Playwright)

Hermetic specs in `e2e/` — all APIs mocked via `page.route` (no backend/Postgres needed; predicate catch-all in `e2e/fixtures/mockApi.ts`). **Before writing new specs: read `e2e/README.md`.** Fast iteration against a running dev server (no build): `npx playwright test <spec> --config=playwright.dev.config.ts`.

## Browser Demo (`demo/`)

The same SPA against an in-memory backend, published on www.nodepilot.run under `/demo/`. No
server, no DB — state lives per tab and a reload restores the seed.

- **Entry and build:** `demo.html` → `demo/main.demo.ts` → third Vite build
  (`vite.demo.config.ts` → `dist-demo/`, `npm run build:demo`). `demo/` deliberately sits **next to**
  `src/` and `e2e/`, not inside them. The product build cannot contain the demo code, because
  `index.html` never references it; `ci.yml` checks this on the built `dist/` via a sentinel.
  The model is `src/preview/` with `skin-preview.html` — extra entries that never land in `dist/`.
- **The dependency direction is exclusively `demo/` → `src/`.** `src/` never imports from
  `demo/`; instead the demo entry injects: `setExecutionHubFactory` (`lib/hubConnection.ts`),
  `setDocsHref` (`lib/docsLink.ts`) and `isolateAuthBoundaryTransport` (`security/authBoundary.ts`).
  `isolation.test.ts` pins the direction.
- **Interception is a `fetch` patch, not a service worker.** Reasons: no `mockServiceWorker.js` to
  ship, no worker lifecycle with stale mocks after a redeploy, no registration order against the
  module-scope `fetch` in `src/main.tsx`. (A worker's scope would **not** have prevented intercepting
  `/api` — the argument is maintainability, not reach.)
- **`__NP_DEMO__`** selects the router base `/demo` instead of `/`. Apache serves the demo start file
  for demo page paths; assets use `/demo/`. The demo entry normalises old hash links. Defined as `'false'` in `vite.config.ts` **and**
  `vitest.config.ts` — if the second is missing, every test that imports `App` breaks
  with a `ReferenceError` that looks like something else.
- **Tab isolation is not free.** Real sign-in, sign-out and session changes empty the caches in other
  tabs and rebuild protected components. Merely observing the same session
  when opening a tab preserves drafts. The independent demo worlds must not share even real
  auth changes; the entry therefore silences the transport in both directions.
- **Seed:** the workflow JSONs from `demo/seed/fixtures/` and `scripts/` — **no** graph is created in the demo package.
  Nodes with several incoming edges get a `waitAll` junction on load (like the
  designer and SCOrch import), otherwise the pre-publish check blocks. The order in
  `seed/graphs.ts` is load-bearing: `seed/executions.ts` indexes its cadence by position, and only
  slots 0 and 2 fall in the 30-minute window of Live-Ops. **Only `seed/entities.ts` and
  `seed/graphs.ts` contain fixed facts** — history, dashboard and counters are derived from them,
  otherwise one page shows 14 runs and the next 15.
- **Run:** `run/player.ts` simulates the **current** canvas graph (via
  `lib/workflowSimulation.ts`), not a recorded script — a self-built workflow
  therefore runs the same way. Each step is **first written to the world, then delivered as an
  event**: the ops feed invalidates query keys on every event, and the refetch hits the
  fake backend.
- **Real lifecycle instead of shortcuts:** Lock disables the workflow (atomically, as in the product),
  and `useWorkflowExecution` refuses the run — edit → publish → execute is
  therefore the chain the smoke test walks.
- **Every mutation is served or names its refusal.** A button that neither works nor explains
  itself is the defect — not the missing feature. Server-side things answer via
  `notInDemo(<action>)` (`net/respond.ts`); the fallback in `net/install.ts` is the emergency brake, not a
  design. **This also applies to downloads:** `downloadFromApi` throws, and a button that does not
  handle its promise turns the refusal into an unhandled rejection.
- **The smoke test really saves.** Walking routes only finds endpoints that are called on load, and
  opening an editor proves nothing about saving — both gaps originated exactly there. Hence: one real
  save per page plus targeted tests for rollback, move, recursive delete and the rejected downloads.
- **Admin settings are served, not blocked.** `demo/handlers/settings.ts` returns the product's own
  defaults per section (the same literals the cards carry as fallback),
  writing answers 501. This is mandatory, not a convenience: `useSectionForm` replaces its
  fallback with `data.payload` as soon as a response arrives, and several cards read
  nested (`payload.proxy.password`) — a partial envelope crashes the page instead of degrading it.
- **Check response formats against the real consumer, not against themselves.** The expensive bugs
  were all key differences that a test against the handler's own shape never sees:
  `{folderId}` instead of `{targetFolderId}`, `id` instead of `workflowId`/`executionId`, arrays instead of
  objects indexed by step ID, `{type,event}` instead of `{type,evt}`. The tests therefore run
  through `api/client.ts`, `api/paging.ts`, `api/sharedFolders.ts`, `api/adminSettings.ts` and the
  real `useLiveOpsFeed` hook.
- **The smoke test was blind in three ways — each blindness let a real crash through:**
  1. `waitForLoadState('networkidle')` is meaningless here. The patched `fetch` never reaches
     the network, Playwright sees zero requests and immediately reports "idle" while the page is still
     waiting for data. `net/install.ts` therefore publishes the counters `__npDemoPending` and
     `__npDemoCompleted`; the test waits for **quiet after activity** (waiting only for "nothing in flight"
     is trivially satisfied at t=0, before the first request has even started).
  2. A mere hash change is a same-document navigation — React may mount the new route only
     after the check. The walk therefore appends its own query to each route, which forces a
     real page load.
  3. A 404 image logs nothing; it silently renders its alt text. **Every** `<img>` on every
     route is therefore checked, not the first one in the sidebar.
- **An error boundary makes a crash invisible.** React hands the error to the boundary
  instead of the window, `pageerror` does not fire, and a route walk reports "clean". The
  smoke test therefore additionally checks that no boundary is rendered — and walks the
  settings **sub-tabs** individually, because the crash lay behind the System tab. Matching is on
  the page text, **not** on `[role="alert"]`: React Router's default error element does not carry
  this role — and that is exactly how the crashed alerting page slipped through as "clean".
  The routes now show the app's localised fallback as `errorElement`; the text match
  covers it in both languages and the router default.
- **An `<a href="/api/…">` never goes through the `fetch` patch.** It is a document navigation:
  under a subpath the visitor lands on the host's 404, and the in-memory world is rebuilt on
  return. `demo/net/anchors.ts` therefore intercepts every same-origin click that would leave
  the demo directory, fetches it through the patch and stores a response with
  `Content-Disposition` as a blob. The audit export is the case that exists today; the guard covers
  the class. The smoke test lets only anchors to `/api/` pass as "interceptable" and checks by
  behaviour that the export does not navigate.
- **Unknown writes answer 501, never 200.** A silent success let deleting a
  user succeed while nothing changed.
- **Tests:** `npx vitest run src/__tests__/demo` and
  `npm run test:e2e:demo` (builds `dist-demo` and serves it under a **subpath** — at the root,
  base-path errors would go unnoticed). Unhandled endpoints log
  `[demo] unhandled …`; the smoke test fails on any such line.
- **Guided entry:** `?tour=file&lang=de|en` accompanies the native start dialog and the
  execution history; `?tour=diagnose` opens a prepared copy error.
  `demo/ui/tour.ts` stays outside the React product tree. The graph comes from
  `scripts/example-guided-file-workflow.json`; `demo/run/fileScenario.ts` derives its
  simulated outputs from the start parameters. `Protected` fails at File Copy,
  subsequent steps do not run. Retry keeps the original inputs.
  The guided scenarios are covered in `e2e-demo/guided-tour.spec.ts`.
  Eight further tasks (`?tour=build|decision|parallel|service|live|versions|machine|maintenance`)
  are in `demo/ui/additionalTours.ts`, their seed data in `demo/seed/missionFixtures.ts` and the
  run simulation in `demo/run/missionScenarios.ts`; covered in `e2e-demo/additional-tours.spec.ts`.
  The website route `/walkthrough/` links all ten tasks.

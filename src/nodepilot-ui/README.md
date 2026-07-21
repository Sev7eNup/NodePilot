# nodepilot-ui

The NodePilot single-page app: workflow designer (React Flow), execution monitor (SignalR live status), admin/settings surfaces, and the operator dashboards. Talks to the NodePilot API over REST + SignalR.

**Stack:** React 19 · TypeScript · Vite 8 · Tailwind CSS 4 · @xyflow/react (React Flow v12) · TanStack React Query (server state) · Zustand (client state) · react-i18next (DE default, EN) · SignalR (`@microsoft/signalr`) · CodeMirror (script editing) · Vitest + Playwright (tests).

## Prerequisites

- Node.js LTS (18+).
- For full local use, the **NodePilot API on `http://localhost:5000`** (see the repo root `CLAUDE.md` → "Projekt starten": start Postgres, then `dotnet run`). The dev server proxies to it.

## Develop

```bash
npm install
npm run dev          # Vite dev server on http://localhost:5173
```

The dev server (`vite.config.ts`) proxies `/api`, `/healthz`, and `/hubs` (WebSocket) to `http://localhost:5000`, so the SPA runs against the real backend without CORS config. First login on an empty DB creates the admin account.

## Build / lint

```bash
npm run build         # tsc -b && vite build  → dist/
npm run lint          # eslint .
npm run preview       # serve the production build locally
```

## Test

```bash
npm test              # Vitest (watch)
npm run test:run      # Vitest (CI, single run)
npm run test:coverage # Vitest + coverage; thresholds in vitest.config.ts gate CI
npm run test:e2e      # Playwright e2e — hermetic, all APIs mocked (no backend needed)
```

E2E specs live in `e2e/`; **read `e2e/README.md` first** (coverage map, the `page.route` catch-all mock in `e2e/fixtures/mockApi.ts`, conventions). Fast iteration against a running dev server (no build): `npx playwright test <spec> --config=playwright.dev.config.ts`.

## Layout & conventions

- `src/pages/` — routed pages (routes in `src/App.tsx`).
- `src/components/designer/` — React Flow canvas. Custom nodes in `nodes/` (registered in the `nodeTypes` map); activity/trigger config editors in `properties/activities|triggers/`, registered one-line in `properties/activityConfigMap.ts`; palette from `library/activityCategories.ts` (`buildActivityCategories`).
- `src/lib/activityCatalog.generated.ts` — frontend mirror of the backend `NodePilot.Core.Activities.ActivityCatalog`. **No codegen script — hand-edit to match the backend.** `ActivityCatalogFrontendSyncTests` (xUnit) fails if it drifts. The derived `REMOTE_ACTIVITY_TYPES` / `TIMEOUT_ACTIVITY_TYPES` / control-flow sets come from here.
- `src/stores/` — Zustand stores (auth, design, theme, sidebar, …). Server state goes through React Query (`refetchOnWindowFocus:false`; SignalR invalidates caches).
- `src/i18n/locales/{de,en}/` — react-i18next namespaces (DE default). New visible strings go in **both** languages.

Backend/architecture conventions (adding an activity end-to-end, API, auth, the data bus) live in the repo-root `CLAUDE.md`.

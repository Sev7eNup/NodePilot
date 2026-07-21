# NodePilot E2E suite — coverage & conventions

Hermetic Playwright specs for the React SPA. They mock every API call with `page.route`
(no backend, no Postgres, no WinRM) and run against a `vite preview` build. The full
`E2ETests.md` catalogue (78 Teile) is the source of truth; this suite is the **automated
UI subset**. Backend-only and environment-bound scenarios are covered elsewhere (noted below).

## Running

```bash
npm run test:e2e          # real config: builds the SPA, serves via vite preview :4173
# fast local iteration against a running dev server (:5173), no build:
npx playwright test <spec> --config=playwright.dev.config.ts
```

The nightly Task Scheduler job (`scripts/nightly-tests.ps1`) runs `npm run test:e2e`
alongside `dotnet test` and `npm run test:run`.

## Conventions (read before adding specs)

- `installDefaultMocks(page)` (from `fixtures/mockApi.ts`) installs a **predicate catch-all**
  returning `[]` (200) for any unmocked `/api/*` request, so a page never 401-redirects to
  `/login`. It also mocks `/api/auth/me` (`MOCK_USER` = admin), `/hubs/**` (404), and empty
  lists. Add per-test `page.route(...)` AFTER it for specific data (last-registered wins;
  match query-string URLs with a trailing `**`).
- `installDefaultMocks` also **pins the designer to the CLASSIC look** (localStorage seed
  `designerTheme: 'classic'`) — the Atelier design (default for fresh real profiles) re-tokenises
  colors/geometry the existing visual assertions were written against. The seed is skipped once
  the app itself persisted a full designStore state (so mid-test toggles survive `page.reload`).
  Atelier-path specs live in `designer-atelier.spec.ts` and seed `'atelier'` explicitly.
- **The preview build renders the UI in English** (i18n falls back to EN). Use
  language-agnostic selectors: `getByRole` with bilingual regex (`/save|speichern/i`),
  input attributes, `getByText(/…/i)`. A few editor strings stay German even under EN.
- Editor in **State B (editable)**: mock `**/api/workflows/<id>` with
  `checkedOutByUserId: MOCK_USER.id` and the `definitionJson` you need; seed nodes clustered
  top-left (React Flow virtualizes off-screen nodes and the bottom-right minimap intercepts
  clicks). Assert graph mutations via the **Save PUT body**, not canvas DOM node counts.
- Simulate roles by overriding `/api/auth/me` with `role: 'Viewer' | 'Operator'`.
- **React Flow canvas drag is not synthesizable** by Playwright (node move, marquee,
  edge-handle connect, drag-to-pan, HTML5 drag-onto-canvas). Use keyboard / context-menu /
  click-add / pre-seeded `definitionJson` alternatives, or `test.skip(true, '<reason>')` —
  never fake a pass.
- **SignalR is mocked to 404**, so live execution progress never streams. Assert request
  contracts and REST-hydrated terminal states instead; `test.skip` anything that needs the
  live channel.

## Coverage map (E2ETests.md Teil → spec)

| Teil | Topic | Spec |
|---|---|---|
| 1 | Workflow-Management | `workflow-management.spec.ts` |
| 2 | Activity-type config UIs | `node-activity-config.spec.ts` |
| 3 | Node operations | `designer-nodes.spec.ts` |
| 4 | Edges & conditions | `designer-edges.spec.ts` |
| 5 | Properties & variables | `designer-properties.spec.ts` |
| 6 | Execution & debugging | `execution.spec.ts` |
| 7 | Error handling / lint | `error-handling.spec.ts` |
| 8 | Persistence / autosave | `persistence.spec.ts` |
| 9 | Special scenarios | `special-scenarios.spec.ts` |
| 10 | UI/UX (a11y, responsive) | `ui-ux.spec.ts` |
| 10 (mobile) | Smartphone-responsive UI (drawer, card lists, read-only mobile graph) | `mobile-responsive.spec.ts` |
| 11 | Dashboard | `dashboard.spec.ts` |
| 12 / 55 | Keyboard shortcuts | `shortcuts.spec.ts` |
| 13 | Sticky-note & group nodes | `special-nodes.spec.ts` |
| 14 | Trigger config UIs | `triggers.spec.ts` |
| 15 | Users / admin | `users.spec.ts` |
| 16 / 64 | Audit log + pagination/filter | `audit-log.spec.ts` |
| 17 | Theme / minimap | `theme.spec.ts` |
| 18 / 52 | Folders / move | `workflow-organisation.spec.ts` |
| 19 | Version diff | `version-diff.spec.ts` |
| 20 | Machines & credentials | `machines-credentials.spec.ts` |
| 21 | Global variables | `global-variables.spec.ts` |
| 25 | Auth lifecycle | `auth.spec.ts` |
| 26 | RBAC role-crossings | `rbac.spec.ts` |
| 27 | Edit-lock lifecycle | `edit-lock.spec.ts` |
| 28 | Step-test with context | `step-test.spec.ts` |
| 30 / 42 | Coverage heatmap | `coverage-heatmap.spec.ts` |
| 31 | DB-Admin viewer | `db-admin.spec.ts` |
| 36 | Activity catalog | `activity-catalog.spec.ts` |
| 37 | Diagnostics / support-log | `diagnostics.spec.ts` |
| 38 / 76 | Admin settings (all sections + probes) | `admin-settings.spec.ts` |
| 39 | Debug variable overrides | `debug-overrides.spec.ts` |
| 41 | Rollback with reason | `rollback.spec.ts` |
| 45 | Shared-folder permissions | `folder-permissions.spec.ts` |
| 48 | Output redaction (viewer) | `redaction-viewer.spec.ts` |
| 50 | Duplicate / export | `workflow-duplicate-export.spec.ts` |
| 51 | Step stats / health | `step-stats.spec.ts` |
| 53 | Schedule preview + AI generate | `schedule-ai.spec.ts` |
| 54 | Command palette / quick-switcher / find-replace | `designer-overlays.spec.ts` |
| 56 | Error notifications & empty states | `notifications-empty.spec.ts` |
| 57 | Execution Gantt / timeline | `execution-timeline.spec.ts` |
| 57 (list) | Executions list: search / status chips / workflow filter / sort / summary / refresh / open | `executions-list-ui.spec.ts` |
| 58 | Variable autocomplete & preview | `variable-autocomplete.spec.ts` |
| 59 | Paused variables inspector | `paused-inspector.spec.ts` |
| 60 | Bulk-edit & activity-type filter | `bulk-edit.spec.ts` |
| 61 | Edge-reshape handles | `edge-reshape.spec.ts` |
| 62 | Designer status banners | `status-banner.spec.ts` |
| 63 | Workflows list UI | `workflows-list-ui.spec.ts` |
| 65 | Credential/machine pickers | `properties-pickers.spec.ts` |
| 67 | Import dialog | `import-dialog.spec.ts` |
| 68 | Activity palette & context menu | `activity-palette.spec.ts` |
| 69 | Toolbar view-toggles | `toolbar-toggles.spec.ts` |
| 70 | Lint panel | `lint-panel.spec.ts` |
| 71 | Live console tabs/filter | `live-console.spec.ts` |
| 72 | Workflow breadcrumbs | `breadcrumbs.spec.ts` |
| 73 | Edge-label override | `edge-label.spec.ts` |
| 74 | Workflow snippets | `snippets.spec.ts` |
| 75 | Quick-interactions | `quick-interactions.spec.ts` |
| 77 | AI workflow assistant + SSE streaming (explain + propose, role-gated apply, stale-guard; chat/script SSE mocked via `text/event-stream` body) | `ai-assistant.spec.ts` |
| 9 (DR) | System-config Backup & Restore (export validation + restore preview/run) | `backup.spec.ts` |
| — (new) | Live-Ops Mission Control: pulse header, real-time execution timeline (running + recently-finished bars), bar drill-down + cancel, next-fires departure board, health rail, folder scoping | `operations.spec.ts` |
| 78 | Alerting rules (list/create/test-fire/secret-redaction/role-gating/gauge-scope-gate/deliveries-modal/cancelledBy-filter) | _vitest `AlertingPage.test.tsx` + `ConditionBuilderEventSource.test.tsx`; Playwright spec is a follow-up_ |
| — (new) | Atelier-Designsprache: Scope-Klassen + Token-Adaption, Skin-Adaption, Header-Umschalter (role=switch), Persistenz über Reload, Classic-Suite-Pin | `designer-atelier.spec.ts` |

## Not covered as UI e2e (by design)

**Backend / API-only — covered by `dotnet test` (`NodePilot.Api.Tests`, `Engine.Tests`, `Cli.Tests`):**
Teil 22 (SCOrch XML import parsing), 23 (external trigger API & idempotency), 29 (sub-workflow
contracts), 34 (migration drift / backend smoke — explicitly backend-only), 35 (secrets
re-encryption), 40 (force-unlock audit write — the UI force-unlock itself is in `rbac` /
`status-banner`), 44 (rate-limit headers), 46 (unresolved-template engine behaviour), 47
(idempotency-key TTL), 49 (execution retry / cancel-all — no rendered UI surface).

**Environment-bound — not hermetically reproducible:**
Teil 24 & 66 (real-time SignalR / connection status — WebSocket upgrade), 32 (LLM features —
need an LLM server), 33 (hardening flags — enforced only in the Production security pipeline),
43 (observability query — needs a Prometheus backend).

Within otherwise-covered Teile, individual scenarios that require canvas drag, live SignalR
streaming, real file-drop, or DevTools offline are marked `test.skip` with an inline reason.

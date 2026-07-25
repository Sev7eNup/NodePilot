# nodepilot-ui — Frontend-Konventionen

Gilt für `src/nodepilot-ui/`. Projektweite Regeln stehen in der Root-`CLAUDE.md`.

## Struktur

- **Neue Seite:** in `src/pages/`, Route in `App.tsx`
- **Neuer Custom Node:** in `src/components/designer/nodes/`, in der `nodeTypes` Map registrieren
- **Neue Activity (UI-Seite):** Eintrag in `library/activityCategories.ts` (`buildActivityCategories`) + `*Config`-Komponente unter `properties/activities|triggers/` + eine Zeile in `properties/activityConfigMap.ts` — `PropertiesPanel.tsx` wird **nicht** editiert. Katalog-Spiegel `lib/activityCatalog.generated.ts` von Hand pflegen (kein Codegen-Skript); `ActivityCatalogFrontendSyncTests` erzwingt Gleichstand mit dem Backend-Katalog. Downstream-Outputs in `describeNodeOutputs` in `lib/upstreamVariables.ts`.

## Stack-Regeln

- UI-Strings über `react-i18next` (Namespaces je `src/i18n/locales/{de,en}/`, Default DE) — neue sichtbare Strings in **beide** Sprachen.
- Client-State via Zustand-Stores (`src/stores/`), Server-State via TanStack React Query (`refetchOnWindowFocus:false`, SignalR invalidiert Caches).

## E2E (Playwright)

Hermetische Specs in `e2e/` — alle APIs via `page.route` gemockt (kein Backend/Postgres nötig; Predicate-Catch-All in `e2e/fixtures/mockApi.ts`). **Vor neuen Specs: `e2e/README.md` lesen.** Fast-Iteration gegen laufenden Dev-Server (kein Build): `npx playwright test <spec> --config=playwright.dev.config.ts`.

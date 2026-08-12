# nodepilot-ui — Frontend-Konventionen

Gilt für `src/nodepilot-ui/`. Projektweite Regeln stehen in der Root-`CLAUDE.md`.

## Struktur

- **Neue Seite:** in `src/pages/`, Route in `App.tsx`
- **Neuer Custom Node:** in `src/components/designer/nodes/`, in der `nodeTypes` Map registrieren
- **Neue Activity (UI-Seite):** Eintrag in `library/activityCategories.ts` (`buildActivityCategories`) + `*Config`-Komponente unter `properties/activities|triggers/` + eine Zeile in `properties/activityConfigMap.ts` — `PropertiesPanel.tsx` wird **nicht** editiert. Katalog-Spiegel `lib/activityCatalog.generated.ts` von Hand pflegen (kein Codegen-Skript); `ActivityCatalogFrontendSyncTests` erzwingt Gleichstand mit dem Backend-Katalog. Der Spiegel trägt **kein** `prompt`-Feld mehr — es gibt keine Prompt-Ausschlussliste, jede Activity ist der KI bekannt. Downstream-Outputs in `describeNodeOutputs` in `lib/upstreamVariables.ts`.

## Stack-Regeln

- UI-Strings über `react-i18next` (Namespaces je `src/i18n/locales/{de,en}/`, Default DE) — neue sichtbare Strings in **beide** Sprachen.
- Client-State via Zustand-Stores (`src/stores/`), Server-State via TanStack React Query (`refetchOnWindowFocus:false`, SignalR invalidiert Caches).
- **Typografie:** `IBM Plex Sans Variable` (`--font-headline`/`--font-body`/`--font-label`, alle drei identisch) + `IBM Plex Mono` (`--font-mono`), deklariert im `@theme` von `index.css`. Beide self-hosted über fontsource — **keine externen Font-Requests**: die Prod-CSP kennt kein `font-src` und fällt auf `default-src 'self'`, ein CDN-Font wäre in Produktion geblockt. Body trägt ein `font-size-adjust` als einzigen Dichte-Ausgleich (Plex hat eine kleinere x-Höhe als das früher genutzte Inter); Monospace ist davon ausgenommen. Monaco kann keine CSS-Variable verwerten und hält den Stack als `MONO_FONT_STACK` in `lib/monacoSetup.ts` — `fontTokens.test.ts` hält beide Seiten deckungsgleich. Die Doku-Website fährt bewusst ein eigenes Type-System (Geist + JetBrains Mono).

- **Formularfelder — zwei Ebenen, nicht eine.** Welche gilt, entscheidet die Fläche darunter:
  - **`.input-field`** (index.css) für Felder auf einer angehobenen Fläche: Designer-Property-Panels, `.np-card`. Der Stil ist *versenkt* (dunkler als der Container + Inset-Schatten) und **setzt diese Anhebung voraus**. Handgebaute `border …`-Ketten sind dort falsch.
  - **Umriss-Kette** `px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500` (+ `w-full`, bei `<select>` zusätzlich `bg-surface-lowest`) für **Seiten-Dialoge** — `ModalShell` sitzt auf `surface-lowest`, und die Panel-Anhebung `.np-modal-panel` existiert **nur in den Dark-Skins**. Ein `.input-field` malt sich dort in Panel-Farbe: auf `light-grey` ein flacher beiger Kasten auf Weiß, der beim Fokus *heller* wird als das Panel und seine Kontur verliert. So halten es GlobalVariablesPage, MachinesPage, UsersPage, MaintenanceWindowsPage, die Alerting-Editoren und CustomActivitiesPage.
  - `focus:ring-blue-500` ist hier **kein** Farbliteral-Verstoß: index.css remappt die Blau-Ring-Utilities über `np-accent-remap` auf `--np-accent-ring`, der Ring folgt also dem Skin-Akzent.
  - **Breite nie in eine geteilte Feld-Konstante backen.** Zwei konkurrierende Utilities derselben Tailwind-Layer werden über ihre Reihenfolge im generierten Stylesheet aufgelöst, nicht über die Klassen-Attribut-Reihenfolge — `w-full` in der Konstante und `w-28` am Feld ist ein Münzwurf. Ebenso: `.input-field` ist **unlayered** und schlägt damit *jede* Utility aus `@layer utilities`; ein `w-28 text-xs` daneben ist wirkungslos.

## E2E (Playwright)

Hermetische Specs in `e2e/` — alle APIs via `page.route` gemockt (kein Backend/Postgres nötig; Predicate-Catch-All in `e2e/fixtures/mockApi.ts`). **Vor neuen Specs: `e2e/README.md` lesen.** Fast-Iteration gegen laufenden Dev-Server (kein Build): `npx playwright test <spec> --config=playwright.dev.config.ts`.

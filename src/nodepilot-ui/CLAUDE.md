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

- **Design-Tokens in JS:** Der Prod-Build minifiziert das CSS mit Lightning CSS und kürzt Farben **innerhalb** von Custom-Properties (`#ffffff` → `#fff`, `#ff0000` → `red`), der Dev-Server nicht — ein roher Token-Wert ist also keine verlässliche Hex-Farbe. Jeder JS-Leser eines `--color-*`-Tokens normalisiert deshalb über `cssColorToHex` aus `lib/cssColor.ts`: die Monaco-Theme-Bridge in `designer/ScriptEditorDialog.tsx` (Monaco akzeptiert nur 6-/8-stelliges Hex und **wirft** sonst) und die ECharts-Tokens in `lib/chartTheme.ts`. Der Helfer beherrscht Hex, `rgb()/rgba()` und benannte Farben; `oklch()`/`color-mix()` liefern bewusst `null` → Fallback des Aufrufers. Regressionsschutz ist `e2e/script-editor.spec.ts`, weil nur die E2E-Suite gegen das **gebaute** Bundle fährt.

- **Formularfelder — zwei Ebenen, nicht eine.** Welche gilt, entscheidet die Fläche darunter:
  - **`.input-field`** (index.css) für Felder auf einer angehobenen Fläche: Designer-Property-Panels, `.np-card`. Der Stil ist *versenkt* (dunkler als der Container + Inset-Schatten) und **setzt diese Anhebung voraus**. Handgebaute `border …`-Ketten sind dort falsch.
  - **Umriss-Kette** `px-3 py-2 border border-outline-variant rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500` (+ `w-full`, bei `<select>` zusätzlich `bg-surface-lowest`) für **Seiten-Dialoge** — `ModalShell` sitzt auf `surface-lowest`, und die Panel-Anhebung `.np-modal-panel` existiert **nur in den Dark-Skins**. Ein `.input-field` malt sich dort in Panel-Farbe: auf `light-grey` ein flacher beiger Kasten auf Weiß, der beim Fokus *heller* wird als das Panel und seine Kontur verliert. So halten es GlobalVariablesPage, MachinesPage, UsersPage, MaintenanceWindowsPage, die Alerting-Editoren und CustomActivitiesPage.
  - `focus:ring-blue-500` ist hier **kein** Farbliteral-Verstoß: index.css remappt die Blau-Ring-Utilities über `np-accent-remap` auf `--np-accent-ring`, der Ring folgt also dem Skin-Akzent.
  - **Breite nie in eine geteilte Feld-Konstante backen.** Zwei konkurrierende Utilities derselben Tailwind-Layer werden über ihre Reihenfolge im generierten Stylesheet aufgelöst, nicht über die Klassen-Attribut-Reihenfolge — `w-full` in der Konstante und `w-28` am Feld ist ein Münzwurf. Ebenso: `.input-field` ist **unlayered** und schlägt damit *jede* Utility aus `@layer utilities`; ein `w-28 text-xs` daneben ist wirkungslos.

## E2E (Playwright)

Hermetische Specs in `e2e/` — alle APIs via `page.route` gemockt (kein Backend/Postgres nötig; Predicate-Catch-All in `e2e/fixtures/mockApi.ts`). **Vor neuen Specs: `e2e/README.md` lesen.** Fast-Iteration gegen laufenden Dev-Server (kein Build): `npx playwright test <spec> --config=playwright.dev.config.ts`.

## Browser-Demo (`demo/`)

Dieselbe SPA gegen ein In-Memory-Backend, veröffentlicht auf GitHub Pages unter `/demo/`. Kein
Server, keine DB — der Zustand lebt pro Tab und ein Reload stellt den Seed wieder her.

- **Entry und Build:** `demo.html` → `demo/main.demo.ts` → dritter Vite-Build
  (`vite.demo.config.ts` → `dist-demo/`, `npm run build:demo`). `demo/` liegt bewusst **neben**
  `src/` und `e2e/`, nicht darin. Der Produkt-Build kann den Demo-Code nicht enthalten, weil
  `index.html` ihn nie referenziert; `ci.yml` prüft das am gebauten `dist/` per Sentinel.
  Vorbild ist `src/preview/` mit `skin-preview.html` — Extra-Entries, die nie in `dist/` landen.
- **Abhängigkeitsrichtung ist ausschließlich `demo/` → `src/`.** `src/` importiert nie aus
  `demo/`; stattdessen injiziert der Demo-Entry: `setExecutionHubFactory` (`lib/hubConnection.ts`),
  `setDocsHref` (`lib/docsLink.ts`) und `isolateAuthBoundaryTransport` (`security/authBoundary.ts`).
  `isolation.test.ts` hält die Richtung fest.
- **Interception ist ein `fetch`-Patch, kein Service Worker.** Gründe: kein mitzulieferndes
  `mockServiceWorker.js`, kein Worker-Lebenszyklus mit veralteten Mocks nach einem Redeploy, keine
  Registrierungsreihenfolge gegen das Modul-scope-`fetch` in `src/main.tsx`. (Der Scope eines
  Workers hätte `/api` **nicht** verhindert — das Argument ist Wartbarkeit, nicht Reichweite.)
- **`__NP_DEMO__`** existiert für genau eine Entscheidung: Hash- statt Path-Routing, weil ein
  statischer Host unbekannte Pfade nicht umschreibt. In `vite.config.ts` **und**
  `vitest.config.ts` auf `'false'` definiert — fehlt das zweite, bricht jeder Test, der `App`
  importiert, mit einem `ReferenceError`, der nach etwas anderem aussieht.
- **Tab-Isolation ist nicht gratis.** Ein zweiter Tab publiziert ein Identitäts-Ereignis, und jeder
  andere Tab beantwortet es mit Cache-Leeren und Remount — ungespeicherte Designer-Änderungen sind
  weg. Deshalb legt der Entry den Transport beidseitig still.
- **Seed:** die Workflow-JSONs aus `samples/` und `scripts/` — im Demo-Paket entsteht **kein**
  Graph. Nodes mit mehreren eingehenden Kanten bekommen beim Laden eine `waitAll`-Junction (wie
  Designer und SCOrch-Import), sonst blockiert der Pre-Publish-Check. Die Reihenfolge in
  `seed/graphs.ts` ist tragend: `seed/executions.ts` indiziert seine Kadenz nach Position, und nur
  Slot 0 und 2 liegen im 30-Minuten-Fenster von Live-Ops. **Nur `seed/entities.ts` und
  `seed/graphs.ts` enthalten gesetzte Fakten** — Historie, Dashboard und Zähler werden daraus
  abgeleitet, sonst zeigt eine Seite 14 Läufe und die nächste 15.
- **Lauf:** `run/player.ts` simuliert den **aktuellen** Canvas-Graphen (über
  `lib/workflowSimulation.ts`), nicht ein aufgezeichnetes Skript — ein selbst gebauter Workflow
  läuft damit genauso. Jeder Schritt wird **erst in die Welt geschrieben, dann als Event
  zugestellt**: der Ops-Feed invalidiert bei jedem Event Query-Keys, und der Refetch trifft das
  Fake-Backend.
- **Lebenszyklus echt statt abgekürzt:** Lock deaktiviert den Workflow (atomar, wie im Produkt),
  und `useWorkflowExecution` verweigert den Lauf — Bearbeiten → Veröffentlichen → Ausführen ist
  deshalb die Kette, die der Smoke-Test abgeht.
- **Jede Mutation wird bedient oder benennt ihre Absage.** Ein Knopf, der weder wirkt noch sich
  erklärt, ist der Defekt — nicht die fehlende Funktion. Serverseitiges antwortet über
  `notInDemo(<Aktion>)` (`net/respond.ts`); der Fallback in `net/install.ts` ist die Notbremse, kein
  Entwurf. **Das gilt auch für Downloads:** `downloadFromApi` wirft, und ein Button, der sein
  Promise nicht behandelt, verwandelt die Absage in eine unbehandelte Rejection.
- **Der Smoke-Test speichert wirklich.** Routen abzulaufen findet nur Endpunkte, die beim Laden
  gerufen werden, und einen Editor zu öffnen beweist nichts über das Speichern — beide Lücken
  hatten genau dort ihren Ursprung. Deshalb: ein echtes Speichern je Seite plus gezielte Tests für
  Rollback, Verschieben, rekursives Löschen und die abgelehnten Downloads.
- **Admin-Settings werden bedient, nicht gesperrt.** `demo/handlers/settings.ts` liefert pro
  Sektion die produkteigenen Defaults (dieselben Literale, die die Karten als Fallback tragen),
  Schreiben antwortet 501. Das ist Pflicht, kein Komfort: `useSectionForm` ersetzt seinen
  Fallback durch `data.payload`, sobald eine Antwort kommt, und mehrere Karten lesen
  verschachtelt (`payload.proxy.password`) — ein Teil-Envelope crasht die Seite, statt sie
  abzuschwächen.
- **Antwortformate gegen den echten Consumer prüfen, nicht gegen sich selbst.** Die teuren Fehler
  waren allesamt Schlüssel-Differenzen, die ein Test gegen die eigene Handler-Form nie sieht:
  `{folderId}` statt `{targetFolderId}`, `id` statt `workflowId`/`executionId`, Arrays statt
  nach Step-ID indizierter Objekte, `{type,event}` statt `{type,evt}`. Die Tests fahren deshalb
  über `api/client.ts`, `api/paging.ts`, `api/sharedFolders.ts`, `api/adminSettings.ts` und den
  echten `useLiveOpsFeed`-Hook.
- **Der Smoke-Test war dreifach blind — jede Blindheit hat einen echten Crash durchgelassen:**
  1. `waitForLoadState('networkidle')` ist hier bedeutungslos. Der gepatchte `fetch` erreicht nie
     das Netz, Playwright sieht null Requests und meldet sofort „idle", während die Seite noch auf
     Daten wartet. Deshalb veröffentlicht `net/install.ts` die Zähler `__npDemoPending` und
     `__npDemoCompleted`; der Test wartet auf **Ruhe nach Aktivität** (auf „nichts in flight"
     allein zu warten ist bei t=0 trivial erfüllt, bevor die erste Anfrage überhaupt startet).
  2. Ein reiner Hash-Wechsel ist eine Same-Document-Navigation — React montiert die neue Route
     womöglich erst nach der Prüfung. Der Walk hängt deshalb pro Route eine eigene Query an und
     erzwingt damit einen echten Seitenaufbau.
  3. Ein 404-Bild protokolliert nichts; es rendert still seinen Alt-Text. Geprüft wird deshalb
     **jedes** `<img>` auf jeder Route, nicht das erste in der Sidebar.
- **Eine Error-Boundary macht einen Crash unsichtbar.** React reicht den Fehler an die Boundary
  statt ans Fenster, `pageerror` feuert nicht, und ein Routen-Durchlauf meldet „sauber". Der
  Smoke-Test prüft deshalb zusätzlich, dass keine Boundary gerendert ist — und geht die
  Settings-**Unterreiter** einzeln ab, weil der Crash hinter dem System-Tab lag. Gematcht wird auf
  den Seitentext, **nicht** auf `[role="alert"]`: die ErrorBoundary der App trägt diese Rolle, das
  Fehler-Element von React Router nicht — und genau darüber rutschte die abgestürzte
  Alerting-Seite als „sauber" durch.
- **Ein `<a href="/api/…">` geht nie durch den `fetch`-Patch.** Es ist eine Dokument-Navigation:
  unter einem Unterpfad landet der Besucher auf der 404 des Hosts, und die In-Memory-Welt ist beim
  Zurückkommen neu aufgebaut. `demo/net/anchors.ts` fängt deshalb jeden gleich-origin-Klick ab, der
  das Demo-Verzeichnis verlassen würde, holt ihn über den Patch und speichert eine Antwort mit
  `Content-Disposition` als Blob. Der Audit-Export ist der heute existierende Fall; der Guard deckt
  die Klasse. Der Smoke-Test lässt nur Anker nach `/api/` als „abfangbar" durchgehen und prüft am
  Verhalten, dass der Export nicht navigiert.
- **Unbekannte Schreibzugriffe antworten 501, nie 200.** Ein stiller Erfolg ließ das Löschen
  eines Benutzers gelingen, während sich nichts änderte.
- **Tests:** `npx vitest run src/__tests__/demo` und
  `npm run test:e2e:demo` (baut `dist-demo` und serviert es unter einem **Unterpfad** — an der
  Wurzel würden Basis-Pfad-Fehler nicht auffallen). Unbehandelte Endpoints loggen
  `[demo] unhandled …`; der Smoke-Test bricht auf jede solche Zeile.
- **Geführter Einstieg:** `?tour=file&lang=de|en` begleitet den nativen Startdialog und die
  Ausführungshistorie; `?tour=diagnose` öffnet einen vorbereiteten Kopierfehler.
  `demo/ui/tour.ts` bleibt außerhalb des React-Produktbaums. Der Graph kommt aus
  `scripts/example-guided-file-workflow.json`; `demo/run/fileScenario.ts` leitet seine
  simulierten Ausgaben aus den Startparametern ab. `Protected` scheitert bei File Copy,
  nachfolgende Steps laufen nicht. Retry behält die ursprünglichen Eingaben.
  Die geführten Szenarien sind in `e2e-demo/guided-tour.spec.ts` abgesichert.

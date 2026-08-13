# Plan — Alerting-Regel-Vorlagen als mitgelieferter Katalog

Detailplan zu **Posten 20** in [roadmap.md](roadmap.md). Nicht implementiert; dieses Dokument ist der
Stand der Entscheidung, nicht des Codes.

## Ausgangslage

In der Dev-Datenbank liegen 30 kuratierte Custom-Alerting-Regeln (`Kind=Custom`), über Monate
gewachsen. Ausgeliefert wird davon nichts: `scripts/seed-custom-alert-rules.ps1` kennt nur 17 davon,
POSTet gegen eine laufende Instanz, ist nicht idempotent (zweiter Lauf kracht in den UNIQUE-Index auf
`Name` und schluckt den Fehler) und verdrahtet eine private Mail-Adresse als Empfänger. Eine frische
Installation startet mit einer leeren Regel-Liste — jeder Operator baut dieselben Regeln von Hand nach.

Ziel: die Regeln als **Vorlagen-Katalog** mitliefern, aus dem sich einzeln oder gesammelt echte Regeln
anlegen lassen.

Entschieden:

- **Katalog statt Seeding.** Beim Boot wird nichts eingefügt; Bestandsinstallationen finden nach einem
  Update keine fremden Regeln in ihrer Liste. Dieselbe Linie fahren die System-Policies schon:
  `SystemAlertPreset` ist ausdrücklich *"Never auto-activated — a preset only pre-fills the editor"*.
  Und dieselbe wie bei den Starter-Presets für Workflows (Posten 17): Button statt First-Run-Seeding,
  die DB füllt sich nicht ungefragt.
- **30 Vorlagen.** Auch die drei englisch benannten Regeln kommen mit, neu betextet — sie tragen
  Abdeckung, die sonst keine Regel hat: `ExecutionRunningLong`, `cancelledBy=user`, ungefilterter
  Auffang für Fehlschläge.
- **Ohne Zustellweg.** Vorlagen bringen keine Route mit; der Empfänger entsteht beim Aktivieren.
- **Zweisprachig.** Name + Beschreibung je `de`/`en`; angelegt wird in der Sprache des Aufrufers.
- **Immer deaktiviert** angelegt.

**Gegen die Dev-DB verifiziert** (read-only): 30 `Kind=Custom`-Zeilen, alle `ScopeKind=Global`, alle
`IsEnabled=false`, alle `DedupKeyTemplate` leer, **keine** GUID-Referenzen in den Filtern (also keine
Folder-/Workflow-/Machine-Abhängigkeit), 22 mit Filter-AST, je genau eine Email-Route auf eine private
Adresse. Die Sammlung ist damit portabel; nur die Routen dürfen nicht mit.

---

## 0. Inhalte zuerst — der fachliche Kern wird vor dem Code reviewt

Der eigentliche Wert sind die 30 Regelkörper, nicht das Gerüst. Erster Schritt ist deshalb ein
reviewbares Inhalts-Artefakt, kein Code:

1. Read-only-Extraktion aus der Dev-DB:
   ```sql
   SELECT "Name","Description","EventTypes","FilterExpressionJson","CooldownMinutes",
          "MinOccurrences","OccurrenceWindowMinutes"
   FROM "NotificationRules" WHERE "Kind" = 'Custom' ORDER BY "Name";
   ```
2. Daraus die Katalog-JSON bauen: `filterExpressionJson`, `cooldownMinutes`, `minOccurrences`,
   `occurrenceWindowMinutes` **verbatim**; Namen/Beschreibungen DE geglättet und EN neu geschrieben;
   `templateId` als stabiler kebab-case-Schlüssel vergeben; `category` zugeordnet.
3. Diese Datei ist der Review-Gegenstand von PR 3 — allein, ohne Controller-Rauschen.

## 1. Herkunfts-ID an der Regel

Namensbasierte Erkennung ist nicht stabil: nach Umbenennen, Sprachwechsel oder einer späteren
Textkorrektur im Katalog würde dieselbe Vorlage erneut angeboten und angelegt.

- **Neue Spalte** `NotificationRule.SourceTemplateId` (`string?`, max 100), EF-Config in
  [NodePilotDbContext.cs](../src/NodePilot.Data/NodePilotDbContext.cs), plus Index
  `(SourceTemplateId)` für den Existenz-Check.
- **EF-Migration** `AddNotificationRuleSourceTemplateId` mit dem Pflicht-Postprocessing aus der
  Root-`CLAUDE.md`: alle `type:`-Annotations raus, `MigrationModelPortability.UseActiveProviderStoreTypes`
  als letzte Zeile in `BuildTargetModel` der Designer-Datei. `MigrationDriftTests` laufen lassen.
- **Nicht** `SystemPresetId` überladen — die Spalte ist dokumentiert System-only; eine zweite Bedeutung
  wäre genau die implizite Kopplung, die später niemand mehr auseinandernimmt.
- **Provenienz, kein Eingabefeld:** wie `Kind` wird `SourceTemplateId` beim Update **nicht** aus dem
  Request übernommen und bleibt erhalten. Über die normale Create-API ist sie immer `null`.
- **Backup/Restore:** Feld in `AlertingBackupPart.ExportAsync` aufnehmen und in
  `BackupRestoreService.ApplyRuleScalars` zurückschreiben, sonst verliert ein Restore die Herkunft.
  Round-Trip-Assertion in `BackupAlertingTests`.
- **Zwei getrennte Skip-Gründe** beim Anwenden: `alreadyApplied` (eine Regel mit dieser
  `SourceTemplateId` existiert) und `nameExists` (der Name ist belegt — durch eine fremde Custom-Regel
  oder eine System-Policy; der UNIQUE-Index ist global).

## 2. Katalog-Asset + Reader in Core

Muster: `ActivityConfigReference` — eingebettete JSON, statischer Ctor, `GetManifestResourceStream`,
Fehlermeldung nennt den fehlenden csproj-Eintrag.

- `src/NodePilot.Core/Alerting/Embedded/custom-alert-rule-templates.json` (neu)
- `<EmbeddedResource Include="Alerting\Embedded\custom-alert-rule-templates.json" />` in
  [NodePilot.Core.csproj](../src/NodePilot.Core/NodePilot.Core.csproj)
- `src/NodePilot.Core/Alerting/CustomAlertRuleTemplates.cs` (neu) — `All`, `Categories`,
  `SchemaVersion`, `TryGet(templateId)`

```json
{
  "schemaVersion": 1,
  "templates": [
    {
      "templateId": "timeout-failure",
      "category": "outcome",
      "name":        { "de": "Timeout-Fehler",  "en": "Timeout failure" },
      "description": { "de": "…",               "en": "…" },
      "eventTypes": ["ExecutionFailed"],
      "scopeKind": "Global",
      "filter": { "type": "comparison", "left": { "kind": "variable", "source": "event", "name": "errorMessage" },
                  "op": "matches", "right": { "kind": "literal", "value": "(?i)(timeout|timed out)" } },
      "cooldownMinutes": 10,
      "minOccurrences": 1,
      "occurrenceWindowMinutes": 0
    }
  ]
}
```

- `filter` steht als **verschachteltes Objekt**, nicht als escapter String — 22 escapte ASTs wären im
  PR nicht lesbar. Der Reader serialisiert kompakt zu `FilterExpressionJson`.
- Deserialisiert mit `UnmappedMemberHandling.Disallow`: ein versehentliches `"routes"` oder
  `"isEnabled"` im Asset scheitert beim Laden statt still ignoriert zu werden.
- `category` ist ein Schlüssel aus fester Allowlist (`outcome`, `lifecycle`, `performance`, `scope`,
  `auth`, `infrastructure`, `flap`), lokalisiert über `alerts:templates.categories.<key>`.

## 3. Routenlose Entwürfe — vollständig, inkl. der drei Folgestellen

Die System-Seite hat die Frage bereits beantwortet: Routen sind nur zum *Aktivieren* Pflicht
([SystemAlertingController.cs:292-295](../src/NodePilot.Api/Controllers/SystemAlertingController.cs#L292-L295),
Enable-Gate bei [:135-136](../src/NodePilot.Api/Controllers/SystemAlertingController.cs#L135-L136)).

| Stelle | Änderung |
|---|---|
| `TryBuildDraft` [:384-386](../src/NodePilot.Api/Controllers/AlertingController.cs#L384-L386) | Routen-Pflicht nur noch bei `isEnabled == true`; Schleife über `routes ?? []` |
| `Enable` [:196-206](../src/NodePilot.Api/Controllers/AlertingController.cs#L196-L206) | 400 bei `rule.Routes.Count == 0`, Wortlaut analog System-Seite |
| `PreviewRule` [:299-325](../src/NodePilot.Api/Controllers/AlertingController.cs#L299-L325) | ruft `TryBuildDraft` mit hartem `isEnabled: true` — auf `false` ändern (Preview persistiert nie, „enabled" ist dort bedeutungslos), **und** die Begründung absichern: `routeResults.All(r => !r.Matches)` ist bei leerer Liste `true` und meldete sonst ein erfundenes „no route condition matched" |
| `TestFire` [:277](../src/NodePilot.Api/Controllers/AlertingController.cs#L277) | `results.All(r => r.Success)` ist über der leeren Liste `true` → **meldet Erfolg, ohne etwas gesendet zu haben**. 400-Gate bei 0 Routen, vor dem Ledger-Schreiben |

`preview-filter` ist **kein** Call-Site von `TryBuildDraft` und damit nicht betroffen.

**Clients, deren Validierung sonst strenger bliebe als der Server:**

- [AlertingRuleEditor.tsx:217](../src/nodepilot-ui/src/components/alerting/AlertingRuleEditor.tsx#L217) —
  `form.routes.length === 0` gilt unbedingt als invalid. Aufteilen: leere Routenliste blockiert nur bei
  `form.isEnabled`; eine halb getippte Route (`!r.target.trim()`) bleibt in beiden Fällen invalid.
  Dazu ein Hinweis unter der Enabled-Checkbox, analog `alerts:system.editor.enabledHint`.
- [AlertingPage.tsx:47](../src/nodepilot-ui/src/pages/AlertingPage.tsx#L47) — `toggleMutation` hat
  **kein** `onError`; ein 400 vom Enable-Gate würde spurlos verpuffen. Zwei Dinge: bei
  `routes.length === 0` gar nicht erst mutieren, sondern den Editor öffnen (der Operator soll den
  Empfänger eintragen, nicht eine Fehlermeldung lesen), **und** ein `onError`-Toast als Auffangnetz —
  ein stumm scheiternder Toggle ist der eigentliche Bug.
- [AlertingCommands.cs](../src/NodePilot.Cli/Commands/Alerting/AlertingCommands.cs) — „mindestens ein
  `--email`/`--webhook`" nur noch prüfen, wenn die Regel aktiviert angelegt wird.

**Kein Pfad führt zu „aktiv ohne Route":** Update mit `isEnabled=true` + leeren Routen bleibt 400, und
disable → Routen leeren → enable läuft ins Enable-Gate. Der Dispatcher ist ohnehin robust — er
`continue`t bei leerer Routenmenge **vor** dem Suppression-State, es entsteht also kein State-Drift.
Das wird als Invariante dokumentiert, nicht zusätzlich abgesichert.

## 4. API + Batch-Vertrag

In [AlertingController.cs](../src/NodePilot.Api/Controllers/AlertingController.cs), DTOs in
[AlertingDtos.cs](../src/NodePilot.Api/Dtos/AlertingDtos.cs).

**`GET /api/alerting/rule-templates`** (Admin + Operator) — liefert `schemaVersion`, `categories` und
je Vorlage **beide** Sprachvarianten plus `existingRuleId` (aus `SourceTemplateId`, ersatzweise
Namensgleichheit case-insensitiv über alle `Kind`) und `nameTaken`. Kein `lang`-Parameter: der Client
hat beide Texte und wählt selbst.

**`POST /api/alerting/rule-templates/apply`** (Admin-only) — Body `{ templateIds: [...], language: "de" }`.

Vertrag, explizit **validate-then-mutate**:

1. **Vor jeder Mutation** wird vollständig geprüft: `language` ∈ {`de`,`en`} sonst 400; `templateIds`
   nicht leer sonst 400 (leer bedeutet **nicht** „alle" — ein versehentlich leeres Array, das 30 Regeln
   anlegt, ist die falsche Fehlerrichtung); doppelte Ids im Request → 400 statt stiller Dedup, weil ein
   Client, der doppelt schickt, einen Fehler hat; unbekannte `templateId` → 400 mit Nennung der Id.
2. **Danach** wird angelegt. Übersprungen wird nur, was sich aus dem Datenbestand ergibt:
   `alreadyApplied` (`SourceTemplateId` vorhanden) und `nameExists`. Antwort immer `200` mit
   `{ created, applied: [{templateId, ruleId, name}], skipped: [{templateId, reason}] }` — Vorbild
   `ImportWorkflowsResponse`.
3. **Bewusst kein atomarer Batch.** Statische Fehler sind durch Schritt 1 ausgeschlossen; was übrig
   bleibt, ist die konkurrierende Namenskollision. 29 gute Regeln zurückzurollen, weil ein Name
   zwischenzeitlich belegt wurde, wäre das schlechtere Ergebnis. Das steht so in der Doku und im Test.
4. **Tracker-Falle:** `NotificationRuleStore.CreateAsync` speichert
   [pro Regel einzeln](../src/NodePilot.Data/NotificationRuleStore.cs#L78). Ein `DbUpdateException` aus
   dem UNIQUE-Index vergiftet sonst den `DbContext` für den Rest der Schleife. Jeder Create bekommt
   `try/catch (DbUpdateException)` → `skipped(nameExists)` **plus** `Entry(draft).State = Detached`,
   bevor es weitergeht. Ohne das Detach reißt eine Kollision den ganzen Batch in einen 500.
5. Angelegt wird über denselben `TryBuildDraft`-Pfad wie beim manuellen Create, mit
   `isEnabled: false`, `routes: []`, `targets: []` — kein zweiter Validierungspfad.
6. **Audit:** eine Sammelzeile mit dem bestehenden `AuditActions.AlertRuleCreated`, Details
   `("source","template")`, `("language",…)`, `("created",…)`, `("skipped",…)`, `("templateIds",…)`,
   `("ruleIds",…)`. **Kein neuer Audit-Code** → `AuditActionsCatalogTests` und
   [claude-reference.md](claude-reference.md) bleiben unberührt.

## 5. Frontend

- [api/alerting.ts](../src/nodepilot-ui/src/api/alerting.ts): Typen + `ruleTemplates()`,
  `applyRuleTemplates(body)`.
- **Neu** `src/nodepilot-ui/src/components/alerting/AlertingTemplatesDialog.tsx`: `ModalShell`, nach
  `category` gruppiert, Gruppen- und Global-Select-all (`indeterminate` per ref), Suche über den
  lokalisierten Text, Chips für Event-Typen/Cooldown/Flap, Fußzeile mit Auswahl-Anzahl. Bereits
  angewandte oder namentlich belegte Zeilen sind deaktiviert, mit unterscheidbarem Hinweis, und werden
  von Select-all übersprungen. Kopfzeile trägt den Hinweis „deaktiviert und ohne Empfänger angelegt".
- [AlertingPage.tsx](../src/nodepilot-ui/src/pages/AlertingPage.tsx): Button „Vorlagen" in der
  Header-Actionzeile, nur `canAdmin && tab === 'custom'`; zusätzlich prominenter Einstieg im
  Leerzustand — das ist der Moment, für den das Feature gebaut wird.
- Nach Apply **beide** Query-Keys invalidieren: `['alerting-rules']` **und**
  `['alerting-rule-templates']` — sonst bietet der Dialog beim erneuten Öffnen gerade angelegte
  Vorlagen weiter als verfügbar an.
- i18n `alerts:templates.*` in DE **und** EN, inkl. `categories.*` und dem neuen
  `enabledNeedsRouteHint` aus Abschnitt 3.
- Stil: Modal sitzt auf `surface-lowest` → **Umriss-Kette**, nicht `.input-field`. Nur Checkboxen, kein
  natives `<select>`. Farben ausschließlich über Tokens.

## 6. CLI + MCP

- CLI: DTOs in `src/NodePilot.Cli/Api/Dtos/AlertingDtos.cs`, zwei Client-Methoden, Commands in
  [AlertingCommands.cs](../src/NodePilot.Cli/Commands/Alerting/AlertingCommands.cs), Renderer,
  Registrierung in [CommandRegistration.cs](../src/NodePilot.Cli/CommandRegistration.cs):
  `np alerting templates` und `np alerting apply-templates (--id X … | --all) --lang de|en`.
  `--all` nimmt alle noch nicht angewandten; weder `--id` noch `--all` → Fehler mit Exit ≠ 0.
- MCP: DTOs + Client-Methoden + `list_alerting_rule_templates` (`ReadOnly`) und
  `apply_alerting_rule_templates` in der bestehenden
  [AlertingTools.cs](../src/NodePilot.Mcp/Tools/AlertingTools.cs) — keine neue Tool-Klasse, also
  **kein** `Program.cs`-Eingriff.

## 7. Tests

**Katalog-Integrität** — `tests/NodePilot.Engine.Tests/Notifications/CustomAlertRuleTemplateCatalogTests.cs`
(Engine.Tests, weil dort schon `ActivityConfigReferenceTests` liegt und `ConditionEvaluator` +
`NotificationRuleSemantics` erreichbar sind):

- Asset lädt, `schemaVersion` aktuell; `templateId` eindeutig **und** kebab-case; `category` aus der
  Allowlist
- Namen über **beide** Sprachen hinweg case-insensitiv eindeutig (der UNIQUE-Index ist global — ein
  DE-Name, der mit dem EN-Namen einer anderen Vorlage kollidiert, bricht die bilinguale Installation)
- Texte nicht leer, in beiden Sprachen vorhanden, `Name` ≤ 100 und `Description` ≤ 500 (die
  DB-Längenlimits)
- `eventTypes` nicht leer und ∈ `NotificationRuleSemantics.SupportedEventTypes`; `scopeKind` überall
  `Global`
- Throttle-Kombinationen gültig: `cooldownMinutes`/`occurrenceWindowMinutes` ∈ [0, 43200],
  `minOccurrences ≥ 1`, und `minOccurrences > 1 ⇒ occurrenceWindowMinutes > 0`
- **Jedes Filter-Feld** `{kind:variable, source:event, name:X}` ∈ `NotificationContext.ToFieldMap().Keys`
  — ein getipptes `folderpath` ergäbe eine Regel, die korrekt aussieht und nie greift
- **Positiv- und Negativ-Fixture je gefilterter Vorlage.** „Parst und wirft nicht" ist zu schwach: der
  `ConditionEvaluator` schluckt unbekannte Strukturen teilweise als `true`, und ein nicht
  kompilierbares Regex wird in `GetCachedRegex` verschluckt → ein Alarm, der nie feuert. Je Vorlage ein
  `NotificationContext`, der matchen **muss**, und einer, der **nicht** matchen darf
- Jeder `SupportedEventTypes`-Wert kommt in mindestens einer Vorlage vor (fängt ab, dass die Kuratierung
  eine Event-Art ganz verliert)

**API** — `tests/NodePilot.Api.Tests/Controllers/AlertingTemplatesControllerTests.cs` (neu):
Katalog liefert beide Sprachen + `existingRuleId`; Apply legt deaktiviert und routenlos an; Sprache
wirkt auf Name/Beschreibung; unbekannte Id → 400 **ohne** jede Regel anzulegen; doppelte Ids → 400;
leere Liste → 400; unbekannte Sprache → 400; `alreadyApplied` nach zweitem Lauf (auch nach Umbenennen
der Regel!); `nameExists` überspringt und legt den Rest an; alle 30 anwenden → 30 Regeln, alle
`IsEnabled=false`, alle `Routes.Count == 0`; Sammel-Audit mit `source=template`.

**Rollen** — der 403-Fall gehört in `tests/NodePilot.Api.Tests/Hosting/RoleMatrixSmokeTests.cs`: ein
direkter Controller-Test führt `[Authorize]` nicht aus und würde die Absicherung nur behaupten.

**Routenlose Semantik** — in `AlertingControllerTests.cs`: create/update deaktiviert mit 0 Routen → OK;
aktiviert mit 0 Routen → 400; `enable` auf routenlose Regel → 400; `test-fire` auf routenlose Regel →
400 (statt falschem Erfolg); `preview-rule` ohne Routen → OK und **ohne** „no route condition matched".

**Migration/Backup** — `MigrationDriftTests` grün; `BackupAlertingTests` um den
`SourceTemplateId`-Round-Trip erweitern.

**CLI** — `tests/NodePilot.Cli.Tests/Commands/CommandIntegrationAlertingTests.cs`: `templates`,
`apply-templates --id`, `--all`, keins von beidem → Fehler-Exit.

**MCP** — WireMock-Tests in `tests/NodePilot.Mcp.Tests/Tools/AlertingToolsTests.cs`; laut
[MCP-Konvention](../src/NodePilot.Mcp/CLAUDE.md) Pflicht, nicht optional.

**Frontend** — `__tests__/components/AlertingTemplatesDialog.test.tsx` (Gruppierung, deaktivierte
Zeilen, Select-all überspringt sie, Apply schickt Ids + Sprache, beide Keys invalidiert, Fehlerpfad) und
in `__tests__/pages/AlertingPage.test.tsx`: Button nur für Admin im Custom-Tab; Toggle auf routenloser
Regel öffnet den Editor statt zu mutieren.

**E2E** — Ergänzung in `src/nodepilot-ui/e2e/alerting.spec.ts` (kein neuer Spec): Custom-Tab →
Vorlagen → bereits angewandte Zeile ist deaktiviert → alle auswählen → Anwenden → Liste zeigt die neuen
Regeln deaktiviert und ohne Route.

## 8. Doku-Sync

- [alerting.md](alerting.md) — Abschnitt „Mitgelieferte Vorlagen": Katalog statt Seeding, routenlos,
  immer deaktiviert, zweisprachig, Herkunfts-ID; zwei REST-Zeilen; zwei `np alerting`-Zeilen; zwei
  MCP-Tools; Governance-Punkt korrigieren (Routen sind zum **Aktivieren** Pflicht, nicht zum Anlegen);
  ein Satz zur Dispatcher-Invariante.
- `src/nodepilot-docs-ui/content/alerting.md` — dieselbe Aussage im kuratierten Ton der Website.
- **Tool-Zahl 99 → 101 an allen Stellen**, sonst rot in `DocumentationCountsTests`: Root-`CLAUDE.md`
  (zwei Behauptungen), `README.md`, [mcp-server.md](mcp-server.md) (drei Zahlen),
  `src/NodePilot.Mcp/CLAUDE.md`, `src/nodepilot-docs-ui/content/mcp-server.md`. Dazu
  `src/nodepilot-docs-ui/content/cli.md` und `src/NodePilot.Cli/CLAUDE.md` für die neuen Befehle.
- `E2ETests.md` + `src/nodepilot-ui/e2e/README.md` — neuer Fall in Teil 78.
- **`scripts/seed-custom-alert-rules.ps1` löschen** — vom Katalog vollständig ersetzt, von keiner Doku
  referenziert. Zwei divergierende Quellen für dieselben Regeln wären die eigentliche Schuld.
  `seed-system-alert-policies.ps1` bleibt.

## 9. PR-Schnitt (5, entspricht dem Budget)

1. **`fix/alerting-routeless-drafts`** — Abschnitt 3 komplett (Server + Editor + Toggle-UX +
   test-fire-Gate) samt Tests und der Governance-Zeile in `alerting.md`. Eigenständig sinnvoll,
   Voraussetzung für alles Weitere.
2. **`feat/alerting-rule-provenance`** — Abschnitt 1: Spalte, Migration, Backup-Round-Trip, Tests.
3. **`feat/alerting-rule-template-catalog`** — Abschnitt 2 + die Guard-Tests aus Abschnitt 7 + Löschen
   des Seed-Skripts. **Ohne API-Fläche**, damit der Review genau die Frage stellen kann, die zählt:
   sind diese 30 Regeln richtig?
4. **`feat/alerting-rule-template-api`** — Abschnitt 4 + API-/Rollen-Tests.
5. **`feat/alerting-rule-templates-clients`** — Abschnitte 5, 6 und der Doku-Sync aus 8 (inkl. aller
   Tool-Zahlen; die Zähl-Guards müssen in einem Commit wandern).

## Verifikation

- `dotnet test tests/NodePilot.Engine.Tests --filter "FullyQualifiedName~CustomAlertRuleTemplate"` —
  der Guard, der einen kaputten Filter, ein falsches Event-Feld oder einen fehlenden EN-Text auffliegen
  lässt.
- `dotnet test tests/NodePilot.Api.Tests --filter "FullyQualifiedName~Alerting"` + `~MigrationDrift`
  + `~RoleMatrix` + `~BackupAlerting`.
- `dotnet test tests/NodePilot.Cli.Tests tests/NodePilot.Mcp.Tests`.
- In `src/nodepilot-ui`: `npx vitest run src/__tests__/components/AlertingTemplatesDialog.test.tsx
  src/__tests__/pages/AlertingPage.test.tsx` und `npx playwright test e2e/alerting.spec.ts`.
- Ende-zu-Ende gegen die Dev-Instanz: `/alerts` → Custom → Vorlagen → alle anwenden → 30 deaktivierte,
  routenlose Regeln; Dialog erneut öffnen → alle als „bereits angewandt" markiert; eine Regel
  umbenennen, Dialog erneut → **weiterhin** „bereits angewandt" (das ist der Test der Herkunfts-ID);
  Toggle auf einer routenlosen Regel → Editor öffnet sich; Route eintragen → Aktivieren geht durch;
  test-fire ohne Route → 400 statt falschem Erfolg; `np alerting templates` und der MCP-Tool-Aufruf
  liefern dieselbe Liste.

## Offene Punkte

- **Die 30 EN-Texte müssen geschrieben werden** (DE liegt vor). Zusammen mit der Kuratierung ist das
  der eigentliche inhaltliche Aufwand — und der Grund, warum er in PR 3 allein steht.
- Zwei Sprachvarianten derselben Vorlage können nicht nebeneinander existieren: `alreadyApplied` greift
  über die Herkunfts-ID, unabhängig von der Sprache. Das ist gewollt.
- `SourceTemplateId` bleibt bei manuell angelegten Regeln `null`; die Spalte ist reine Provenienz und
  taucht in keiner Editor-Maske auf.

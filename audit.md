# NodePilot Repository- und POC-Audit

- Stand der Prüfung: 26. August 2026
- Geprüfter Commit: `fa1a760` auf `chore/comment-cleanup-pass` plus bereits vorhandener Working Tree mit 243 geänderten Einträgen
- Prüfmodus: read-only; während des Audits wurden keine Repository-Dateien verändert
- Nachtrag: Architektur-Audit vom 5. September 2026 mit Umsetzung am 6. September 2026 (PR #305
  und #306) — eigener Abschnitt vor dem Executive Summary

## Umsetzungsnachtrag zu Priority 1 (26. August 2026)

Der Bericht darunter bleibt als Audit-Baseline erhalten. Im anschließenden Fixlauf wurden die
angeforderten Punkte 1–4 wie folgt bearbeitet:

1. **Recursive Folder Delete: behoben.** `recursive=true` ist serverseitig global Admin-only;
   Folder-`Edit` reicht weiterhin nur zum Löschen eines leeren Ordners. API-Capability, UI und CLI
   unterscheiden Delete jetzt von Edit.
2. **Produktionslogging: im Produkt und lokal verifiziert.** Der NodePilot-Default ist
   `Information`, während ASP.NET- und EF-Kategorien auf `Warning` bleiben. Ein automatisierter
   Smoke-Test schreibt ein Information-Audit-Event durch Serilog in ECS-NDJSON und parst die
   SIEM-Felder erfolgreich. Der letzte Transport in ein konkretes Kunden-SIEM muss im POC mit dem
   dort eingesetzten Agenten und Index separat nachgewiesen werden.
3. **Restart-/Trigger-/Idempotency-Vertrag: festgelegt und getestet.** Die aktuelle Garantie ist
   ausdrücklich *at-most-once automatic dispatch*, nicht durable Replay: nie gestartete Pending-
   Ausführungen werden mit eigenem Recovery-Marker abgebrochen und nur deren Idempotency-
   Reservation wird freigegeben; Running/Paused bleiben wegen möglicher externer Seiteneffekte
   reserviert. Trigger haben weiterhin kein Catch-up. Die damals noch offene Durable Outbox wurde
   im nachfolgenden Priority-2-Fixlauf umgesetzt.
4. **Unsichere Retries: behoben.** Redigierte, abgeschnittene oder ungültige Input-Snapshots werden
   mit `execution_inputs_not_replayable` abgelehnt und nicht dispatched.
5. **Konditionale Fan-ins: behoben.** Mehrere unterschiedliche Vorgänger dürfen nur noch in eine
   explizite `junction` münden. Der Designer bietet beim zweiten Eingang automatisch eine
   `waitAll`-Junction an und verdrahtet die bestehenden Eingänge um; Save/Publish/API und Linter
   erzwingen denselben Vertrag. SCOrch-Importe erhalten die Junction automatisch. Die Engine wertet
   Junction-Conditions deterministisch über alle relevanten Eingangskanten aus.

Gezielte Verifikation des Fixlaufs: API 137/137, Engine/Scheduler 21/21, UI-Unit 53/53,
Playwright 16 bestanden/1 bewusst übersprungen, TypeScript- und Vite-Produktionsbuild erfolgreich.

Zusätzliche Verifikation für Punkt 5: Engine/Workflow/Definition 176/176 sowie
SCOrch/Analyzer/Scheduler 95/95, API 125/125, AI-Prompt 61/61, MCP 41/41 und fokussierte
Editor-UI 155/155 bestanden; TypeScript- und Vite-Produktionsbuild erfolgreich.

## Umsetzungsnachtrag zu Priority 2 (26.–27. August 2026)

Die angeforderten Punkte 1, 3, 5 und 6 wurden umgesetzt:

1. **Durable Outbox und Execution-Lifecycle:** `Pending Execution`, Idempotency-Reservation und
   geschützter Dispatch Intent werden atomar persistiert. Ein geleaster DB-Worker setzt Pending-
   Arbeit nach Restart/Failover fort; zentrale CAS-Transitionen schützen Claim, Terminalisierung
   und direkte Cancellation. Verliert ein Node zwischen Outbox-Lease und Engine-Claim die Führung
   oder scheitert die DI-Scope-Erzeugung vor Engine-Ownership, bleibt der Intent für einen sicheren
   Retry erhalten. Bereits gestartete Arbeit wird weiterhin nie automatisch wiederholt.
3. **Queue-/Capacity-/Cancellation-Fehler:** Die produktive In-Memory-Queue wurde durch den
   Outbox-Worker ersetzt. Priorität ist DB-seitig, Retry vor Engine-Ownership blockiert keinen
   Worker-Slot, Capacity-Fehler hinterlassen keinen Running-Ghost und Cancel verliert keine Race
   gegen einen bereits terminalen Zustand. Die funktionslose In-Memory-Capacity-Einstellung wurde
   aus Konfiguration, Settings-API, UI, Sizing und Deployment-Templates entfernt.
5. **Pagination:** `GET /api/executions` liefert echte Seiten mit DB-basiertem `Total` und stabilem
   Sort-Tiebreaker. SPA, CLI und MCP lesen Folgeseiten und schneiden die Historie nicht mehr still
   nach 500 Zeilen ab.
6. **Verträge:** Capability- und Settings-Verträge bleiben zentral geprüft; die CLI-/MCP-DTO-
   Paritätsprüfung hat keine Known-Gap-Ausnahmen mehr. Folder-/Capability-, OIDC-, Break-glass-,
   Authority- und Dashboardfelder sind in den Clients vollständig gespiegelt und schreibbare
   Felder sind über CLI/MCP erreichbar.

Abschlussverifikation am 27. August 2026: Solution-Build ohne Fehler; API-Fokus 111/111,
Engine/Scheduler-Fokus 97/97, CLI-Contract/Client 54/54, MCP-Fokus 2/2 und UI-Fokus 110/110
bestanden; TypeScript- und Vite-Produktionsbuild erfolgreich. Der Solution-Build meldet weiterhin
1.253 bereits vorhandene Warnungen, aber keine neuen Buildfehler.

## Weiterer Umsetzungsnachtrag zu Priority 1 (27. August 2026)

Die anschließend angeforderten offenen Punkte 3, 5, 6 und 7 wurden umgesetzt:

3. **SCOrch-Dateien bleiben bytegenau:** UI und CLI übertragen die Originalbytes inklusive BOM;
   das MCP-Tool nimmt dieselben Bytes als Base64 entgegen. UTF-16LE-/UTF-16BE-Exporte werden nicht
   mehr vor dem Server in UTF-8 umcodiert.
5. **Native Imports sind immer deaktiviert:** `IsEnabled` aus einer Exportdatei wird im Ziel nicht
   mehr übernommen. Jeder importierte Workflow benötigt nach Ziel-, Credential- und
   Side-effect-Prüfung eine bewusste Aktivierung. Beide Import-APIs liefern dafür die erzeugten IDs;
   `POST /api/workflows/{id}/enable` und `np workflow enable <id>` schalten gezielt scharf. CLI-
   Importe geben den vollständigen ID-Report mit `-o json` CI-tauglich auf stdout aus.
6. **Triggerzustellung ist persistent und dedupliziert:** Schedule-, FileWatcher-, Database- und
   EventLog-Quellen persistieren Cursor. Receipt, Cursor, Pending Execution und Dispatch-Outbox
   werden gemeinsam committed; DB-Ausfall und Leadership-Wechsel führen zu Retry/Reconciliation
   statt stillem Verwerfen. Cron-Misfires und erhaltene EventLog-Einträge werden nachgeholt,
   FileWatcher rekonstruiert bleibende Zustandsänderungen aus einem persistenten Snapshot. Ein
   EventKey erzeugt höchstens eine persistierte Execution. Physisch nicht rekonstruierbare
   Zwischenereignisse einer Polling-/Watcher-Quelle benötigen weiterhin eine externe Queue oder
   ein Journal; diese Grenze ist jetzt ausdrücklich dokumentiert.
   **Überholt seit 28. August 2026 (#265):** Das Nachholen nach Neustart oder Failover wurde
   bewusst abgeschafft. Jede Quelle spult ihren Cursor beim Start ohne Feuern vor und meldet das
   übersprungene Fenster (Log-Zeile plus `nodepilot.scheduler.triggers.fires_skipped`);
   Deduplizierung und Retry im laufenden Betrieb bleiben. Maßgeblich ist `CLAUDE.md`, Abschnitt
   „Trigger".
7. **Backup vermittelt keine Vollsicherheit mehr:** Schema v4 verschlüsselt und authentifiziert den
   kompletten Payload statt nur einzelner Secret-Felder. Exportwarnungen verhindern die Ausgabe
   eines unvollständigen Archivs; Restorewarnungen rollen die gesamte Wiederherstellung zurück.
   Preview benötigt die Passphrase. UI und Dokumentation kennzeichnen `.npbackup` ausdrücklich als
   Konfigurationsportabilität und verlangen für echtes DR zusätzlich native DB-, `ProgramData`-,
   Konfigurations- und Key-Sicherung samt Restore-Drill.

Abschlussverifikation: Solution-Release-Build ohne Fehler; Engine/Trigger 223/223,
Data/Migration 33/33, API Backup/Import 115/115, CLI 120/120, MCP 11/11 und UI
Backup/Import 89/89 bestanden; TypeScript- und Vite-Produktionsbuild erfolgreich.

## Architektur-Audit vom 5. September 2026

- Stand der Prüfung: 5. September 2026, Arbeitsbaum einschließlich lokaler Änderungen; read-only,
  keine Builds, Tests oder Deployments durch den Prüfer
- Gesamtbewertung des Prüfers: **7/10**; vier Befunde mit hohem Impact (weiterlaufende Activities
  nach einem Engine-Fehler, konkurrierend erzeugbare Ordnerzyklen, zusammengeführte Workflows beim
  Restore, unredigierte Fehlertexte in Logs und Traces)
- Jeder High- und Medium-Befund wurde vor der Umsetzung am Code gegengeprüft; das Ergebnis steht
  bei den Posten unten

| Dimension | Wert | Begründung des Prüfers |
|---|---:|---|
| Projektstruktur | 8/10 | Klare Projekte und überprüfter Abhängigkeitsgraph; innerhalb großer Module verbleiben Verantwortungsschwerpunkte. |
| Codequalität | 7/10 | Aussagekräftige Fachbegriffe und defensive Prüfungen; konkrete Duplikate, Fehlerbehandlungs- und Ressourcenprobleme. |
| Architektur | 8/10 | Kohärenter modularer Monolith; komplexer Ausführungskern mit teilweise statischem Zustand. |
| Robustheit | 6/10 | Gute Schutzmechanismen für Normalbetrieb und DB-Ausfälle, aber vier kritische Randfälle (im Nachtrag umgesetzt). |
| Testarchitektur | 7/10 | Umfangreiche Tests und CI-Gates; reale Produktionsprovider und vollständige Browser→API-Verträge bleiben unzureichend abgesichert (deckt sich mit 4.15). |
| Wartbarkeit | 7/10 | Zentrale Konfiguration und Dokumentation; mehrfach implementierte Sicherheitslogik und große Orchestrierungsklassen. |

Als Stärken hielt der Prüfer fest: die Projektgrenzen entsprechen dem in `DependencyDirectionTests`
erzwungenen Graphen (Abschnitt 2), CLI und MCP greifen ausschließlich über HTTP auf das Backend zu;
`StepRunner` schreibt den Step-Start vor der externen Ausführung und den terminalen Zustand vor der
Ergebnisweitergabe dauerhaft, die Engine beansprucht wartende Ausführungen über bedingte Updates;
Autorisierung ist mehrstufig (JWT-Validierung, `TokenValidityMiddleware` mit Session, Widerruf,
SecurityStamp und Rolle, Folder-Grants unter der globalen Rolle, CSRF auf mutierenden
Cookie-Anfragen); der Frontend-Zustand ist gegen veraltete Antworten geschützt (Save-Snapshots,
Verwerfen nach Benutzerwechsel, Bereinigung an der Auth-Grenze);
`RuntimeOverridesWriter.TryUpdateSectionAtomic` kombiniert Mutex, ETag und Dateiaustausch; CI
erzwingt das Coverage-Gate von 85 % Zeilen / 70 % Branches und die Deploy-Skripte prüfen Signatur
und Manifest vor dem Austausch.

### Umsetzungsnachtrag (6. September 2026, PR #305 und #306)

Alle High-Befunde und die verifizierten Medium-Befunde mit Code-Bezug sind umgesetzt:

1. **H1 – Activities liefen nach einem Engine-Fehler weiter: behoben.** `WorkflowScheduler.RunAsync`
   fing nur den Junction-Race; jede andere Exception — praktisch ein Persistenzfehler beim Step, der
   kein bestätigter Ausfall ist, etwa Command-Timeout oder Deadlock — verließ die Schleife, die
   Engine finalisierte den Lauf und gab Slot und Concurrency-Gate frei, während Geschwister-Steps
   als Waisen weiterliefen und ihre Zeilen in eine terminale Execution schrieben, die niemand mehr
   canceln konnte. Der Scheduler cancelt und awaitet jetzt jeden gestarteten Step, bevor die
   Exception propagiert; der Debug-Pause-Guard hängt am Step-Token und blockiert das nicht.
2. **H2 – Ordnerzyklen durch konkurrierende Moves: behoben.** Prozessweiter
   `FolderTreeMutationLock` je Baum (ausreichend, weil mutierende Requests im Cluster leader-gated
   sind) plus Besuchsmengen in `RecomputePathsRecursive` und `DescendantMaxDepth` beider Bäume. Die
   Eintrittswahrscheinlichkeit war im Bericht überzeichnet — zwei Edit-berechtigte Benutzer im
   selben DB-Roundtrip-Fenster —, die Folge aber real (Endlosschleife mit wachsenden Path-Strings,
   Teilbaum verschwindet aus der Baumansicht). Authz-Ahnenkette und `IsDescendant` waren bereits
   tiefen-gebunden, ein API-weiter Hang trat nicht ein.
3. **H3 – Restore behandelte den Workflow-Namen als Identität: behoben.** `Workflow.Name` hat
   keinen Unique-Index, gleichnamige Workflows sind ein unterstützter Zustand
   (`WorkflowNameResolver` liefert bei Mehrdeutigkeit 409). Der zweite „Deploy" wurde bei `skip`
   verworfen, bei `overwrite` überschrieb er den ersten, und seine Source-Id wurde auf den ersten
   gemappt — Referenzen wurden still umgebogen. Ein Backup-Workflow matcht eine Zielzeile jetzt
   zuerst per Id, dann per Name im Zielordner; Backup-Zeilen matchen einander nie, die Preview zählt
   nach derselben Regel, `overwrite` stellt bei Id-Match auch den Namen her. ADR 0001 ergänzt.
4. **H4 – Fehlertexte umgingen die Redaktion: behoben.** Neben `WinRmSession` (roher
   PowerShell-Fehlerstrom als Span-Status) und `WorkflowEngine.CompleteAsFailedAsync` (rohe
   Exception in Log und Trace) trug auch der Activity-Span in `StepRunner` den unredigierten
   Step-Fehler — das hatte der Bericht übersehen. Remote-, Activity- und Engine-Spans nennen jetzt
   Fehlerklasse und Exception-Typ; Engine-Log und Root-Span tragen die redigierte Nachricht. Wirkt
   nur mit `OpenTelemetry:Enabled=true` (default `false`), der Log-Pfad immer.
5. **M3 – Credential-Rotation und WinRM-Pool: behoben**, Roadmap-R2-Posten vorgezogen. Der
   Pool-Key trägt einen Fingerprint aus Benutzername, Domäne und gespeichertem Passwort; Sessions
   unter dem alten Key laufen per TTL aus, ausgeliehene beenden ihren Step unter der Identität, mit
   der sie geöffnet wurden. Die Prüfung verschärfte den Befund: die Idle-TTL startete bei jeder
   Wiederverwendung neu, unter Dauerlast lief eine alte Identität unbegrenzt weiter, und auch
   Benutzername- oder Domänenänderungen blieben unbeachtet.
6. **M4 – SSE-Größenprüfung kam zu spät: behoben.** Der Stream läuft durch den vorhandenen
   `LengthLimitedStream`. Die Exposition war enger als beschrieben: durch das 90-s-Budget begrenzt,
   nur vom admin-konfigurierten HTTPS-Endpunkt erreichbar, `llmQuery` streamt nicht.
7. **M5/M6 – Desktop: behoben.** Der Restart-Befehl parste nie (per PowerShell-Parser bestätigt:
   ein Fehler vorher, keiner nachher); er liegt jetzt quotefrei in `backendRestart.ts`, ein
   Fehlschlag wird gemeldet, jede Readiness-Anfrage hat eine Frist (`http.ts`), beides mit Tests.
8. **M7 – CLI/MCP duplizierten Session-Rotation und Token-Speicherung: behoben.** `TokenStore`,
   `StoredSession` und `TokenRefreshHandler` liegen einmal in `NodePilot.Core.Clients` (Core trägt
   dafür die `ProtectedData`-Paketreferenz); `CliSessionInteropTests` verhindert eine Rückkehr der
   Kopien, `EndpointClientCoverageTests` zählt das gemeinsame Verzeichnis als Call-Site beider
   Clients.
9. **TD1 – Binär-Rollback stellte das Schema nicht zurück: behoben, soweit sinnvoll.** Der Updater
   probt `/healthz/ready` nach dem Rollback und warnt statt „Rollback complete" zu melden; Skript,
   `deploy/README.md` und Doku-Website sagen, dass ein Binär-Rollback das Schema nicht zurückstellt
   und ein DB-Backup vor dem Update gehört. Automatisches Down-Migrieren wurde bewusst nicht gebaut
   (riskanter als der Status quo).
10. **TD2 – Nightly beendete Prozesse nach Port und Name: behoben.** Nur Prozesse aus dem eigenen
    Checkout (Pfad oder Kommandozeile unter `RepoRoot`) werden beendet. Der Befund war nur teilweise
    bestätigt: Dev-Skript, nicht elevated, der installierte Dienst lauscht auf 47000, nicht 5000.
11. **TD3 – forEach-Kinder verloren die Eltern-Zuordnung: behoben.** Kinder tragen
    `parentExecutionId` und `callDepth`; vorher erschienen sie als Top-Level-Läufe in
    Executions-Liste, Ops-Timeline und Alerting-Klassifizierung, und das Support-Log schrieb
    Start- und Endzeile pro Item.
12. **T2 – `TestDbFactory.Create()` schloss die Connection nie: behoben.** `Create()` übergibt die
    Connection an den Context (`contextOwnsConnection`), `CreateWithConnection()` bewusst nicht.

Verifikation: Engine 66 (Scheduler, Telemetry, ForEach, WinRmSession) und 12 (Pool), API 87
(Backup) und 55 (Ordner, Dep-Graph), AI 78, Data 299 (komplett) und 25 (Ordner-Store), CLI 513 und
MCP 172 (jeweils komplett), Desktop 107 plus `tsc`; beide PowerShell-Skripte parsen fehlerfrei;
CI beider PRs grün, CodeQL ohne neue Befunde außer Style-Notes in Testdateien.

### Zweiter Umsetzungsnachtrag (6. September 2026, Restposten aus dem August-Bericht)

Eine Durchsicht der gesamten Datei gegen den Stand nach #305/#306 ergab vier Posten, die in
keinem Nachtrag als erledigt standen und sich am Code als offen bestätigten. Alle vier sind
umgesetzt:

1. **4.7 – Fehlende Globals liefen als Text weiter: behoben.** Die T-7.1-Prüfung im `StepRunner`
   scannte nur Step- und Manual-Muster; ein Test pinnte fest, dass `{{globals.X}}` **nicht**
   gemeldet wird. Ein fehlendes oder umbenanntes Global blieb wörtlich in REST-Header, Mailtext
   oder Skript stehen, der Step meldete Erfolg. Der Scan kennt jetzt das Globals-Muster mit
   eigenem Befund („Unknown global variable(s)"), auch für `runScript`/Custom Activities; scheitert
   das Laden des Stores, wird ein Lauf, der Globals referenziert, vor dem ersten Step als `Failed`
   beendet statt mit leerem Dictionary zu laufen.
2. **4.6 – Conditions liefen fail-open: behoben (ADR 0015).** Am 3. September waren nur `<`,
   `<=` und `isFalse` bei leerem Operanden geschlossen worden. Unbekannter Typ, Gruppe mit
   unbekanntem Operator oder ohne Kinder, kaputte Legacy-Strings und Verweise auf nicht
   vorhandene Steps ergaben weiterhin `true`; `!=`, `isEmpty` und `contains ""` hielten für einen
   Wert, der nie ankam, und `not` drehte jede geschlossene Antwort wieder auf. Jetzt: neuer
   `EdgeConditionValidator` in Core, eingehängt in die Strukturvalidierung, also an Save, Publish,
   Rollback, nativem und SCOrch-Import, MCP und AI-Merge (`400 invalid-edge-condition` mit
   JSON-Pfad). Zur Laufzeit wirft der Evaluator `ConditionEvaluationException`, der Scheduler
   bricht den Lauf mit Kantenbezug ab. Die Auswertung ist dreiwertig: ein Operand ohne Wert macht
   den Vergleich unentscheidbar, unentscheidbar erfüllt nie, auch nicht hinter `not`.
   Legacy-Kurzformen akzeptieren jetzt auch den `outputVariable`-Alias, den der Designer beim
   Umbenennen schreibt und der vorher nie auflöste. Alerting-Filter behalten ihre Semantik.
3. **4.12 – Step blieb unter terminaler Execution `Running`: behoben, soweit sinnvoll.** Bestätigt
   erreichbar über einen gescheiterten terminalen Step-Write (Lauf `Failed`, Step für immer
   `Running`), den Zombie-Pfad von `/cancel-all` und das User-Offboarding; kein Reconciler besuchte
   solche Steps je wieder. Sichtbar als endloser Spinner im Execution-Panel, tickende Live-Timeline
   und dauerhaft zu hoher „aktive Läufe"-Badge auf der Maschinenseite. Jede terminale
   Execution-Schreibung (Engine-CAS in beiden Pfaden, `/cancel`, `/cancel-all`, Offboarding) setzt
   jetzt die noch offenen Steps auf `Cancelled` (`ExecutionStateLifecycle.CancelOrphanedStepsAsync`).
   Das DB-seitige Fencing der Step-Writes im HA-Betrieb bleibt bewusst offen: `ClusterFencingHost`
   cancelt kooperativ, und der sichtbare Schaden ist mit dem Sweep beseitigt. Mitgenommen: das
   Dashboard zählte „long-running" fest ab 30 Minuten, Operations und Alerting ab
   `Alerting:LongRunningSeconds`; jetzt eine Quelle.
4. **Doku-Widerspruch zu `db_owner`: behoben.** Die Hardening-Seite verlangte einen Login ohne
   `db_owner`, Installer, Provisioning und Deploy-README verlangen die Rolle auf der
   NodePilot-Datenbank für den Migrations-Bootstrap. Die Seite sagt jetzt: least-privilege auf
   Server-Ebene, `db_owner` datenbankgebunden erforderlich. Die Trennung der Migrations- von der
   Laufzeit-Identität (Priority 2) bleibt ein Feature für den Fall, dass ein Kunden-DBA sie fordert.

Korrekturen an diesem Dokument aus derselben Durchsicht: Nachtrag vom 27. August Nr. 6 beschreibt
ein Nachholen, das #265 abgeschafft hat (Vermerk dort); das September-Low zum Desktop-README war ein
Fehlbefund (Vermerk in der Tabelle); der Pattern-Punkt „Alert-Tests mit `All()`" ist im heutigen
Testbestand nicht mehr auffindbar.

Verifikation: Engine 584 (Conditions, Validator, Scheduler, StepRunner, Globals, Unpersisted
Failure, Decision, SCOrch, Manual-Parameter, Grammatik-Parität, Engine-Tests), Data
(Lifecycle), API (Executions, Offboarding, Dashboard, Alerting, Workflow-Controller,
Import/Export, Dep-Graph) — Zahlen siehe PR.

### Verbleibende Posten

Nicht umgesetzt, jeweils mit Einordnung. M1, M2 sowie die beiden Testgrenzen sind bewusste
Entscheidungen und bei einem nächsten Audit nicht neu zu bewerten.

| Prio | Befund des Prüfers | Einordnung |
|---|---|---|
| Medium | **M1 – Workflow-Listen enden bei 500 Einträgen.** `GET /api/workflows` liefert keine Fortsetzung; UI-Teilstringsuche und Ordnerfilter arbeiten lokal, MCP meldet die abgeschnittene Anzahl als `totalAvailable`. Vorschlag: serverseitige Filter und Pagination, Listen als Zusammenfassungen ohne Definition; UI, CLI und MCP müssen Seiten verarbeiten. | Bewusster DoS-Guard, auf der Doku-Website dokumentiert und per `GetAll_CapsResultSetAt500_EvenWhenMoreWorkflowsExist` gepinnt. Bis 500 zugängliche Workflows folgenlos; darüber verschwinden die am längsten unberührten aus Listen und Pickern, Deep-Links und `/export` bleiben. Wenn, dann als Feature über alle drei Clients — die Executions-Pagination (Priority 2 Nr. 5) ist das Vorbild. Kleiner Zwischenschritt: `totalAvailable` im MCP durch ein `truncated`-Flag ersetzen. |
| Medium | **M2 – Rechteberechnung verursacht eine Query je Ordner.** `ResourceAuthorizationService.GetAncestryGrantsAsync` bündelt unterschiedliche Ordner nicht; bei F Ordnern bis zu F zusätzliche Abfragen. Vorschlag: Grants einmal je Request laden und effektive Rollen daraus berechnen. | Kleine indexierte Lookups, nur für Non-Admins, durch das 500er-Cap begrenzt; die Grants liegen aus `GetAccessibleFolderIdsAsync` bereits im Speicher, das Bündeln wäre ein In-Memory-Refactoring. Roadmap-Regel: N+1 nur bei gemessener Latenz. Der Folder-Tree-Endpoint skaliert mit der Gesamtordnerzahl und wäre der Kandidat, falls es misst. |
| Medium | **T1 – Die Browser-E2E-Fixture antwortet nicht gemockten `/api/`-Aufrufen mit `200 []`.** Ein falscher oder neuer Endpoint muss keinen Testfehler auslösen. Vorschlag: unerwartete Aufrufe sammeln und im Teardown fehlschlagen lassen. | Dokumentierte Konvention (`e2e/README.md`, `src/nodepilot-ui/CLAUDE.md`); die Suite ist bewusst hermetisch, 48 von 74 Specs prüfen Request-Verträge in Route-Handlern, Objekt-Endpoints scheitern mit `[]` sichtbar. Verbleibende Lücke: Read-only-Endpoints, deren Payload keine Spec inspiziert. Eine strikte Variante wäre billig, verlangt aber vorher die Durchsicht aller 74 Specs auf beiläufige Aufrufe. Deckt sich mit 4.15. |
| Medium | **T3 – Die Migrationskette wird auf PostgreSQL und SQL Server nur als Text geprüft.** `MigrationDriftTests` generieren Skripte und führen sie dort nicht aus; 33 Migrationen verzweigen auf `ActiveProvider`. Vorschlag: isolierter Integrationstest-Job gegen echte Container. | Bekannt und eingeplant: Roadmap R1 Posten 26. Deckt sich mit 4.15. |
| Low | `GlobalVariableFoldersController.MapError`: `_ => throw ex` setzt den Stacktrace am Wurfpunkt neu, und `InvalidOperationException` wird pauschal als Eingabefehler klassifiziert, obwohl der Typ auch Infrastrukturfehler beschreibt. Vorschlag: gezielte Catch-Blöcke oder fachliche Exception-Typen. | Offen; klein, ohne Betriebsauswirkung. |
| Low | Das Desktop-README nennt 120 Sekunden Readiness-Wartezeit, `main.ts` setzt 240. | **Fehlbefund.** Die 120 s im README sind `Database:StartupWaitSeconds` (Warten der API auf die Datenbank), die 240 s in `main.ts` die Readiness-Frist der Electron-Shell — zwei verschiedene Timeouts, beide korrekt dokumentiert. |
| Low | `ActivityLogger` des Switchers hält die Historie unbegrenzt im Speicher, die Oberfläche baut sie wiederholt vollständig auf. Vorschlag: Ringpuffer und inkrementelle Aktualisierung. | Offen. |
| Low | Doku-Website: das Inhaltsverzeichnis (`Toc.tsx`) reagiert nicht auf einen Sprachwechsel derselben Seite, und seitenübergreifende Links verlieren ihr Fragment (`DocPage.tsx`). | Offen; vom Prüfer benannt, nicht gegengeprüft. |

Strukturvorschläge des Prüfers, die über die Fixes hinausgehen und offen bleiben:

- Reine Baumalgorithmen (Zyklusprüfung, Tiefe, Pfadberechnung) einmal implementieren statt doppelt
  in `SharedWorkflowFoldersController` und `GlobalVariableFolderStore`; die unterschiedlichen
  Autorisierungsregeln rechtfertigen getrennte Services, nicht doppelte Algorithmen. Lock und
  Besuchsmengen wurden in beide Kopien eingebaut.
- Den prozessweiten Zustand der `WorkflowEngine` (statische Dictionaries, Kapazitätszähler) in
  einen DI-Singleton mit testbarer Lebensdauer überführen; heute serialisieren die Engine-Tests
  deshalb ihre Ausführung (`SerialEngineTestCollection`).
- `BackupRestoreService` (rund 1.400 Zeilen) entlang eines Restore-Planers zerlegen — der Restore
  ist jetzt korrekt, die Größe der Klasse gehört weiter zu Priority 3 „Große Dateien zerlegen".
- Audit-Schreibfehler brechen normale Mutationen bewusst nicht ab (`AuditWriter`); eine
  erfolgreiche Mutation garantiert deshalb keinen Audit-Datensatz. Gewollt, siehe Priority 2
  „Audit- und SIEM-Zustellung über Outbox absichern".
- Der Installer bündelt ACLs, Zertifikatrechte, SCM-Anlage und Rollback; der vorhandene
  `SetupContract` ist die Grenze für eine Zerlegung.

Offene Fragen des Prüfers, aus dem Code nicht beurteilbar: Zielgrößen für Workflows, Ordner,
gleichzeitige Steps und Ergebnisgrößen; ob PostgreSQL, SQL Server, WinRM und HA unter realen
Ausfällen abgenommen wurden (siehe 4.15 und Abschnitt 12); welche Update- und Restore-Garantien
produktiv gelten (Restore-Drill, siehe 4.14); ob die Desktop-App Exporte ermöglichen soll — sie
blockiert Downloads (`security.ts`), während die SPA Download-basierte Exporte anbietet; der
aktuelle Advisory-Stand der Abhängigkeiten. Das Vertrauensmodell, nach dem ein Operator Workflow-Code
unter der Dienstidentität ausführen darf und Folder-RBAC keine Sandbox ist, hat der Prüfer als
dokumentierte Produktentscheidung bestätigt (siehe Abschnitt 7, „Operator").

## 1. Executive Summary

Für einen unbeschränkten, produktionsnahen Ablöse-POC lautet die Bewertung: **No-Go**.

Für einen eng abgegrenzten technischen POC in einer isolierten Testumgebung ist ein **Conditional Go** vertretbar, sofern die Guardrails aus Abschnitt 12 eingehalten werden.

Die Architektur ist grundsätzlich sauber und umfangreich getestet. Die größten Risiken liegen jedoch exakt an den Orchestrator-Grenzen: Berechtigungen, dauerhafte Auftragsannahme, Restart/Failover, Triggerzustellung, Bedingungen, Retries, SCOrch-Import und Disaster Recovery.

Bestätigte Showstopper:

1. Operatoren können den Admin-only Workflow-Delete über rekursives Löschen eines Ordners umgehen.
2. Bereits angenommene Ausführungen sind nicht restart- oder failoverfest.
3. Ein Crash kann einen Idempotency-Key 24 Stunden lang an eine verlorene Ausführung binden.
4. Retry verwendet redigierte oder abgeschnittene Eingaben und kann dadurch mit falschen Parametern laufen.
5. Konditionale Fan-ins hängen von der Abschlussreihenfolge ihrer Vorgänger ab.
6. Ungültige Bedingungen laufen fail-open und können veröffentlicht werden.
7. Fehlende Globals gelangen als literale `{{globals.X}}`-Werte in Aktivitäten.
8. UTF-16-SCOrch-Exporte sind über UI, CLI und MCP nicht zuverlässig importierbar.

### Ausgeführte Prüfungen

- Backend: 6.030/6.030 Tests bestanden
- Haupt-UI: 2.851 Tests bestanden
- UI-Coverage: 75,26 % Statements, 64,27 % Branches
- Playwright: 404 bestanden, 22 übersprungen, ein Test zunächst flaky; anschließend 10/10 gezielte Wiederholungen bestanden
- Docs-UI: 23 Tests bestanden
- Desktop: 82 Tests bestanden
- NuGet-Scan über 18 Projekte: keine bekannte verwundbare Abhängigkeit
- `npm audit` in allen drei Node-Anwendungen: 0 Findings
- Release-Build erfolgreich, aber 1.802 Warnungen
- `git diff --check`: keine Whitespace-Fehler

Die grünen Tests schließen die Befunde nicht aus: Browser-E2E-Tests mocken sämtliche APIs und Migrationen laufen nicht gegen echte PostgreSQL- oder SQL-Server-Instanzen.

## 2. Detected Architecture

Die beabsichtigte Architektur ist ein modularer .NET-Monolith:

```text
Core
├── Data / Remote / Telemetry / AI
├── Engine
│   └── Scheduler
└── API
    ├── React UI
    ├── CLI
    └── MCP
```

Die Abhängigkeitsrichtung wird unter anderem durch `DependencyDirectionTests` erzwungen. Domänensprache und Entscheidungen sind mit `CONTEXT.md` und zwölf ADRs überdurchschnittlich gut dokumentiert.

Die tatsächlichen Brüche entstehen vor allem zwischen Schichten:

- API-RBAC, Folder-RBAC und UI-Capabilities bilden nicht dieselbe Autorisierungslogik ab.
- Persistierte Execution-Zustände und die In-Memory-Dispatch-Queue bilden keine gemeinsame transaktionale Zustellgarantie.
- Importgrenzen werden im Backend bytegenau behandelt, in den Clients jedoch vorher in Strings umgewandelt.
- Produktdokumentation, Produktionskonfiguration und tatsächliches Logging widersprechen sich.
- Tests sind breit, aber entscheidende Infrastrukturgrenzen werden überwiegend gemockt.

## 3. Coherence Scorecard

Bei „AI Artifact Risk“ bedeutet eine hohe Zahl ein geringes Risiko.

| Dimension | Wert | Begründung |
|---|---:|---|
| Overall Architecture Coherence | 7/10 | Klare Schichten und Domain-Definitionen; kritische Verträge brechen aber an Schichtgrenzen. |
| Project Structure Consistency | 8/10 | Projekte sind sinnvoll getrennt, einzelne Controller, Services und UI-Seiten sind stark übergroß. |
| Naming Consistency | 7/10 | Meist konsistent; Begriffe wie Retry, Total, Backup Encryption und Operator vermitteln teilweise falsche Garantien. |
| Dependency Discipline | 9/10 | Abhängigkeitsrichtungen werden durch Architekturtests wirkungsvoll erzwungen. |
| Duplication Control | 6/10 | Capability-, Settings-, Client- und Dokumentationslogik existiert an mehreren Stellen und driftet. |
| Pattern Consistency | 6/10 | Fail-closed Security steht neben fail-open Conditions; State-Updates wechseln zwischen CAS und ungeprüftem `SaveChanges`. |
| Test Consistency | 7/10 | Sehr große Suite, aber echte Datenbanken, WinRM, Installer, Windows Desktop und reales API-E2E fehlen. |
| Maintainability | 6/10 | Mehrere Dateien zwischen 1.000 und 2.000 Zeilen, 1.802 Buildwarnungen und verteilte Runtime-Zustandslogik. |
| AI Artifact Risk | 7/10 | Kein belastbarer Nachweis für AI-Slop; sichtbar sind eher Contributor- und Dokumentationsdrift. |

Separate POC-Betriebsreife: **4/10** für eine echte Orchestrator-Ablösung, höher für eine reine Happy-Path-Demo.

## 4. Major Findings

### 4.1 P1 – Operator kann ganze Workflow-Teilbäume löschen

Direktes Löschen eines Workflows ist Admin-only. Rekursives Ordnerlöschen verlangt dagegen nur `ResourceOp.Edit` und entfernt Workflows inklusive Execution-Historie. Ein standardmäßig angelegter Operator mit Root-`FolderEditor` kann dadurch einen Nicht-Root-Teilbaum samt Historie löschen.

Evidenz: `WorkflowsController.cs:491`, `SharedWorkflowFoldersController.cs:262`, `UsersController.cs:108`, `ResourceAuthorizationService.cs:289`, `SharedWorkflowFoldersControllerTests.cs:238`.

Empfehlung: Recursive Delete ausschließlich Admins beziehungsweise einer eigenen Delete-Capability erlauben. Bis dahin keine normalen Operator-Konten mit Root-`FolderEditor`; Endpoint am Reverse Proxy sperren.

### 4.2 P1 – Angenommene Executions sind nicht dauerhaft zugestellt

`202 Accepted` bedeutet nicht, dass eine Ausführung einen Neustart überlebt. Die Queue ist vollständig im Speicher. Startup-Recovery storniert `Pending`, `Running` und `Paused`, statt sie wieder aufzunehmen.

Evidenz: `ExecutionDispatchQueue.cs:19`, `ExecutionDispatchService.cs:55`, `StartupRecovery.cs:55`.

Impact: Ein Crash zwischen DB-Commit und Worker-Dequeue verliert einen bereits akzeptierten Auftrag. Laufende Workflows werden bei Restart oder HA-Handoff nicht fortgesetzt.

Empfehlung: Persistente Outbox/Dispatch-Tabelle mit Lease, Ack und Recovery. Bis dahin keine Restarts, Deployments oder Failover während aktiver Runs; Queue vor Wartung drainieren und anschließend stornierte Runs abgleichen.

### 4.3 P1 – Idempotency-Key kann einen verlorenen Auftrag vergiften

External Trigger persistiert Execution und Idempotency-Key, enqueued aber anschließend separat. Nach einem Crash wird die Execution storniert, derselbe Key liefert weiterhin diese stornierte Execution.

Evidenz: `ExternalTriggerController.cs:96,320-401`, `StartupRecovery.cs:55-99`.

Impact: Der Absender kann bis zum Ablauf des Keys keinen neuen Lauf auslösen.

Empfehlung: Dispatch atomar über eine Outbox koppeln und Replays statusbewusst behandeln. Bis dahin nach Restart Idempotency-Keys und stornierte Triggerausführungen aktiv reconciliieren.

### 4.4 P1 – Retry kann mit falschen oder leeren Parametern laufen

Der persistierte Parametersnapshot ist für Logging redigiert und auf 32 KiB begrenzt. Retry verwendet genau diesen Snapshot als vermeintlich identische Eingabe. Abgeschnittenes JSON wird still verworfen.

Evidenz: `ExecutionDispatchService.cs:63-78,352-358`, `ExecutionsController.cs:533-568`.

Impact: Secrets werden als Maskierung wiederholt; große Parameterläufe werden ohne Eingaben erneut gestartet.

Empfehlung: Einen getrennten verschlüsselten, ungekürzten Retry-Snapshot speichern oder Retry für solche Ausführungen verbieten. Im POC nicht für secret- oder parameterabhängige Runs verwenden.

### 4.5 P1 – Konditionale Fan-ins sind reihenfolgeabhängig

Nach der Fan-in-Readiness-Prüfung wird nur die Kante des zuletzt abgeschlossenen Vorgängers ausgewertet. Derselbe Workflow kann abhängig von Laufzeit und Abschlussreihenfolge unterschiedliche Zweige nehmen.

Evidenz: `WorkflowScheduler.cs:214-225,285-322`.

Empfehlung: Join-Semantik explizit definieren und alle relevanten Eingangskanten auswerten. Im POC keine konditionalen Fan-ins einsetzen.

**Umsetzungsstatus 26. August 2026: behoben.** Direkter Fan-in auf normale Activities ist ungültig;
Junctions sind verpflichtend und alle relevanten Eingangskanten werden reihenfolgeunabhängig
ausgewertet. Die dauerhafte Entscheidung ist in ADR 0013 dokumentiert.

### 4.6 P1 – Fehlerhafte Conditions laufen fail-open

Unbekannte Typen, ungültige Gruppen, fehlende Steps und fehlerhafte Legacy-Ausdrücke ergeben `true`. Publish aktiviert Definitionen, ohne den vorhandenen Analyzer als Gate zu verwenden.

Evidenz: `ConditionEvaluator.cs:49-68,304-320`, `WorkflowEditingController.cs:456-512`, `EvaluateConditionTests.cs:63-82`.

Impact: Eine fehlerhafte Schutzbedingung kann den destruktiven Pfad freigeben, den sie verhindern sollte.

Empfehlung: Conditions beim Publish validieren und zur Laufzeit fail-closed behandeln. Bis dahin jede Bedingung manuell prüfen und negativ testen.

**Umsetzungsstatus 6. September 2026: behoben** (ADR 0015; Details im zweiten Umsetzungsnachtrag
zum September-Audit). Der Teilschritt vom 3. September (`<`, `<=`, `isFalse` bei leerem Operanden)
war davor die einzige Änderung.

### 4.7 P1 – Fehlende Globals werden als Text weitergereicht

Ist der Global Store nicht erreichbar oder eine Variable fehlt, bleibt `{{globals.API_KEY}}` stehen. Die Unresolved-Prüfung erkennt Step- und Manual-Variablen, aber keine Globals.

Evidenz: `WorkflowEngine.cs:238-263`, `VariableResolver.cs:250-260`, `StepRunner.cs:711-741`.

Impact: REST-, Mail- oder Script-Aktivitäten können mit einem literalisierten Secret-Platzhalter laufen und trotzdem Erfolg melden.

Empfehlung: Alle referenzierten Globals vor Start auflösbar machen und fehlende Globals als harten Fehler behandeln.

**Umsetzungsstatus 6. September 2026: behoben** (Details im zweiten Umsetzungsnachtrag zum
September-Audit). Bis dahin unverändert offen; der Nachtrag vom 26. August hatte den Punkt nur als
manuelle Abnahme geführt.

### 4.8 P1 – SCOrch-UTF-16-Import ist clientseitig beschädigt

Das Backend akzeptiert XML als Raw Bytes, UI, CLI und MCP dekodieren aber vorher in einen String und senden UTF-8. Typische UTF-16LE/BOM-Exporte können dadurch scheitern.

Evidenz: `WorkflowImportExportController.cs:317-335`, `WorkflowsPage.tsx:374-396`, `WorkflowImportExportCommands.cs:106-123`, `NodePilot.Mcp/Api/NodePilotApiClient.cs:208-214`.

Empfehlung: Dateien bytegenau übertragen. Für den POC Raw-API-Upload oder kontrollierte UTF-8-Konvertierung inklusive XML-Deklaration.

### 4.9 P1/P2 – SCOrch-Kompatibilität ist best-effort

- Gemeinsames Hard-Limit von 500 Workflows plus Variablen
- Zahlreiche nicht unterstützte Aktivitäten
- VBScript, JScript und C# werden deaktiviert
- `Policy.Name`, `Policy.PID` und ähnliche Metadaten bleiben offen
- Vergleichssemantik kann zwischen SCOrch und NodePilot abweichen
- Der vollständige Remediation-Report existiert nur im UI-State und verschwindet beim Schließen

Empfehlung: Echten Kundenexport vor dem POC nach Aktivitätstypen, Scriptsprachen, Größen, Runbookzahl und Variablen inventarisieren. Importantwort als Abnahmeartefakt speichern und importierte Workflows zunächst deaktiviert lassen.

### 4.10 P1 – Native Imports können Trigger sofort reaktivieren

Native Exporte enthalten `IsEnabled`; der Import übernimmt diesen Wert. Importierte Schedule-, File-, DB- oder EventLog-Trigger können in der Testumgebung sofort reale Ziele ansprechen.

Evidenz: `WorkflowImportExportController.cs:199-241`.

Empfehlung: POC-Ziel vollständig isolieren und native Dateien vor dem Import auf `IsEnabled=false` setzen. Erst nach Ziel-, Credential- und Side-effect-Prüfung einzeln aktivieren.

### 4.11 P1/P2 – Trigger bieten keine durchgehende At-least-once-Garantie

Trigger während eines DB-Ausfalls werden verworfen. DB-Sentinels sind prozesslokal, EventLog hat keinen Replay, FileWatcher kann bei Overflow verlieren und Cron-Misfires werden übersprungen.

Evidenz: `TriggerOrchestrator.cs:511-523`, `DatabaseTriggerSource.cs:89-115`, `EventLogTriggerSource.cs:14-33`, `FileWatcherTriggerSource.cs:356-400`.

Empfehlung: Für relevante Trigger externe durable Queue oder Reconciliation-Jobs einsetzen und Workflows idempotent gestalten.

### 4.12 P2 – Weitere Runtime-State-Defekte

- Volle Queue plus Fire-and-forget-Subworkflows kann alle Worker blockieren.
- Capacity-Rejection kann eine Execution als `Running` zurücklassen.
- Cancellation kann ein gerade gespeichertes `Succeeded` oder `Failed` mit `Cancelled` überschreiben.
- Ein Step kann unter einer terminalen Execution dauerhaft `Running` bleiben.
- Im HA-Betrieb sind Execution-Writes gefencet, Step-Writes jedoch nicht.

**Umsetzungsstatus 27. August 2026:** Die ersten drei Punkte sind durch Durable Outbox, DB-Priorität
und CAS-Terminalisierung behoben. Die beiden Step-State-Punkte gehören zu Priority 2 Nr. 2 und waren
nicht Teil dieses Fixauftrags; sie bleiben offen.

**Umsetzungsstatus 6. September 2026:** Der vierte Punkt ist behoben — jede terminale
Execution-Schreibung setzt offene Steps auf `Cancelled` (zweiter Umsetzungsnachtrag zum
September-Audit, Nr. 3). Der fünfte Punkt (DB-seitiges Fencing der Step-Writes) bleibt bewusst
offen.

### 4.13 P1 – Produktionslogging verletzt den Observability-Vertrag

Production setzt den Root-Level auf `Warning`. Boot, Execution-Start/-Erfolg, normale User-Logs und committed Audit-/SIEM-Events werden als `Information` geschrieben und daher vor den Sub-Sinks entfernt.

Evidenz: `deploy/templates/appsettings.Production.json.template:87-95`, `LoggingSetup.cs:142-170`, `AuditEventForwarder.cs:43-80`.

Empfehlung: `Default=Information`, laute Microsoft- und EF-Kategorien gezielt auf `Warning`. Vor dem POC Boot, Erfolg, Fehllogin und SIEM-Eingang verifizieren.

### 4.14 P1/P2 – Backup kann falsche Sicherheit vermitteln

- `.npbackup` ist lesbares JSON mit HMAC und feldweiser Verschlüsselung, nicht vollständig verschlüsselt.
- Maschinen-, Identitäts- und Workflow-Metadaten bleiben lesbar.
- Nicht entschlüsselbare Secrets werden ausgelassen, die UI meldet trotzdem Erfolg.
- Das Konfigurationsbackup enthält keine Execution-Historie, Auditlogs oder Statistiken.
- Settings-Restore erfolgt nach DB-Commit und ist nicht vollständig atomar.

Empfehlung: Nur Backups mit `warnings=0` akzeptieren, verschlüsseltes Medium und restriktive ACL verwenden, zusätzlich native DB-, ProgramData-, Konfigurations- und Keyring-Sicherung erstellen und Restore-Drill durchführen.

### 4.15 P1 – Reale Kundenplattform ist durch CI nicht abgedeckt

Playwright mockt alle APIs. Migrationen werden nur über SQLite beziehungsweise SQL-Generierung geprüft. Reales WinRM ist ein Lab-Smoke-Test. Die Installer-GUI wurde nicht interaktiv automatisiert; mehrere beworbene Plattformkombinationen wurden nicht vollständig im Labor abgenommen.

Empfehlung: Exakte Zielmatrix vor dem Kundentermin auf einem Klon testen: Windows-Version, SQL-Provider, gMSA, WinRM/Kerberos, TLS/SAN, Proxy, AV/EDR und Authenticode/AppLocker.

## 5. Duplication and Consolidation Opportunities

| Konzept | Drift | Empfehlung |
|---|---|---|
| Berechtigungen | Rollen-Capabilities, Folder-Grants, Controllerprüfungen und UI-Flags entscheiden separat | Eine zentrale Operation-Policy pro destruktiver Aktion verwenden |
| Dokumentation | Root-Dokumente und Docs-UI widersprechen sich bei Backup, Importendpoint und MCP-Installation | Eine kanonische Quelle generieren oder Docs-Parität in CI prüfen |
| Settings | Config, DTO, UI und Controller-Defaults laufen auseinander | Settings aus einem gemeinsamen Schema ableiten |
| API-Clients | UI, CLI und MCP besitzen eigene Import-/DTO-Logik | Gemeinsamen generierten Client oder Contract-Tests pro HTTP-Methode verwenden |
| Execution State | Controller, Queue, Worker, Engine und Recovery mutieren denselben Lebenszyklus | Eine zentrale persistente State Machine mit CAS/Fencing etablieren |

Provider-spezifische Migrationen und getrennte UI-Anwendungen sollten nicht künstlich konsolidiert werden.

## 6. Structural Problems

Mehrere zentrale Dateien tragen zu viele Verantwortlichkeiten:

- `WorkflowEditorPage.tsx`: ca. 1.970 Zeilen
- `SettingsSections.cs`: ca. 1.809 Zeilen
- `WorkflowsPage.tsx`: ca. 1.524 Zeilen
- `WorkflowEngine.cs`: ca. 1.459 Zeilen
- `BackupRestoreService.cs`: ca. 1.412 Zeilen
- `AuthController.cs`: ca. 1.302 Zeilen

Der dringendste strukturelle Umbau betrifft den Execution-Lebenszyklus:

```text
API Admission Transaction
  ├── Execution
  ├── Idempotency Record
  └── Durable Dispatch Outbox
             ↓
      Leased Dispatcher
             ↓
   Validated Workflow Version
             ↓
 Fenced Execution- und Step-State
```

## 7. Naming and Terminology Problems

- „Retry with identical inputs“ stimmt nicht mit der Implementierung überein.
- „Total“ bedeutet in der UI faktisch „Anzahl der neuesten maximal 500 Elemente“.
- „Encrypted backup“ suggeriert eine verschlüsselte Gesamtdatei.
- „Operator“ klingt nach eingeschränkter Rolle, kann aber PowerShell als Servicekonto ausführen.
- `FolderEditor` enthält indirekt eine destruktive Admin-Funktion.
- „Healthy“ beim EventLog-Trigger sagt nichts über Replayfähigkeit oder einen gestorbenen Watcher aus.
- README nennt `POST /api/import`; tatsächlich ist die Route `/api/workflows/import`.
  **Behoben 6. September 2026 (#309).**

## 8. Pattern Inconsistencies

- TLS und Secret-Schutz sind überwiegend fail-closed; Workflow-Conditions sind fail-open.
  **Behoben 6. September 2026 (ADR 0015), siehe 4.6.**
- Execution-Terminalwrites verwenden CAS/Lease-Prädikate; Step- und Cancel-Writes teilweise normales `SaveChanges`.
  **Teilweise behoben 6. September 2026:** verwaiste Steps werden bei jeder terminalen
  Execution-Schreibung mitgezogen (4.12); ein DB-seitiges Fencing der Step-Writes bleibt offen.
- SCOrch-Import ist serverseitig raw-byte-orientiert, clientseitig stringorientiert.
- Backend- und UI-Importlimits unterscheiden sich stark.
- Alert-Tests können wegen `All()` auf einer leeren Route grün melden.
  **Nicht mehr auffindbar (6. September 2026):** keine Alerting-Testdatei enthält heute eine
  `All()`-Zusicherung.
- Long-running Threshold ist in Operations konfigurierbar, im Dashboard aber fest kodiert.
  **Behoben 6. September 2026:** beide lesen `Alerting:LongRunningSeconds`.
- Credential-Rotation invalidiert den bestehenden WinRM-Sessionpool nicht sofort.
  **Behoben 6. September 2026 (#306): Credential-Fingerprint im Pool-Key, siehe Audit vom
  5. September, M3.**

## 9. AI-Code / Contributor-Drift Indicators

Es gibt keinen belastbaren Grund, den Code pauschal als AI-generiert oder Slop zu bezeichnen. Domainmodell, ADRs, Tests und Abhängigkeitsregeln sind dafür zu konsistent.

Driftindikatoren:

- Großer, bereits geänderter Working Tree erschwert Release-Provenienz.
- 1.802 Buildwarnungen reduzieren den Signalwert neuer Warnungen.
- Schädliches Verhalten wird teilweise in Tests als gewünschtes Verhalten festgeschrieben.
- Dokumentation ist mehrfach gepflegt und bereits sichtbar auseinandergelaufen.
- Sehr große Dateien fördern lokale Lösungen statt einheitlicher Patterns.

## 10. Recommended Target Architecture

NodePilot sollte auf fünf verbindliche Verträge konvergieren:

1. **Durable Execution Contract:** Akzeptiert bedeutet persistent dispatchbar; definierte Restart-, Retry- und Triggergarantie.
2. **Compiled Workflow Contract:** Publizierte Definitionen sind vollständig validiert; Conditions und Globals scheitern geschlossen.
3. **Unified Authorization Contract:** API, Folder-RBAC und UI verwenden dieselben benannten Operationen.
4. **Byte-preserving Integration Contract:** Imports bleiben bis zum Parser rohe Bytes; Limits und Ergebnis-Metadaten kommen vom Server.
5. **Observable Operations Contract:** Produktdefaults erfüllen die dokumentierte Support-, Audit- und SIEM-Semantik.

## 11. Prioritized Action Plan

### Priority 1 – Critical Coherence Issues

1. Recursive Folder Delete für Nicht-Admins schließen oder technisch blockieren.
2. Produktionslogging auf Information korrigieren und SIEM-Smoke-Test durchführen.
3. Restart-/Trigger-/Idempotency-Garantie ausdrücklich festlegen und testen.
4. Retry mit Secrets beziehungsweise großen Inputs sperren.
5. Konditionale Fan-ins aus dem POC entfernen; Conditions und Globals manuell abnehmen.
6. Kunden-SCOrch-Export bytegenau inventarisieren und probeimportieren.
7. Native Imports ausschließlich deaktiviert und in isolierte Zielsysteme einspielen.
8. Exakte Kundenplattform inklusive DB, gMSA/WinRM, TLS, AV/EDR und Restore durchtesten.
9. Einen sauberen, unveränderlichen Commit und ein daraus gebautes signiertes POC-Artefakt festlegen.

### Priority 2 – Structural Cleanup

- Durable Outbox und zentralen Execution-State-Lifecycle einführen.
- Step-Writes mit Execution-Lease und Concurrency-Token fencen. Der sichtbare Teil (verwaiste
  `Running`-Steps) ist seit 6. September 2026 durch den Sweep bei jeder terminalen
  Execution-Schreibung erledigt; das DB-seitige Fencing bleibt bewusst offen.
- Queue-Deadlock, Capacity-Ghost-States und Cancellation-Race korrigieren.
- Backupwarnungen im UI sichtbar und als Fehler behandelbar machen.
- Server-seitige Pagination mit echtem Total statt stiller 500er-Grenze. Für Executions umgesetzt
  (Priority 2 Nr. 5); die Workflow-Liste bleibt bewusst bei 500, siehe Audit vom 5. September, M1.
- Capability-, Settings- und Clientverträge vereinheitlichen.
- Audit- und SIEM-Zustellung bei Bedarf über Outbox absichern.
- Datenbankmigration langfristig von der Runtime-`db_owner`-Identität trennen. Der
  Doku-Widerspruch dazu (Hardening-Seite „kein `db_owner`" gegen Installer und Deploy-README) ist
  seit 6. September 2026 bereinigt; die Trennung selbst bleibt ein Feature auf Anforderung.

### Priority 3 – Nice-to-Have Improvements

- Große Controller-, Engine- und UI-Dateien zerlegen.
- Buildwarnungen schrittweise auf null oder einen kleinen festen Baselinewert reduzieren.
- Dokumentation aus einer Quelle veröffentlichen.
- Starter-Templates, vollständige englische UI und CLI/MCP-Parität verbessern.
- Große UI-Bundles stärker aufteilen.

## 12. Suggested Guardrails

- Single Node, kein HA-Test ohne separate Freigabe.
- Dedizierte, minimal berechtigte gMSA; kein LocalSystem auf gemeinsam genutzten Hosts.
- Nur Admins oder eng begrenzte `FolderOperator`; kein Root-`FolderEditor`.
- Recursive Folder Delete am Reverse Proxy sperren.
- Keine Deployments, Restarts oder Wartungsfenster während laufender Workflows.
- Keine direkten Fan-ins auf normale Activities; für jede Zusammenführung die angebotene Junction
  verwenden und deren Modus (`waitAll`, `waitAny`, `waitNofM`) fachlich prüfen.
- Kein UI-Retry für parameter- oder secretabhängige Runs.
- Kritische Trigger über durable externe Quelle plus Reconciliation absichern.
- SCOrch-Datei vorab auf Encoding, Größe, Anzahl und Aktivitätstypen prüfen.
- Importbericht sofort speichern; jeden importierten Workflow deaktiviert reviewen.
- Testziele und Credentials ausschließlich auf POC-Systeme beschränken.
- `Logging:LogLevel:Default=Information`; Audit-, Support- und SIEM-Smoke-Tests.
- Nur Backups mit null Warnungen akzeptieren; zusätzlich native DB-Sicherung und Restore-Drill.
- Für jeden migrierten Prozess Alt- und Neusystem zunächst parallel beobachten und fachliche Ergebnisse vergleichen.
- POC-Erfolg nicht nur am Happy Path messen: Restart, DB-Ausfall, verlorener Trigger, doppelter Trigger, fehlendes Global und negativer Condition-Pfad gehören in die Abnahme.

## 13. Final Verdict

**Repository-Verdict: mostly coherent with some drift.**

**Produkt-Verdict: high-risk für einen unbeschränkten Orchestrator-Ablöse-POC; Conditional Go für einen isolierten und streng begrenzten Test-POC.**

Die wichtigste nächste Maßnahme ist, den Execution-Admission-Pfad aus Execution, Idempotency und Dispatch dauerhaft und wiederanlauffähig zu machen. Ohne diese Garantie beweist ein POC nur den Happy Path, nicht die Eignung als Orchestrator.

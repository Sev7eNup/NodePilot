# NodePilot Repository- und POC-Audit

- Stand der Prüfung: 26. August 2026
- Geprüfter Commit: `fa1a760` auf `chore/comment-cleanup-pass` plus bereits vorhandener Working Tree mit 243 geänderten Einträgen
- Prüfmodus: read-only; während des Audits wurden keine Repository-Dateien verändert

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

### 4.7 P1 – Fehlende Globals werden als Text weitergereicht

Ist der Global Store nicht erreichbar oder eine Variable fehlt, bleibt `{{globals.API_KEY}}` stehen. Die Unresolved-Prüfung erkennt Step- und Manual-Variablen, aber keine Globals.

Evidenz: `WorkflowEngine.cs:238-263`, `VariableResolver.cs:250-260`, `StepRunner.cs:711-741`.

Impact: REST-, Mail- oder Script-Aktivitäten können mit einem literalisierten Secret-Platzhalter laufen und trotzdem Erfolg melden.

Empfehlung: Alle referenzierten Globals vor Start auflösbar machen und fehlende Globals als harten Fehler behandeln.

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

## 8. Pattern Inconsistencies

- TLS und Secret-Schutz sind überwiegend fail-closed; Workflow-Conditions sind fail-open.
- Execution-Terminalwrites verwenden CAS/Lease-Prädikate; Step- und Cancel-Writes teilweise normales `SaveChanges`.
- SCOrch-Import ist serverseitig raw-byte-orientiert, clientseitig stringorientiert.
- Backend- und UI-Importlimits unterscheiden sich stark.
- Alert-Tests können wegen `All()` auf einer leeren Route grün melden.
- Long-running Threshold ist in Operations konfigurierbar, im Dashboard aber fest kodiert.
- Credential-Rotation invalidiert den bestehenden WinRM-Sessionpool nicht sofort.

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
- Step-Writes mit Execution-Lease und Concurrency-Token fencen.
- Queue-Deadlock, Capacity-Ghost-States und Cancellation-Race korrigieren.
- Backupwarnungen im UI sichtbar und als Fehler behandelbar machen.
- Server-seitige Pagination mit echtem Total statt stiller 500er-Grenze.
- Capability-, Settings- und Clientverträge vereinheitlichen.
- Audit- und SIEM-Zustellung bei Bedarf über Outbox absichern.
- Datenbankmigration langfristig von der Runtime-`db_owner`-Identität trennen.

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

# Runde 3 – Runtime, Daten und AI

Status: **vollständige Quellenrunde abgeschlossen; Findingsrunde mit behobenen
Medium-Befunden**. Die frische Gesamtprüfung fand weitere Mängel; dieser Bericht
ersetzt deshalb nicht die notwendige nächste vollständige Projektrunde.

Ausgangspunkt ist der von Root eingefrorene Quellenstand vom 2026-10-09T10:46:01Z,
Manifest-Hash `9ec49063c7b0b522b5ade566c36b70d1160c9680954d3482cba1a2c49384032b`
(3166 Dateien). Die Runde verwendet erneut `improve-codebase-architecture` und
`audit-ai-slop-code`, mit rotiertem Besitz und frischer Implementierungslektüre;
Ergebnisse früherer Runden ersetzen diese Lektüre nicht.

## Umfang und Nachweis

| Module | Status dieser Runde | Konkrete Entry Points / geprüfte Invarianten |
|---|---|---|
| Remote (6 Dateien) | Implementation vollständig frisch gelesen | `CreateSessionAsync`, `ExecuteScriptAsync`, Pool Checkout/Return/Sweep/Dispose; Credential-Fingerprint, Timeout/Cancel, kein verstecktes Script-Replay, WinRM-Fehlerklassifikation, NoOp-Konfiguration und Metriken |
| Telemetry (5 Dateien) | Implementation vollständig frisch gelesen | SDK/Serilog-Registrierung, Resource Identity, EF-Parameterunterdrückung, Prometheus Instant/Range/Scalar-Transport und Parser; explizite Admin-Endpunkte bleiben Vertrauensvoraussetzung |
| Data/Security (7 Dateien) | Implementation vollständig frisch gelesen | AES-GCM/DPAPI, Envelope, PBKDF2/HKDF-Unterkeys, MAC/Verifier, Provider-Bootstrap/Registry und Migrations-Fallback; Konfiguration ist vertrauenswürdig |
| Data Stores | Implementation vollständig frisch gelesen | Credential, Globals/Folder, CustomActivity, Maintenance, NotificationRule, ExecutionLog, OperationalKnowledge, WebhookReplay; Scope-Filter, Secret-Persistenz, Konkurrenz-/Commit-Seams, verfügbare Consumer |
| Data Availability (6 Dateien) | Implementation vollständig frisch gelesen | Tracker, Command-/Connection-Interceptors, Retry-Entscheidung, Probe-Connectionstring und CommandBudget; Episode, Fail-closed-Öffnung, keine unbedingte automatische Replay-Erlaubnis |
| Data übrige Runtime-Dateien | Implementation vollständig frisch gelesen | DbContext/AgentModelConfiguration, OutboxClaim, ExecutionStateLifecycle, MigrationBootstrapper/Portability, Fehlerklassifikation, TreeLock, DpapiScopeResolver, Metrics |
| Data Migrationen | Up/Down der drei Ausgangsmigrationen vollständig gelesen; beide neuen Migrationen und generierte Modelländerungen abgeglichen | Baseline-Tabellen/FKs/Indizes/Seed; Agent-Tabellen; Triggercount-Backfill; Snapshotänderungen ausschließlich ConcurrencyToken, zwei Typbreiten und Live-Key-Index. Designer mit aktiver Provider-Filter-/StoreType-Anpassung; Drift-/native Script-/SQLite-UpDownUp-Tests |
| Engine/Agents (19 Dateien) | Implementation vollständig frisch gelesen | ActivityRunner, ToolHost, Target, Journal/Checkpoint/RunDatabase, PermissionPolicy, ExternalReadPolicy, ReadOnlyWorkflowScope, ExecutionGate, File/Artifact/Skill/MCP/ProcessScript; Modellinhalt bleibt untrusted, Policy vor Side Effects, Parent-Identität und begrenzte Ausgabe |
| Engine/Security (8 Dateien) | Implementation vollständig frisch gelesen | REST-Provider und atomare Listen, NetworkGuard, PathGuard/Watcher/Target-Script, OutputRedactor und PowerShellQuoter; Proxy-Zielprüfung, Reparse-Points, Quoting, Redaction-Grenzen |
| Engine Activities (39 Dateien) | Implementation vollständig frisch gelesen | Alle Executor-Bodies einschließlich ControlFlow, Queries, REST/Email, File/Folder/Hash/Zip/Text, Registry/Service/Power/ScheduledTask/StartProgram, SQL/WMI/WaitForCondition, CustomActivity, AI/LLM und Subworkflows; Config→Script/Transport→Output, Cancellation, Persistenzredaktion |
| Engine PowerShell (12 C#-Dateien) | Implementation vollständig frisch gelesen | Runspace/Process Engines, Launcher/Jobs/SafeHandles, Wrapper/Operation/ActivitySupport, Factory, Interface, ChildProcessEnvironment, ParameterKeyValidator, SpawnCoordinator; Marker, parallele Streams, Drain, Fault/Cancel und Ressourcenbesitz |
| Engine Execution/übrige Runtime | Implementation vollständig frisch gelesen | 13 Execution-Dateien, WorkflowEngine, Registry/DI, StepTester, Debug (2), Conditions (2), Triggers (7), Notifications (5), SMTP/Options, SingleNode/Telemetry/JSON; Graph/Ancestor-Scope, WaitAny, Queue-/Step-/Child-Kapazität, Recovery, Cache-Versionierung, Test-Identität |
| Engine Scorch (2 Dateien) | Implementation vollständig frisch gelesen | Gehärteter XML-Eingang, Mapper-Katalog/Heuristik/Fallback, Referenzen/Kind-Aufrufe, Link-Übersetzung, Outputnamen, Layout/Diagnostik; Import ist deaktiviert und meldet verlustbehaftete Übersetzung |
| Scheduler (51 C#-Dateien) | Implementation vollständig frisch gelesen; Kandidaten gegengeprüft | TriggerOrchestrator + 4 Sources + Observer/SinglePass, Lease/Leader, 8 Retention-/Cleanup-Services und Options, Stats/Maintenance/Health, NotificationDispatcher + 6 Collectors/Support, 14 SystemAlert-Sources + Evaluator/Query/Parameters/Catalog/Conditions/Interface, Schedule-Gauge und Metrics |
| Ai | **an Root delegiert** | Root liest den gesamten `src/NodePilot.Ai`-Bereich und zugehörige Tests frisch. Engine-Agent-Adapter und LLM-Consumer verbleiben in diesem Bericht. |

Domain-/ADR-Lektüre: CLAUDE, CONTEXT, Threat Model; ADR 0002, 0004, 0008, 0011,
0013–0018 und `docs/alerting.md`. ADR0008 dokumentiert nun explizit die
Unterscheidung vollständiger Instanz-Snapshots von Ereignis-/Teilabfragen.

## Security-Ergebnis

Keine Critical/High in diesem Bereich bestätigt. M61 (Persistenzredaktion), M64
(unbegrenztes Regex-Backtracking) und M68 (FileWatcher-Reparse-Grenze im
Reconcile-Pfad) wurden bestätigt, unabhängig gegengeprüft und behoben. M54 ist
ein konkreter Freigabe-/Governance-Race; daraus wird kein allgemeiner Schutz vor
vertrauenswürdigen Operator-Scripts behauptet. Keine noch offene bestätigte
Critical-/High-/Medium-Fundstelle nach den Fix-Gegenchecks; diese Gegenchecks
sind ausdrücklich keine weitere vollständige Quellenrunde.

## Architektur und funktionale Kandidaten

- **M53 bestätigt und behoben:** `StepExecution.StepType` besaß ein DB-Limit von 30 Zeichen,
  während eine gültige Custom Activity einen Key bis 64 Zeichen und den Präfix
  `custom:` verwenden darf. StepRunner speichert den vollständigen Node-Typ.
  PostgreSQL/SQL Server setzen das Limit durch; SQLite-Testschemas nicht.
  Root-Gegenprüfung bestätigt; auch `SupportEvent.ActivityType` war mit 60 Zeichen
  zu klein. Gemeinsame Key-/Type-Grenze und additive providerneutrale Migration
  erhöhen beide Persistenzfelder auf 71 Zeichen. Modellregressionen zunächst 2/2
  rot, anschließend inklusive SQL-Server-/PostgreSQL-Migrationsskripten,
  Up/Down/Up und Erhalt bestehender SQLite-Daten grün.
- **M54 bestätigt und behoben:** Custom-Activity-Änderungen prüften einen Token
  nur im Speicher. Ein zwischen Load und Save freigegebener Draft konnte danach
  noch durch den Operator überschrieben werden. Deterministische Save-Barriere
  reproduzierte den Fehler. Der vorhandene Token ist nun EF-Concurrency-Token;
  Delete/Rollback/Enable verlangen außerdem den bei der Autorisierungs-/Review-
  Lektüre gesehenen Token. Konflikte verwenden die vorhandene 409-Seam. Sechs
  weitere Store-Regressionsfälle prüfen veraltete Review-Stände und Commit-Races.
  Funktionaler Governance-Befund, kein behaupteter allgemeiner Operator-Sandbox-
  Schutz. Gemeinsamer Data-Fokus: **34/34 grün**.
- **M61 bestätigt und behoben:** ReturnData redigierte persistierte Einzelwerte ohne
  ihren Namen. Zusammengesetzte sensitive Keys wie `smtpPassword`/`accessToken`
  verloren so den vorhandenen NamedValue-Schutz. `RedactNamedValue` schützt den
  DB-Umschlag; die rohe InMemory-Datenbusausgabe bleibt funktionsfähig. Zwei rote
  Regressionen prüfen auch die spätere API-artige Redaction des DB-Werts.
- **M62 bestätigt und behoben:** drei Service-Management-SC-Operationen erzeugten
  mit `$LASTEXITCODE:` ungültige PowerShell. Drei Parser-Regressionen rot→grün;
  `${LASTEXITCODE}` behebt ausschließlich die Variablenabgrenzung.
- **M63 bestätigt und behoben:** Poll-Delay-Cancellation wurde zu Timeout/Failed.
  Ein normaler WaitAny-Verlierer ließ dadurch den ganzen Run fehlschlagen.
  Zwei Cancellation-Regressionen plus echter Engine/WaitAny-Lauf rot→grün.
- **M64 bestätigt und behoben:** beide TextFileEdit-Regex-Konstruktoren hatten
  unendliches Backtrackingbudget. Zwei isolierte Prozesse überschritten vor
  dem Fix die 5s-Außengrenze; mit 500ms Regexbudget liefern beide strukturierte
  Fehler ohne Dateiänderung. Kein Live-Service und kein hängenbleibender
  Testhost-Runspace verwendet.
- Nach M61–64: **Engine-Familienfokus 109/109 grün**, keine Skips. Ein alter
  Test verlangte die falsche Cancellation-Timeout-Form und wurde mit dem
  korrigierten Scheduler-Vertrag aktualisiert. M54 API-Controller/Backup:
  **14/14 grün**.
- **M68 bestätigt und behoben:** Snapshot/Reconcile des FileWatchers folgt nach
  Start angelegten Reparse-Points, obwohl der Eventpfad sie ablehnt. Root
  bestätigt gemeinsame Guard-Seam. Snapshot und Reconcile verwenden die
  vorhandene Reparse-freie Enumeration/Validierung; unmittelbar vor Delivery
  wird nochmals geprüft. Ein fehlgeschlagener Scan ersetzt den alten Snapshot
  nicht durch einen leeren. Zwei echte nach Start erzeugte Datei-/Verzeichnis-
  Symlinks reproduzierten den Fehler und werden jetzt abgewiesen.
- **M71 bestätigt und behoben:** SystemAlertEvaluator dupliziert Parameter-Parsing
  und lässt JsonElement in `Dictionary<string,object?>`. Numerische Source-
  Parameter scheitern an Convert und werden still übersprungen; die vorhandene
  `SystemAlertParameters.ToQuery`-Seam boxt korrekt aus. Die Duplikation wurde
  gelöscht. Zwei reale CancelRate-Source-Fälle (numerischer/stringifizierter
  60-Minuten-Parameter statt 10-Minuten-Default) rot→grün.
- **M72 bestätigt und behoben:** älteste 200 weiterhin Pending/Running Rows
  besetzten jeden Scan, bereits ausgelöste Alerts wurden dedupliziert, aber
  jüngere überfällige Runs nie besucht. Ein lokaler zyklischer Cursor über
  `(StartedAt, Id)` besucht die gesamte Menge in weiterhin begrenzten Batches.
  Zwei 205-Run-Regressionen für QueuedLong/RunningLong rot→grün.
- **M73 bestätigt und behoben:** Pending-Retries prüften den Master-Switch
  `NotificationRule.IsEnabled` nicht und versendeten nach Deaktivierung weiter.
  Recovery lädt die Regel mit und beendet solche Versuche als Failed. Ein echter
  zweiter Dispatcher-Pass nach Abschaltung reproduzierte und verifiziert den Fix.
- **M74 bestätigt und behoben:** ein aus dem Trigger-Unhealthy-Snapshot
  verschwundenes, gesundes Objekt behielt die alte Episode; erneuter Ausfall
  erzeugte denselben deduplizierten EventKey und blieb still. Das vorhandene
  Source-Interface deklariert jetzt `MissingInstancesAreHealthy` (Default false);
  nur die vollständige Trigger-Quelle setzt true. Erfolgreiche vollständige
  Snapshots beenden fehlende Instanzen. Leader mit leerem gesundem Registry ist
  verfügbar, passive HA-Nodes bleiben unavailable. Zwei Episodenregressionen,
  Leader/Follower-Availability und partielle/unavailable Gegenfälle sichern die
  unterschiedliche Semantik ab. Kein neues Markerinterface.
- **M76 bestätigt und behoben:** zwei Creates konnten dieselbe Key-Vorprüfung
  passieren. Die doppelte freigegebene Definition ließ den AI-Workflowchat beim
  `ToDictionary(custom:key)` abbrechen (Root-Consumer-Gegenprüfung). Ein
  gefilterter Unique-Index schützt live Keys einschließlich Drafts; Tombstones
  bleiben mehrfach zulässig. Store übersetzt nur die konkrete Key-Constraint,
  Import überspringt den konkurrierenden Verlierer und arbeitet weiter, Restore
  rollt bei diesem Konflikt zurück und fordert ein neues Preview/Retry.
  Vorhandene Duplikate werden weder gelöscht noch umbenannt: Migration stoppt
  mit Diagnose und read-only Abfrage in `docs/custom-activity-key-migration.md`.
  Echter Create-Save-Race rot→grün; native Scripts für drei Provider,
  Bestandsduplikat-Erhalt, Tombstones und Import-/Restore-Verhalten grün.
- Gegenargument zur beliebigen Scriptwahl durch Custom-Key-Create-Races: Runtime verwendet die
  maßgebliche `__customDefinitionId` mit Key-Abgleich und nicht die erste
  `GetByKeyAsync`-Zeile. Eine behauptete unmittelbare beliebige Scriptwahl wurde
  daher verworfen. Der tatsächliche reproduzierbare Consumer-/Integritätsfehler
  wird unter M76 behandelt.

Weitere verworfene oder nicht bestätigte Kandidaten:

- **M70 nur Kandidat:** synchroner UNC-Reconcile kann bei blockiertem Dateisystem
  Shutdown verzögern. Kein kontrolliertes Repro einer mittleren Betriebsstörung;
  ein Timeout um unkündbares Sync-I/O würde Arbeit und Ressourcen weiterlaufen
  lassen. Keine neue Scanworker-Infrastruktur und kein behaupteter Fix.
- WorkflowDefinitionCache nach Restore: Restore erhöht Version/UpdatedAt; der
  vermutete veraltete gleiche Cachekey ist damit nicht belegt.
- DebugHandle-Exception vor Engine-Catch: Dispatch terminalisiert im generischen
  Catch auch Running, trotz irreführendem Methodennamen. Kein belegter hängen-
  bleibender normaler Dispatch-Run.
- Runtime-Registry-Overload mit eigenem kurzlebigem Scope: Produktion benutzt den
  Provider-Overload; die andere Form erscheint in Tests. Kein belegter Medium-
  Lifecycle-Fehler.
- Execution-Retention mit Archivfehler: bewusst dokumentierte Best-effort-
  Semantik; Audit-Retention hat den strengeren Fail-closed-Vertrag. Nicht als
  unbeabsichtigter Datenverlust umgedeutet.
- VariableResolver mit sequentiellen Ersetzungen: mögliche Interpretation eines
  späteren Platzhalters im ersetzten Wert blieb ohne konkreten verletzten
  Literalvertrag/Repro unbestätigt. Kein Medium und keine semantische Änderung.
- Scorch-Übersetzungsverlust ist sichtbar gewarnt und Import bleibt deaktiviert.
  Trusted-Operator-Script-/Remote-Ausführung ist kein Sandbox-Bypass.
- Reine Pool-Shutdown-/Metrik-Härtung, seltene doppelte Fehler im Task-Fehlerpfad
  und invalide Admin-Condition-JSON mit 500 wurden nicht ohne belastbare mittlere
  Auswirkung hochgestuft.

Abschließende fokussierte Validierung:

- Data **43 bestanden / 2 übersprungen** (nur unkonfigurierte echte PostgreSQL/
  SQL-Server-Instanzen). Native Scripts, SQLite-Migrationen und Driftchecks liefen.
- API Custom-Activity-/Backup-Familien **16/16 bestanden**, keine Skips.
- Scheduler-/Alerting-/FileWatcher-/Parity-Familien **184/184 bestanden**, keine
  Skips. Die neun neuen Repro-Fälle waren vorher sämtlich rot.
  Anschließend ergänzte Partial-/Unavailable-Gegenfälle **2/2 bestanden**.
- Engine-Aktivitätsfamilien M61–64 **109/109 bestanden**, keine Skips.

Deletion test bisher: SecretEnvelope, Protector-Seam, Availability-Tracker,
OutboxClaim und TreeLock konzentrieren echte geteilte Invarianten; Entfernen
würde die Komplexität auf mehrere Consumer verteilen. NoOp und WinRM sind echte
Adapter. Kein Refactoring allein wegen Dateigröße oder Dateizahl vorgeschlagen.
Auch SubWorkflowGateLease/Participant, PowerShell Operation/Wrapper, QueryPayload,
Notification-Collector und LeaderGatedRetention besitzen konkrete gemeinsame
Invarianten. Die doppelte SystemAlert-Parameterkonvertierung dagegen hat bereits
eine messbare Consumer-Abweichung (M71); ihre Löschung zugunsten der vorhandenen
Seam ist eine begründete Vertiefung.

## Grenzen

Builds wurden mit Root und den übrigen Agents serialisiert. Vollständige eigene
Produktionslektüre ist in der Matrix aufgeführt; zugehörige Testfamilien wurden
inventarisiert und an den Consumer-/Regression-Seams gelesen. Es wird keine
vollständige Body-Lektüre sämtlicher Testdateien behauptet. Neue relevante Tests:
CustomActivityDefinitionStore/KeyConstraint, MigrationDrift/ProviderMigration,
ControlFlow/ServiceManagement/WaitForCondition/TextFileEdit, WorkflowEngine,
FileWatcherTriggerSource, NotificationDispatcher, SystemAlertEvaluator,
TriggerUnhealthySource und API Custom-Activity-/Backup-Tests.

Native DB-Provider wurden als SQL-Generator geprüft, nicht live ausgeführt.
WinRM, Cluster mit mehreren Hosts, UNC-Netzwerkfehler und externe Dienste wurden
nicht live getestet. Vendorte Archive-PowerShell-Implementierung bleibt eine
Drittanbietergrenze; Manifest, Einbindung und NOTICE wurden geprüft, ihr gesamter
Scriptbody nicht neu sicherheitsauditiert. Der Root führt die projektweiten
Release-Suiten separat; diese werden hier nicht mit fokussierten Runs verwechselt.

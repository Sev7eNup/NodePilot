# Runde 2 – Engine, Scheduler, Data, Remote, Telemetry

Stand: 2026-10-09. Eigenständiger zweiter Rundgang mit `improve-codebase-architecture` und `audit-ai-slop-code`; nicht auf den Diff aus Runde 1 beschränkt. Der abschließende Nullbefund des Gesamtprojekts ist erst nach Gegenprüfung aller Teilberichte möglich.

## Maßstab und tatsächlicher Umfang

Gelesen: CLAUDE.md, CONTEXT.md, LANGUAGE.md des Architektur-Skills, Threat Model sowie die einschlägigen ADRs zu Ausführung, vertrauenswürdigen Autoren, Folder RBAC, Aktivitätsverträgen, Agent-Rechten und aktiver Subworkflow-Kapazität (0018). Inventar: `rg --files` über die fünf Projekte, anschließend Quelllektüre entlang der Eintrittspunkte, Ressourcenübergaben, Datenbankmutationen, Abbruchpfade und Sicherheitsentscheidungen. Bestehende Tests wurden den jeweiligen Verträgen zugeordnet. Generierte Designer/Snapshots wurden mit dem Laufzeitmodell auf Schlüssel/Constraints/Provider-Besonderheiten abgeglichen, nicht wie handgeschriebene Geschäftslogik zeilenweise bewertet. Das gebündelte Microsoft.PowerShell.Archive wurde auf Herkunft, Einbindung und Startverhalten geprüft; keine neue Vollprüfung des Fremdpakets.

Es handelt sich um eine vollständige Modulgruppenprüfung mit unterschiedlicher Detailtiefe, nicht um den Anspruch, jede mögliche Eingabekombination erschöpfend bewiesen zu haben. Keine der unten genannten Modulgruppen wurde nur anhand ihres Dateinamens freigegeben. Große Mapper, eingebettete PowerShell-Texte und generierte Modelle wurden über ihre Eingänge, gefährlichen Operationen, Validierung und bestehende Vertragsregressionen geprüft.

Vertrauensannahmen: Eine administrative Domäne, vertrauenswürdige normale Skriptautoren/Operatoren. Normaler `runScript`, globale Credentials und administrative Remote-Verwaltung sind beabsichtigte Fähigkeiten, keine vermeintliche Mandantensandbox. Agent-Aufrufe sind dagegen vom Host auf freigegebene Leseoperationen begrenzt. Prozessisolation ist Ressourcen-/Prozessbegrenzung, keine Windows-Berechtigungssandbox.

## Modul- und Eintrittspunkt-Coverage

| Bereich | In dieser Runde geprüfte Eingänge und Invarianten | Ergebnis |
|---|---|---|
| Engine-Lebenszyklus | WorkflowEngine Execute/Cancel/Finalize/Capacity; StartupRecovery Single-Node und Lease-Recovery; atomare Eltern-/Step-Endzustände; WorkflowDefinitionCache; AncestorIndex/AncestorScopedResults; StepRunner Auflösung, Autorisierung, Wiederholungen und Persistenz | Runde-1-Korrekturen erneut im vollständigen Aufrufpfad geprüft; kein weiterer bestätigter C/H/M-Befund |
| Scheduler des Graphen | WorkflowScheduler Run/RunLoop/AbandonInFlight/ExecuteWithGate, Junction-Races, Abbruch und Gate-Bilanz; SubWorkflowGateLease Teilnehmerübergabe; StartWorkflow/ForEach sync/queued, Identität, Rekursion, Reload nach Kapazitätswartezeit | Keine erneute Gate-/Identitätslücke bestätigt; ADR0018 beschreibt die tatsächliche Semantik |
| Auflösung/Registrierung/Debug | ActivityRegistry und DI-Registrierung, StepTester inklusive Autorisierungssnapshot und Custom-Config, MachineResolver, VariableResolver, Conditions, RetryPolicy; DebugCoordinator/DebugHandle | TestStep-Kindworkflow-Kandidat verworfen, siehe unten; keine neue mittlere Strukturmaßnahme |
| Alle Activity-Familien | RunScript/CustomActivity/AI-Agent/AI-Team/LLM; REST/SQL/WMI; Datei/Ordner/Zip/Hash/Textedit/JSON/XML; Service/Registry/ScheduledTask/StartProgram/PowerManagement/WaitForCondition; Decision/Junction/ReturnData/GenerateText/Delay/Log/Email. Geprüft: Execute/BuildScript/PostProcess, gemeinsame BaseRemoteActivity/ActivityExecution/QueryPayloadSource/PowerShellOperation, Literalquoting, Zielpfadprüfung, erlaubte Parameter, Limits, Ergebnisstatus | Konkreter Replay-Befund in tieferem Ausführungsmodul; allgemeine Aufteilung großer Activities ohne Vertragsgewinn verworfen |
| PowerShell/Remote | EngineFactory, Runspace Execute/EndInvoke/Stop/Dispose, Process nonisolated/isolated Start/Exit/Drain/Tempfile; IsolatedProcessLauncher native Handle-/Job-Ownership; SpawnCoordinator, ChildProcessEnvironment; Wrapper/ParameterKeyValidator/ActivitySupport. WinRmSessionFactory TLS/Auth, SessionPool Leases/Invalidierung, Session Execute/Stop/Dispose, NoOp und Fehlerklassifikation | R2-E1: ungenehmigtes dreifaches Replay nach bereits erfolgter Skriptausführung bestätigt und entfernt |
| Agents | AgentActivityRunner/RunDatabase/RunJournal/Checkpoint/Gate; AgentToolHost HTTP/Workflow-Aufruf; ExternalReadPolicy/ReadOnlyWorkflowScope; PermissionPolicy AST/CMD/Bash/CIM; FileTools/LocalSharePath/ArtifactStore; SkillArchive/SkillExecution/SkillTools; MCP Serverrevision/Schema/Transport; ContentRedactor/ProcessScript | R2-E2: unvollständige HTTP-Zielpolicy bei Proxy; übrige Kandidaten an realer Host-Enforcement-Grenze geprüft |
| Engine Security/Mail/Notifications | NetworkGuard URL+Connect+DNS, RestApiHttpClientProvider direct/default/custom/bypass, PathGuard/FileWatcherPathGuard/TargetPathGuardScript, OutputRedactor/PowerShellQuoter; SMTP-Transport, beide Notification-Sinks, Renderer/RuleSemantics/SystemAlertConditionValidator | R2-E2 umfasst auch Webhook-Sink, der die Proxy-Allowlist vorher ausließ |
| Trigger | Alle Engine-Triggerexecutor-Einstiege (Manual, Webhook, Schedule, Database, EventLog, FileWatcher); Scheduler TriggerOrchestrator Registrierung/Sync/Admit/Checkpoint/Receipt/Outbox/Leasefence; alle vier Sources, SinglePassGate/TriggerFireObserver/TriggerHealthRegistry | R2-E4: Ownership-Lücke bei abgebrochenem FileWatcher-Start bestätigt |
| Background/Cluster | ClusterLeaderService Erwerb/Erneuerung/DB-Zeit/monotone Gültigkeit/Verlust; LeaderGatedRetentionService; Execution-, Audit-, Notification-, SupportEvent-, WorkflowVersion-, TriggerReceipt-Retention und IdempotencyCleanup; MaintenanceWindowSnapshotService; WorkflowStatsRefresher/SystemHealthWriter | R2-E3: begrenzte Archivprüfung besucht ohne Fortschritt nur den ersten Stapel |
| Alerting | NotificationDispatcher persist-before-send, Pending-Recovery/Retry/Leasefence; ExecutionEventSupport/-Collector, Elapsed-/LongRunning-/QueuedLong-Collector; SystemAlertEvaluator Episoden und Rekonstruktion; Catalog/Query/Parameters/Conditions/Gauge-Helper; alle 14 Quellen (Audit, DeliveryFailure, Backlog, CancelRate, Credentials, ExecutionResult, Machine, Pending, ScheduleMissed, ServiceStale, StuckExecution, TriggerUnhealthy, WorkflowHealth, NoRecentSuccess) | R2-E5: häufige Zeitpläne verbergen ausgefallene Starts; keine vermeintliche Sicherheitslücke aus beabsichtigter globaler Admin-Alarmierung |
| Data Stores | Credential/GlobalVariable/GlobalVariableFolder/CustomActivity/NotificationRule/MaintenanceWindow Store; MaintenanceEvaluator, WorkflowNameResolver/FolderTreeMutationLock; ExecutionStateLifecycle, DispatchOutboxClaimer, WebhookReplayStore; ExecutionLog/OperationalKnowledge Reader | Parameterisierte Abfragen, Konflikt-/Transaktionsgrenzen und Fail-Closed-Secretauflösung geprüft; kein weiterer bestätigter C/H/M-Befund |
| Data Modell/Migration/Secrets/Availability | NodePilotDbContext/AgentModelConfiguration Schlüssel, FK-Löschverhalten, Refresh-/SecurityStamp-Konflikte, Outbox-/Receipt-Constraints; MigrationBootstrapper/Portability und alle drei Migrationen; SecretEnvelope, Registry/Bootstrap, AES-GCM/DPAPI/Passphrase/Migrating; AvailabilityTracker/ConnectionString/Budget/Interceptors/BreakerStrategies/DbErrorClassifier | Keine neue Constraint-/Krypto-/Breaker-Lücke bestätigt; reale Provider-Upgrades und Failover separat unverified |
| Telemetry/Infra | PrometheusClient, OpenTelemetryExtensions, SerilogTelemetryBridge, Options/ResourceIdentity; Engine ActivityTelemetryAllowList und Metrik-Tags; csproj-Abhängigkeitsrichtung/DI, Directory.Build.targets und PowerShell SDK/Archive-Ausgabe, TestCommons und zugehörige Testfamilien | Keine neue C/H/M-Maßnahme begründet; optionale Telemetrie und admin-konfigurierte Exportziele bleiben erhalten |
| SCOrch | Import XML-Reader/DTD-Grenzen, globale Variablen/verschlüsselte Platzhalter, Child-Referenzen; ActivityMapper Map/EnforceContract/disabled Fallback und relevante Builder | Kein ausführbarer stiller Fallback: unbekannte/unvollständige Mappings bleiben sichtbare deaktivierte Platzhalter |

## Critical

Keine in dieser Runde bestätigt.

## High

Keine in dieser Runde bestätigt.

## Medium

### R2-E1 – Transportschicht wiederholt bereits ausgeführte Skripte

`RunspaceExecutionEngine.ExecuteAsync` und `WinRmSession.ExecuteScriptAsync` wiederholten eine gesamte Invocation bis zu dreimal, sobald deren Fehlertext `Collection was modified` enthielt. Damit konnte ein Workflow mit deaktivierter RetryPolicy trotzdem drei fachliche Seiteneffekte ausführen. Vier echte PowerShell-Regressionsfälle schreiben vor `throw` beziehungsweise `Write-Error` ein Zeichen in eine Datei: vor Korrektur stand `xxx` statt `x` (4/4 rot). Ursache war doppelte Retry-Verantwortung zwischen StepRunner und Transport plus ein Fehlertext als vermeintlicher Startfehlernachweis.

Korrektur: Transport führt eine Invocation aus; nur explizite übergeordnete RetryPolicy darf sie wiederholen. Eager Module-Import, Runspace-Pool und Abbruch bleiben bestehen. Ein echter Importfehler vor Nutzerinvocation könnte separat behandelt werden; kein Beleg rechtfertigte hier ein fachliches Replay. Der bestehende reine Substring-Classifier-Test entfiel zusammen mit dem toten Helper. 418 PowerShell/Remote/Retry-Tests grün.

Alltag: Ein Skript erstellt einen Benutzer und scheitert danach beim Auflisten einer Sammlung. Zuvor liefen Erstellung und Folgeschritte dreimal; nun wird der tatsächliche Fehler nach einem Versuch gemeldet.

### R2-E2 – HTTP-Sicherheit war auf mehrere Aufrufer verteilt

`AgentToolHost.RequestAsync` und `AgentMcpClientFactory.ConnectAsync` riefen nur `RestApiHttpClientProvider.ValidateDestinationPolicy` auf. Der Provider prüfte ausschließlich exakte Hostfreigaben bei Proxybetrieb. Anders als RestApiActivity fehlte dort NetworkGuards unverzichtbare Link-local-Sperre. Bei einem explizit freigegebenen Metadatenziel oder gemischtem DNS mit Link-local-Adresse erreichte der Proxy den bisher nur vom normalen Activity-Eingang abgesicherten Pfad. Der ConnectCallback sieht beim Proxy lediglich dessen Adresse. WebhookNotificationSink hatte das umgekehrte Teilproblem: NetworkGuard, aber keine exakte Proxy-Zielfreigabe.

Korrektur: Provider besitzt jetzt die vollständige URL-/Adress-/Proxy-Zielentscheidung. REST initial/redirect, Agent HTTP/MCP und Webhook nutzen dieselbe Grenze. Neue Regressionen: literal metadata, gemischtes DNS, echte Agent-Tool-Aufrufkette vor Send, Webhook ohne Proxy-Freigabe; erlaubte private Dienste bleiben positiv getestet. Unabhängiger API-Agent-Gegenreview bestätigt direct/bypass/allowlisted-private Semantik. Proxy-eigene DNS-Abweichungen bleiben eine explizite Vertrauensgrenze, kein behauptetes lokales IP-Pinning.

Alltag: Ein Agent soll über den Firmenproxy einen freigegebenen Dienst abfragen. Die Hostfreigabe darf ihn nicht nebenbei an die Cloud-Metadatenadresse lassen. Diese Ausnahme ist nun an einer Stelle ausgeschlossen.

### R2-E3 – Archivprüfung prüft dauerhaft nur die ältesten Dateien

`AuditLogRetentionService.VerifyArchiveIntegrityAsync` sortierte alle Archive und nahm jedes Mal nur die ersten `maxFiles`, ohne Fortschritt. Spätere Archive wurden entgegen dem Methodenvertrag niemals geprüft. Begrenzung ist sinnvoll; fehlende Fortschrittsverantwortung war es nicht.

Korrektur: stabiler Cursor aus Änderungszeit und Name pro Archivverzeichnis, begrenzter nächster Stapel und erneuter Beginn nach einem vollständigen Zyklus. Gleiche Zeitstempel und verschwundene Cursor-Dateien blockieren den Fortschritt nicht. Regression mit zwei Archiven, Limit 1 und drei Durchläufen verlangt A → B → A. Hashdrift bleibt Warnung/Metrik, die Archiv-/Löschreihenfolge wurde nicht geändert. Cursor ist absichtlich Prozesszustand; Neustart beginnt wieder vorn.

Alltag: Beim täglichen Prüfen eines großen Belegarchivs wurden immer dieselben ersten Belege kontrolliert. Jetzt kommen auch die späteren dran, anschließend werden die alten erneut geprüft.

### R2-E4 – Abgebrochener FileWatcher-Aufbau verliert Ressourcenbesitz

`FileWatcherTriggerSource.RunBoundedAsync` entsorgte verspätete Resultate nur nach Timeout, nicht nach Caller-Cancellation. Ein langsamer UNC-Start konnte nach Leadership-Verlust oder Shutdown noch einen aktivierten Watcher erzeugen, der nie in `_watcher` veröffentlicht wurde. Die vorhandene Identitätsprüfung verhindert fremde Triggerzustellung, aber entsorgt nicht die OS-Ressource. Auch Snapshot-/Arm-Fehler nach Konstruktion und Abbruch vor der Übergabe an `_deliveryGate` benötigten lokale Entsorgung.

Korrektur: dieselbe späte Cleanup-Übernahme für Timeout und Cancellation; Dispose bei fehlgeschlagenem Snapshot/Arming sowie vor fehlgeschlagener Übergabe. Deterministischer Barrierentest hält den Erzeuger an, bricht den Caller ab, gibt den Erzeuger frei und verlangt genau eine Entsorgung. Keine Änderung an Zustellung, Debounce oder Checkpoint-Semantik.

Alltag: Beim Serverwechsel antwortet ein Netzlaufwerk verspätet. Ein bereits abbestellter Ordnerwächter blieb früher im Hintergrund übrig; nun wird er nach seiner Rückkehr aufgeräumt.

### R2-E5 – Häufige Zeitpläne verbergen ausgefallene Starts

`ScheduleMissedSource` suchte den jüngsten Termin vor jetzt. Bei einem minütlichen Zeitplan und fünf Minuten Grace liegt dieser Termin immer innerhalb der Grace: der Alarm bleibt auch bei einem Ausfall seit gestern stumm. Bei explizit erlaubten Sub-Minuten-Zeitplänen kam hinzu, dass die Vorwärtsiteration ab jetzt minus 48 Stunden nach 10.000 Terminen abbrach und einen viel zu alten Termin zurückgab. Ein alter Lauf konnte die Prüfung dann fälschlich erfüllen.

Beweis: Vier ObserveAsync-Fälle mit realer SQLite-Persistenz, Cron alle fünf Sekunden beziehungsweise jede Minute, letzter Lauf gestern beziehungsweise vor zwei Minuten. Vor Korrektur waren beide erwarteten Alarme rot, beide rechtzeitigen Läufe grün. Korrektur: letzten bereits überfälligen Termin vor `now - grace` prüfen. Die bereits gepinnte Quartz-Version 4.2.1 besitzt `GetPreviousValidTimeBefore`; sie ersetzt die fehlerhafte eigene Vorwärtsiteration, das 48h-Lookback bleibt bestehen. Drei weitere Fälle prüfen dichte Termine, einen täglichen Termin und einen außerhalb des Fensters liegenden monatlichen Termin. 93 SystemAlert-/Schedule-Tests grün.

Alltag: Ein Kontrollworkflow soll jede Minute laufen. Ein neuer Solltermin durfte die fünfminütige Alarmwartezeit bisher immer wieder verdecken. Nach fünf Minuten ohne rechtzeitigen Lauf wird der Ausfall nun sichtbar.

## Low / ausgeschlossene Kandidaten

- ActivityRegistry-Einargument-Overload könnte einen Executor aus einem schon entsorgten Scope zurückgeben. Produktionsaufrufer StepRunner/StepTester benutzen den scopedProvider-Overload; kein belegter produktiver Laufzeitfehler. Keine zusätzliche Interface-Schicht angelegt.
- StepTester erhält keine CallerId: StartWorkflow/ForEach werden vor Ausführung ausdrücklich abgelehnt; ein Agenttest scheitert beim Journalstart am fehlenden persistierten Parent, bevor Tools geöffnet werden. Kein Publisher-Privilege-Borrowing über diesen Eingang bestätigt.
- Normale Skripte, frei formuliertes SQL und globale Credentials sind Fähigkeiten des dokumentierten vertrauenswürdigen Autorenmodells. Agent-Shells werden dagegen durch AST-/Parameter-/Toolprüfung begrenzt. Kein künstlicher C/H/M-Befund aus absichtlichem Funktionsumfang.
- Große Activity-, Mapper- und Query-Helper allein rechtfertigen keine Severity. Gemeinsame bestehende Grenzen (PowerShellOperation, BaseRemoteActivity, QueryPayloadSource, NotificationRuleSemantics, ConditionEvaluator) tragen echte Verträge. Zusätzliche pass-through Services hätten nur Navigation und Testkopplung erhöht.
- Best-effort Execution-Archivierung und fail-closed Audit-Archivierung sind absichtlich unterschiedliche Produktverträge; keine pauschale Zusammenlegung.
- Debug-Metrikdrift bei sehr frühem DB-/Notifierfehler, unbenutzte/selten genutzte Helper und kosmetische Kommentar-/Längenthemen erreichen ohne erhebliche Auswirkung keine mittlere Severity.
- Dateisystem-TOCTOU durch gleichzeitig schreibenden vertrauenswürdigen Systemadministrator ist nicht als neue sichere Sandbox-Escape behauptet; Pfadvalidierung/ACL-Vertrauensgrenze bleibt ausdrücklich beschrieben.

## Validierung und offene Nachweise

- R2-E1: 4 deterministische Regressionen rot → grün; 418 Tests in PowerShell/Remote/Retry bestanden. WinRmSession-Fälle verwenden den bestehenden lokalen Runspace-Seam, keinen entfernten WinRM-Server.
- R2-E2/E3/E4 erster gezielter Lauf: 603 bestanden, 7 Fehlschläge ausschließlich durch bisher gemockte `.test`-DNS-Namen in Agent-Tests. Diese Fixtures wurden auf den vorhandenen AsyncLocal-DNS-Testseam umgestellt; keine produktive Policy-Ausnahme. Abschließender Lauf: 612 bestanden, 0 fehlgeschlagen, 0 übersprungen (RestApi/NetworkGuard/Agent/Notification/FileWatcher/AuditLogRetention).
- R2-E5: 2 Regressionen rot, 2 positive Gegenfälle grün vor Fix; danach 93 SystemAlert-/Schedule-Tests bestanden. Kein Timeout erhöht und kein Retry eingebaut.
- `git diff --check` ohne Whitespacefehler; lediglich bestehende LF/CRLF-Hinweise in Runde-1-Dateien.
- Nicht live ausgeführt: echte UNC-Netzunterbrechung, Zwei-Host-Clusterfailover, SQL-Server-/Postgres-Upgrade, externer WinRM-Server und echter Forwardproxy. Aussagen dazu beruhen auf Quellpfaden/Regressionseams, nicht einem vorgetäuschten Integrationstest.

## Architektur- und Sicherheitsfazit dieser Runde

Architektur: Drei konkrete Ownership-Probleme (Invocation-Retry, Archivfortschritt, Watcher-Lebensdauer), die zu flache HTTP-Policy-Schnittstelle sowie fehlerhafte eigene Terminermittlung statt vorhandener Kalenderfunktion. Die Korrekturen verschieben Verantwortung zur Stelle, die den Zustand tatsächlich besitzt; keine kosmetische Modularisierung.

Security: Ein bestätigter mittlerer HTTP-Policy-Befund, außerdem unvollständige Integritätsprüfung als Zuverlässigkeitsproblem einer Sicherheitskontrolle. Kein neuer Critical/High-Befund. Die fünf bestätigten mittleren Befunde sind korrigiert und gezielt geprüft; kein weiterer offener C/H/M-Kandidat in diesem Scope. Weil diese Runde Befunde erzeugte, ersetzt ihr Abschluss keinen neuen unabhängigen projektweiten Nullrundgang.

## Ergänzender Runde-2-Auftrag: API Dispatch, Diagnostik, Logging und Health

Dieser nachträglich übernommene Bereich wurde vollständig anhand seiner aktuellen Quellen geprüft, nicht nur anhand des Branch-Diffs. Grundlage sind beide Skills, CONTEXT, das Vertrauensmodell und ADR 0009/0010/0011/0014. Der Umfang erweitert die oben abgeschlossene Engine/Data-Runde; er behauptet keinen dritten Projektrundgang.

| Module / Quellen | Tatsächlich geprüfte Eingänge und Verträge | Ergebnis |
|---|---|---|
| ExecutionDispatchService, Worker, Signal/CallbackRegistry, Options, Outcome | Admission mit verschlüsseltem Outbox-Intent, externer Commit-Wakeup, atomare Claim-Seam, Pending/Running-Ownership, Abbruch/Outage/Lease-Recovery, Maintenance- und Concurrency-Gates, aktuelle Principal-Rechte; manuelle/debug/retry-Caller in ExecutionsController | R2-E6 Medium; Rechteentzug bei interaktiven Pending-Läufen wurde nicht revalidiert |
| SupportEventChannel, SupportEventDbSink, SupportEventFlushService | Nicht blockierende Aufnahme, strukturierte Projektion, Batch-Ownership, Datenbankausfall, Recovery-Signal, Verlustzählung, Shutdown/Dispose | Best-effort ist bewusster Vertrag; Overflow-Zählung hatte einen separaten Low-Fehler |
| SupportLogFileResolver, DiagnosticsController, DiagnosticsDtos | Alle vier HTTP-Eingänge: Tail, Tagesdownload, strukturierte Events und CSV/NDJSON-Export; Admin-Attribut, DateOnly-Pfadbildung, EF-Filter, Cursor/Sortierung, Limits und CSV-Escaping-Seam | Keine Auth-/Traversal-Lücke bestätigt; Dateisegment-Auswahl als Low korrigiert |
| LogFormatters, CmTraceFormatter, EcsJsonFormatter, SupportLogFormatter, OtelTagEnricher; Hosting/LoggingSetup und LoggingFormatBootValidator | Bootstrap/Host-Konfiguration, Formatwahl, Rolling/Retention, Support-Sink-Filter, Tag-Allowlist, Escaping/Typabbildung, bekannte Normalisierungskollisionen | Format-Whitespace als Low bestätigt; keine zusätzliche C/H/M-Strukturmaßnahme |
| DatabaseReadyHealthCheck, LdapHealthCheck und Program-Health-Routen | Armed/Unavailable ohne DB-Zugriff, begrenzter SELECT-1-Readiness-Test, Cancellation/CommandBudget-Restore; anonyme Statusantworten, separate Directory-Health zur Erhaltung lokaler Notfallanmeldung | Keine sensible Health-Ausgabe oder neue C/H/M-Lücke bestätigt; Standard-HealthWriter veröffentlicht keine Exception-/Endpointdetails |

### R2-E6 — Medium: entzogene Run-Rechte gelten nicht für interaktive Warteschlangeneinträge

`ExecutionDispatchService.ValidateEffectivePrincipalAsync` aktualisierte Aktivität und Directory-Freshness für bekannte Benutzer, prüfte Folder-/Run-Rechte aber nur bei `automated`. Ein Operator konnte einen manuellen/debug/retry-Lauf vorlegen; wurde ihm während der Wartezeit das Folder-Recht entzogen oder seine globale Rolle auf Viewer gesetzt, lief der Workflow trotzdem an. Der normale API-Aufruf speichert die aktuelle Caller-ID; lange Wartezeiten sind durch volle Worker oder den Workflow-Concurrency-Limit regulär möglich.

Gegenprüfung: Die Admission-Berechtigung ersetzt keine aktuelle Dispatch-Autorisierung; die vorhandenen Deaktivierungs-/Freshness-Prüfungen und ADR 0009 setzen diese Entscheidung bereits voraus. Null-Caller in historischen interaktiven Intents bleiben bewusst unverändert; automatisierte Intents ohne Principal bleiben gesperrt. Kein Feature wird entfernt.

Korrektur: Die bestehende Principal-Seam prüft `ResourceOp.Run` für jeden vorhandenen effectiveUserId anhand der aktuellen Rolle und des aktuellen Workflow-Ordners. Kein zusätzlicher Policy-Wrapper. Sechs deterministische Regressionen (manual/debug/retry × Demotion/Folderentzug) scheiterten vor dem Fix wegen tatsächlicher Engine-Aufrufe; fünf positive bzw. automatisierte Gegenfälle bestanden. Alle elf Fälle sind nach dem Fix grün.

### Niedrige und ausgeschlossene Kandidaten des Ergänzungsbereichs

- Bestätigt, minimal korrigiert: Channel `DropWrite` ließ `TryWrite` trotz verworfenem Element erfolgreich erscheinen. `Wait`-Modus mit ausschließlich nicht blockierendem `TryWrite` erhält die bisherige Drop-newest-Semantik und meldet den Verlust an den vorhandenen Zähler. Voller Puffer, unveränderte ältere Einträge und erneute Aufnahme nach Drain sind als Regression erfasst.
- Bestätigt, minimal korrigiert: Boot-Validator akzeptiert außenliegende Whitespaces; der Formatter normalisierte sie nicht und wählte Text statt des konfigurierten strukturierten Formats. Beide interpretieren die Einstellung jetzt gleich; Regressionen für CMTrace, JSON und ECS-JSON.
- Bestätigt, korrigiert: Der Tagesdatei-Resolver ignorierte `_001`-Folgesegmente und verwendete UTC für den lokalen Rolling-Tag. Text-Tail/Download konnten dadurch unvollständig sein. Der Resolver liefert jetzt sämtliche exakt zugehörigen Tagessegmente in numerischer Reihenfolge. Der Tail besucht neueste Segmente zuerst, hält nur die noch fehlenden Zeilen in einer Ringqueue und beendet sich bei höchstens den angeforderten 1000 Zeilen. Ältere Segmente bleiben dann ungelesen. Der neue interne `SupportLogReadStream` verkettet Download-Dateien ohne Contentpuffer und mit höchstens einem offenen File. Der Download bleibt `text/plain` und enthält ältere Segmente weiterhin. Tests prüfen `_002/_010/_1000`, falsche Suffixe/fremde Tage, segmentübergreifenden Tail und vollständigen Download sowie Abbruch/Dispose mit exklusivem Wiederöffnen unter Windows. Der strukturierte Hauptviewer war unabhängig verfügbar; deshalb Low.
- Nicht als C/H/M eingestuft: Der Support-DB-Sink ist keine zweite globale Geheimnis-Redaktion; LogActivity redigiert vor dem Sink, der Viewer ist Admin-only. Kein konkreter unberechtigter Geheimnisabfluss aus den geprüften Quellen nachgewiesen.
- Nicht als C/H/M eingestuft: ECS-Exception plus zusätzliche `error.*`-Properties könnte doppelte Root-Schlüssel erzeugen, aber kein aktueller produktiver Aufrufpfad mit dieser Kombination bestätigt. Keine spekulative Refaktorierung.
- Deletion test: Worker/Signal/Outbox-Seam kapseln verschiedene Laufzeiten und durable/process-local Zustände. Ihre Zusammenlegung würde Implementierungswissen an Admission-Caller verschieben. Formatter sind echte Adapter verschiedener Ausgabeformate. Ähnliche EF-Filter in Events/Export rechtfertigen ohne Semantikfehler keine neue öffentliche Query-Abstraktion.

Validierung: 200/200 Tests, 0 Skip/Fail im gebündelten Release-Lauf für ExecutionDispatch, Logging, Diagnostics, DatabaseReadyHealthCheck, LdapHealthCheck und die parallel vom Root ergänzten acht AuthenticationGroupOverrideTests. Vorhandene Tests für Claims/Ownership/Concurrency, Support-Batching/Recovery, Formatter/Escaping, Diagnostics-Tail/Export/Cursor und Readiness wurden den Eintrittspunkten zugeordnet. Kein echter HA-Failover, produktiver SIEM-Ingest oder externer LDAPS-/DB-Outage wurde für diesen Anhang ausgeführt. Root-Gegenprüfung des Dispatch-Befunds erfolgte vor dem Fix; Root-Gegenprüfung des Rollfile-Patches bestätigte die Stream-/Resolver-Semantik und verlangte die oben beschriebene Tail-Lesebegrenzung. Deren zusätzlicher Test mit exklusiv gesperrtem, nicht benötigtem Altsegment wartet noch auf den nächsten gebündelten API-Lauf.

Ergänzungsfazit: Ein zusätzlicher Medium-Befund und drei sinnvolle Low-Korrekturen, kein neuer Critical/High. Die neue Stream-Seam besitzt tatsächlich die sequenzielle File-Lebensdauer; sie ist keine allgemeine Stream-Abstraktionssammlung. Kein weiterer bestätigter C/H/M-Kandidat in der vollständig geprüften Restfläche.

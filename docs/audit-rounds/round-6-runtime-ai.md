# Runde 6 – Runtime, AI, Data, Scheduler, Remote und Telemetry

Stand: 2026-10-12, Branch `audit/practical-impact-2026-10-12`, Basis `de71058` plus laufende Auditkorrekturen. Unabhängiger, rotierter Quellenreview mit `improve-codebase-architecture` und `audit-ai-slop-code`. Maßstab: Critical/High und Medium mit konkreter alltäglicher Auswirkung. Trusted-Operator-Modell aus CONTEXT/Threat Model; ADR 0017/0018 erneut gelesen, übrige Architektur-/Agentverträge aus Runde 5 berücksichtigt.

## Ergebnis

**Ein neuer funktionaler Medium-Befund: #142, behoben und unabhängig gegengeprüft.** Kein neuer bestätigter Critical-/High-Sicherheitsbefund. Diese Runde ist wegen #142 eine Findings-Runde, kein Nullfundnachweis. Die separate abschließende Integrationsrunde steht in [Runde 7](round-7-runtime-ai.md).

## Tatsächliche Abdeckung

Alle sechs Projektfamilien wurden frisch inventarisiert und risikobasiert anhand von Entry Points, Zustandsübergängen und Boundary-Implementierungen geprüft. Das ist ausdrücklich keine Behauptung, jede Zeile jedes generierten Modells oder jeder Activity gelesen zu haben. Methoden-/Guard-Inventare wurden zur Auswahl benutzt, nicht als Ersatz für die unten bezeichneten Körperprüfungen.

| Modulgruppe | Frisch gelesene zentrale Körper und geprüfte Verträge |
|---|---|
| Engine-Ausführung | WorkflowEngine: Claim, Start, globale Werte, Abschluss und dauerhafte Terminalisierung; StepRunner: Running-Barriere vor Seiteneffekt, Kontext, sichere Configauflösung, Debugänderungen und redigierte Persistenz; WorkflowScheduler: RunAsync, RunLoop, Junction-Auswertung; SubWorkflowGateLease und SubWorkflowInvocation vollständig. |
| Activities | Inventar aller Activities; BaseRemoteActivity-Ausführung, RunScript lokal/remote, SQL-Execute inkl. Parameter und Zeilenlimit, REST-Execute/Redirect, StepTester-Autorisierungssnapshot; gezielte Zip-Manifest-/Commit-/Cleanup- und Subworkflow-Kapazitätspfade. Keine vollständige erneute Zeilenprüfung aller erzeugten PowerShell-Skripte. |
| PowerShell/Debug/Conditions | Prozessstart, Abbruch, inkrementelle Ausgabeerfassung und DrainReads; Templateauflösung mit qualifiziertem Parameterbesitzer; Debug-Pause/Resume/Cleanup; ConditionEvaluator-Precedence und Regex-Zeitgrenze. Methoden-/Fehlerinventar der weiteren PowerShell-Adapter. |
| Engine-Agenten | AgentActivityRunner, AgentTarget, AgentToolHost und ReadOnlyWorkflowScope vollständig; PermissionPolicy-Parser und kanonischer Befehlaufbau, FileTools-Pfad-/Reparse-/Transfergrenzen, ArtifactStore vollständig; Skill-/MCP-/Journal-Gates als Boundary-Trace. |
| Netzwerk/Import/Benachrichtigung | REST-Provider-Verbindungsaufbau mit IP-Pinning, Proxy-Destination-Policy und Redirect-Revalidierung; SCOrch-XML-Readergrenzen; Mail-/Notification-Methoden und Header-/URL-Guards. |
| AI/LLM | AgentRuntime-Finalisierung und GuardedFunction, LlmChatClientAdapter-Budget/Retry/Truncation, AgentConclusion-Parse/Prompt; AgenticToolLoop und WorkflowDefinitionMerge vollständig; Responses-Parse/FinishReason, Transport-Größen-/Timeout-/SSE-Pfade, EndpointGuard vollständig. Team-/Context-/Evidence-Familien anhand Limits und Lebenszyklus-Seams. |
| Knowledge | KnowledgeFileSearch-Pfadauflösung, Corpus-Fehlergrenzen, KnowledgeChatToolRegistry tatsächlicher Dispatch/erneutes Gate und scoped Workflowlookup. Docs-/Source-Filter und Availability-Verträge. |
| Data | Secretprotektoren AES/Passphrase/Registry vollständig, Credential-/GlobalVariable-Lese-/Schreibkörper, CustomActivityDefinitionStore und ExecutionStateLifecycle vollständig; OutboxClaimer und FolderTreeMutationLock vollständig; AvailabilityTracker-Zustandsübergänge; OperationalKnowledge-Scope. DbContext-Schlüssel, FKs, Concurrency und Indexe gezielt. Alle Oktober-Migrationen Up/Down gelesen; ältere Baseline/Designer nur Strukturinventar. |
| Scheduler | TriggerOrchestrator-AdmitFire bis Receipt/Checkpoint/Outbox-Commit; Cluster-Lease-Erneuerung; Schedule-/Database-/EventLog-Delivery und Disposal; FileWatcher-BoundedScan/Reconcile; NotificationDispatcher-Persist/Send/Retry; SystemAlertEvaluator-Availability/Healing. Alle weiteren Collector-/Source-/Retention-Familien über Query-/Lifecycle-Inventar, gezielte Stat-/Health-/Leader-Gate-Körper. Execution-/Audit-Retention-Archiv-/Löschpfade vertieft. |
| Remote/Telemetry | WinRmSessionFactory, SessionPool und Session vollständig: Credentials, Lease, Cancellation, vergiftete Sessions; PrometheusClient vollständig; OpenTelemetry-Instrumentierung, EF-Parameterentfernung, Ressourcenidentität und Serilog-Brücke gezielt. |

## #142 – Negativer Archivpfad-Cache verhindert Wiederaufnahme

**Severity:** Medium, Funktion/Betrieb. `ExecutionRetentionService.ValidateArchivePath` speicherte nach einer fehlgeschlagenen Schreibprobe `""` in `_validatedArchivePath`. Derselbe konfigurierte Pfad wurde danach nie erneut geprüft. Ein beim Dienststart oder Bereinigungsdurchlauf vorübergehend nicht verfügbarer Archivpfad blieb damit bis Neustart/Pfadänderung deaktiviert. Auch nach Wiederherstellung wurden neue abgelaufene Ausführungen ohne Archiv entfernt.

Gegenargument geprüft: Die Best-effort-Löschung bei kaputtem Archiv ist ausdrücklich gewollt (L-16). Sie wurde **nicht** verändert. Der eigenständige Defekt war die ausbleibende Wiederaufnahme nach einer normalen vorübergehenden Störung.

Minimaler Fix an derselben Seam: fehlgeschlagene Probe nicht cachen, beim nächsten Intervall erneut prüfen; erfolgreiche Probe und bewusst leere Konfiguration weiter cachen. Keine neue Abstraktion, Hintergrundschleife oder Einstellung.

Regression `ExecutionRetentionServiceTests.RunIterationAsync_ArchiveBecomesWritable_ResumesWithoutConfigurationChange`: reale SQLite-Daten und temporärer Dateipfad; zunächst blockiert eine Datei das Archivverzeichnis, danach wird es ohne Konfigurationswechsel verfügbar. Erster Durchlauf löscht gemäß bestehendem Vertrag; zweiter archiviert die neu angelegte alte Ausführung als NDJSON und bereinigt sie. Vor Fix rot, nach Fix **18/18 Execution-/Audit-Retention-Tests grün**, keine Skips. Root hat Ursache, Test und Diff unabhängig bestätigt. Logs: `.codex-round6-retention-red-detail.log`, `.codex-round6-retention-green.log`.

## Architektur- und Sicherheitsgegenprüfung

Deletion-Test: SubWorkflowInvocation/Lease, LeaderGatedRetentionService, HTTP-Provider, Secretprotektoren und ChatToolDispatch verbergen jeweils gemeinsame Zustands-/Sicherheitsinvarianten. Löschen würde dieselben Entscheidungen in mehrere Consumer verlagern. Kein belegter alltäglicher Medium-Strukturmangel; keine künstliche neue Interface-Hierarchie empfohlen. Lange Activity-/Mapperdateien allein sind kein Befund.

Identitäts-Trace: persistierter Parent → tatsächliche Calleridentität → frische Child-Autorisierung nach Kapazitätswartezeit. Agent-Lesemodus wird im Child-Kontext weitergeführt; StepTest verbietet Subworkflow-Aufrufe ohne persistierten Parent. Agent-Shellaufrufe werden auf erlaubte AST-/Parameterformen beschränkt und kanonisch rekonstruiert. Operator-Skriptausführung unter konfigurierter Identität bleibt beabsichtigte Funktion.

Output-Trace: rohe Ergebnisse bleiben intern für Datenbusreferenzen verfügbar; StepRunner redigiert die Persistenz/Notifier-Ausgabe. Debugsnapshots redigieren benannte Werte. Knowledge liefert scoped/redigierte Definitionen; Toolverfügbarkeit wird beim Dispatch erneut geprüft. HTTP-Redirects revalidieren Ziele und entfernen nicht öffentliche Cross-Origin-Header.

Nicht als neue Befunde gewertet: dokumentierte Agent-Team-Slotfreigabe während Child-Wartezeit (ADR 0017/0018), Best-effort-Retention, fehlende Exactly-once-Garantie externer Benachrichtigungen und theoretische Dateisystem-TOCTOU ohne konkret nachgewiesenen Alltagseffekt. Keine verschärfte Trustsemantik eingeführt.

## Validierung und Grenzen

Zusätzlich frisch gelesen: `QualifiedParameterOwnerTests` (#136) und `FileWatcherSnapshotDeadlineTests` (#141), einschließlich echter Activity-Consumer bzw. deterministischer Scanbarrieren. Ihre vorhandenen GREEN-Ergebnisse wurden nicht als eigene neue Testausführung ausgegeben. In dieser Runde selbst ausgeführt: ausschließlich die oben genannten 18 Retention-Tests plus vorheriger roter Einzelfall.

Keine vollständige neue Suite, kein externer Modellprovider, WinRM-Ziel, UNC-Ausfall, SMTP-Endpunkt oder nativer SQL-Server/Postgres-Migrationslauf. Keine Dependency-Advisory-Aktualisierung in diesem Teilreview. Fremde `image-manifest.json` unberührt. Produktänderung ausschließlich #142; keine Commits/Deployments.

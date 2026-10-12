# Runde 7 – abschließende Runtime-Integration

Stand: 2026-10-12, `audit/practical-impact-2026-10-12`, finaler Produktarbeitsstand nach #142. Separater Quellenpass nach Abschluss von [Runde 6](round-6-runtime-ai.md), mit beiden beauftragten Skills: `improve-codebase-architecture` und `audit-ai-slop-code`. Scope: Engine/AI/Data/Scheduler/Remote/Telemetry; Critical/High sowie Medium mit konkreter täglicher Nutzungsauswirkung.

**Ergebnis: keine neuen bestätigten Critical-, High- oder einschlägigen Medium-Befunde in diesem Integrationsumfang.** Keine Produktänderung während Runde 7. Das ist eine risikobasierte Quellen- und Vertragsprüfung, keine erneute Vollzeilenlektüre und kein Nachweis mathematischer Fehlerfreiheit.

## Frisch nachverfolgte Integrationsketten

| Kette | Konkreter Gegencheck auf dem finalen Stand |
|---|---|
| Auftraggeber → Child → Engine | `SubWorkflowInvocation.LoadParentExecutionAsync` verwendet den persistierten Auftraggeber; Publisher-Fallback nur bei fehlender Identität. `StartWorkflowActivity` und `ForEachActivity` reichen diese Identität, Parent-ID und Tiefe weiter. Nach Kapazitätswartezeit lädt `ReloadAuthorizedChildAsync` Aktivierung und Berechtigung frisch. Der Agent-Lesemodus prüft den frisch geladenen Child erneut. |
| Child-Kapazität → Scheduler → Abbruch | `SubWorkflowGateLease` besitzt physische Slots, Scheduler-Schritte eigene Teilnehmer. Der letzte suspendierte Teilnehmer gibt frei; Resume erwirbt vor Fortsetzung. `WorkflowScheduler.RunAsync/AbandonInFlightAsync` cancelt und beobachtet alle gestarteten Schritte vor Rückkehr. ForEach begrenzt Worker, wartet `WhenAll` und gibt erworbene Workflow-Slots im `finally` zurück. |
| Agent → Tools → Child | `AgentToolHost` autorisiert beim tatsächlichen Aufruf; ausgewählte Workflow-ID und veröffentlichter Zustand werden geprüft. `workflow_run` aktiviert den ReadOnly-Scope und explizite Child-Cancellation. `AgentExecutionGate.WhileReleasedAsync` serialisiert Freigabeabschnitte. Der dokumentierte Team-Trade-off aus ADR 0017/0018 bleibt bewusst erhalten. |
| Modellantwort → Toolausführung | Responses-Incomplete wird vor Tool-Call-FinishReason klassifiziert; `LlmChatClientAdapter` verwirft eine abgeschnittene Arbeitsantwort vor Toolausführung. Transportretry ist einmalig, budgetiert und wiederholt keine Tools. AgentActivityRunner räumt Sessions/Artefakte auf und versucht den Journalabschluss mit eigenem Zeitbudget; fehlgeschlagene dauerhafte Speicherung wird nicht als Erfolg ausgegeben. |
| Prozess/Remote → Ergebnis → Abschluss | `ProcessExecutionEngine.DrainReadsAsync` liefert vorhandene Teiltexte bei Drain-Timeout und beobachtet spätere Fehler auch bei Cancellation. WinRM unterscheidet Caller-Cancel von Timeout und markiert abgebrochene Sessions als vergiftet. Der Sessionpool bindet Wiederverwendung an Ziel und Credential-Fingerprint. `WorkflowEngine.PersistTerminalStateResilientAsync` nutzt frische DB-Scope/CAS mit Owner-/Epoch-Fence und räumt verwaiste Schrittzustände auf. |
| Trigger → Outbox/Receipt → Recovery | `TriggerOrchestrator` persistiert Dispatch, Receipt und Checkpoint innerhalb der Aufnahme-Transaktion; erneute Lease-Prüfung vor Dispatch. FileWatcher-Reconcile führt den Scan innerhalb der Deadline aus und prüft Cancellation vor Änderungen. Späte Scans besitzen nur den lokalen Snapshot, keine nachträgliche Delivery-/Checkpoint-Mutation. |
| Retention → Archivstörung → Wiederverfügbarkeit | `RunIterationAsync` liest aktuelle Optionen; `PurgeOnceAsync` nimmt nur terminale alte Ausführungen. #142 lässt fehlgeschlagene Proben ungecacht. Derselbe wieder verfügbare Pfad wird im nächsten Pass geprüft und archiviert; gültiger Cache und leere Konfiguration behalten ihr bisheriges Verhalten. `ArchiveAsync` bleibt absichtlich Best-effort; keine geänderte Löschgarantie. |
| Ausgabe → Telemetrie | Remote-Spans führen Statusklasse und Byteanzahl statt roher Skriptausgabe. Engine-Redaktion bleibt an Persistenz/Notifier-Grenzen; interne Datenbuswerte werden nicht pauschal zerstört. EF-Telemetrie entfernt `db.query.parameter.*` vor Export. |

## Architektururteil und Gegenargumente

Ownership und Invarianten bleiben an vorhandenen Seams: Child-Admission/Lease, Scheduler-Drain, dauerhafte Terminalisierung, gemeinsame Transportpolicy, Agent-Dispatch und Retention-Probe. Der Deletion-Test ergibt keinen sinnvollen zusätzlichen Wrapper oder eine neue Interface-Hierarchie. Eine Veränderung nur wegen Dateilänge oder allgemeiner Duplikationsähnlichkeit wäre im aktuellen Nutzerumfang unbegründet.

Nicht als Defekt ausgegeben werden die dokumentierte Best-effort-Archivierung, externe Seiteneffekte ohne verteilte Exactly-once-Garantie, das Überleben eines ununterbrechbaren nativen SMB-Aufrufs bis zur OS-Rückkehr und absichtliche Operator-Skriptausführung. Für keinen dieser Punkte wurde ein neuer vertragswidriger täglicher Ablauf belegt.

## Nachweise und Grenzen

Der neue #142-Recoveryfall wurde in Runde 6 zunächst rot und anschließend mit **18/18** Retention-Tests grün geprüft; nach dem letzten GREEN änderten sich dort nur erklärende Kommentare. Root hat Test, Ursache und finalen Diff unabhängig geprüft. #136-/#141-Regressionen wurden in Runde 6 frisch gelesen; Runde 7 prüfte die Consumerketten erneut.

Keine neue Testsuite oder Dependency-Abfrage in Runde 7. Kein echter UNC-Ausfall, externer Modellprovider, Remotehost, Cluster-Failover oder nativer DB-Server in diesem Integrationspass. Kein Commit, Push oder Deployment. Frühere Berichte dienten zur Orientierung; obige Aussagen beruhen auf erneut geöffneten finalen Quellen.

# Runde 5 – Runtime, Scheduler, Data, Remote, Telemetry und AI

## Auftrag und Stand

- Branch: `audit/practical-impact-2026-10-12`, Ausgangspunkt `de71058`.
- Frische Quellenprüfung mit `improve-codebase-architecture` und `audit-ai-slop-code`; Änderungen seit `318ae01` werden zusätzlich betrachtet, ersetzen aber keine Modulprüfung.
- Maßstab dieser Runde: Critical/High sowie Medium mit konkreter Auswirkung im täglichen Betrieb. Low, kosmetische Änderungen und seltene Randfälle werden nicht repariert.
- Produktcode zunächst read-only. Kandidaten werden vor einer Änderung unabhängig gegengeprüft. Keine Veröffentlichung, kein Deployment, keine echten Geheimnisse oder externen Zielsysteme.
- **Risikobasierte Quellenrunde abgeschlossen. Befundrunde mit zwei bestätigten und korrigierten Medium-Funktionsfehlern; keine Nullfundrunde.** Die unabhängige rotierte Runde 6 folgt gesondert.

## Prüfflächen

| Familie | Tatsächlich geprüft | Status |
|---|---|---|
| Verträge/Trust | CLAUDE, CONTEXT, Threat Model, Architektur-Skill/LANGUAGE, Security-Skill, ADR 0002/0004/0006/0008/0011/0013/0014/0015/0016/0017/0018 | Abgeschlossen |
| AI Entwurf | AgentTeamDraftingService, AgentTeamContracts: Nameninventar, JSON-Formprüfung, beschränkte Wiederholung, keine wirksamen Berechtigungen aus Modellantworten | Host-Auflösung gehört zum API-Gegencheck |
| AI Transport, Chat, Knowledge, Agent Runtime | Beide OpenAI-Clients, Transport/Endpoint/Proxy/Profile/DI, Generierung/Assistant/Merge, Toolloop und Dispatch, sämtliche Knowledge- und Agent-Module einschließlich Evidence/Board/Completion/Context/Schema, Prompts und Katalogrenderer | Risikobasiert abgeschlossen; lange Instruktionskonstanten teilweise statt wortweise |
| Engine Activities und PowerShell | Sämtliche Activity-Familien von Datei/Text/XML/JSON/ZIP bis SQL/REST/Mail/WMI/Registry/Prozess/Service/Task/Power und Unterworkflows; Runspace/Process/Wrapper/Launcher/Environment/Parameterauflösung und Factory | Abgeschlossen; vendortes Archivmodul nur Import-/Packagingvertrag |
| Engine Lifecycle, Security, Trigger, Agents | WorkflowEngine/Scheduler/StepRunner/Retry/Recovery, Cache/VariableResolver, Gate/Invocation, StepTester/Debug, alle Security-/Trigger-/Notification-Module, Registry/DI/Options. Agents zusätzlich unabhängig durch Root | Abgeschlossen; [ergänzender Agent-Nachweis](round-5-engine-agents.md) |
| Data Stores/Model/Availability/Secret Provider | Sämtliche handgeschriebenen Familien einschließlich Credential-/Custom-/Knowledge-/Notification-/Replay-Stores, Lifecycle/Outbox, TreeLock, DbContext/AgentModel und Providerportabilität | Abgeschlossen |
| Data Migrationen | Neue Histogramm- und Retryindex-Migrationen mit Up/Down und relevanten Designermappings; übrige Migrationen nach destruktiven Operationen und Providerverträgen, MigrationDrift-/Provider-Tests risikoorientiert | Keine Vollzeilenprüfung generierter Designer; keine echten Providerläufe |
| Scheduler Sources/Retention/Notifications/Alerts/HA | Alle Sources, TriggerOrchestrator/Health, Retentionfamilien, Dispatcher/Collectors, sämtliche Alertquellen und Evaluator/Parameter/Catalog/Query, ClusterLeaderService, Stats/Maintenance/Signals | Abgeschlossen |
| Remote und Telemetry | Alle sechs Remote- und fünf Telemetry-C#-Dateien: Sessionpool/-factory/-lease, Fehlerklassifikation, OTel/Prometheus/Serilog/ResourceIdentity; Registrierungen und Projektdateien | Abgeschlossen; relevante Guard-/Emissionstests quellenbasiert |
| Import und Tests/Projektgrenzen | Scorch-Eingabeparser, deaktivierter Import-/Reviewvertrag, Referenzauflösung und risikoreiche Mapper; konkrete Variable-/Runspace-/Watcher-Regressionen, Agent-/Provider-/Remote-/Telemetry-Verträge | Risikobasiert abgeschlossen; repetitive Mappingtabellen und Testfixtures selektiv |

Inventarisierung allein zählt ausdrücklich nicht als Quellenprüfung. Ältere Runden und deren grüne Tests werden nicht als neuer Rundennachweis gewertet.

## Befunde und Gegenargumente

### #136 – Qualifizierter Schrittparameter übernimmt fremden Kurzalias (Medium, Funktion)

`PowerShellActivitySupport` ersetzte fehlende `{{lookup.param.hostName}}`-Werte durch einen unqualifizierten `hostName` eines anderen Vorgängers. `BuildStepVariables` erzeugt diesen Kurzalias regulär; `StepRunner` lässt RunScript/Custom ihre Templates selbst auflösen. Ein leerer Suchschritt konnte so statt des ausdrücklich angefragten Ziels den Server eines anderen Vorgängers liefern. Beide betreffenden Vorgänger können gültige Ahnen sein; die Scope-Prüfung verhindert diesen Fall daher nicht.

Root hat Ursache, üblichen Ausführungspfad und minimale Korrektur unabhängig bestätigt. Die zwei Fallbacks sind entfernt. Korrekte Schritt-ID-/Outputalias-Zugriffe und ausdrücklich verwendete PowerShell-Kurzvariablen bleiben erhalten; unbekannte Templates behalten den bestehenden Literalvertrag. Keine neue Fehler- oder Berechtigungsrichtlinie.

Vier Regressionen verwenden echte Variablenzusammenstellung und echte Runspace-Ausführung über RunScript sowie gespeicherte/aktivierte Custom Activities. Alle vier waren rot (fremder Wert `ServerA`); danach 110/110 Variable-/Template-/Script-/Custom-/StepRunner-Fälle grün. Belege: `.codex-round5-param-owner-red.log`, `.codex-round5-param-owner-green.log`.

### #141 – Hängender Dateiwächter-Scan blockiert die Triggerverwaltung (Medium, Funktion)

Die regelmäßige Bestandsaufnahme lief synchron unter dem Delivery-Gate. Bei einer nicht mehr antwortenden Netzwerkfreigabe wartete `DisposeAsync` unbegrenzt auf diese Arbeit; der Orchestrator entfernt bzw. erneuert seine Trigger seriell. Damit konnte der Ausfall einer regulären UNC-Freigabe Änderungen an anderen Triggern festhalten. Die vorhandene Deadline für Start und Directory-Probe begrenzte diesen Scan nicht.

Die bestehende Deadline umfasst jetzt ausschließlich die Erstellung eines lokalen Snapshot-Dictionarys. Nach Timeout wird die Quelle unhealthy und startet keine weiteren Scans; vor Ergebnisübernahme wird Cancellation geprüft. Delivery und Checkpoint bleiben beim wartenden Aufrufer. Root hat Ursache und finalen Diff unabhängig bestätigt. Zwei Barrieretests waren vorher rot; anschließend 102/102 FileWatcher-/Orchestrator-Fälle grün, ohne Skips (`.codex-round5-filewatcher-deadline-red.log`, `.codex-round5-filewatcher-deadline-green.log`).

Grenze: Der native SMB-Aufruf bleibt durch das Betriebssystem begrenzt, sein Workerthread kann bis zur Rückkehr weiterleben. Er hält die Triggerverwaltung nicht mehr fest und darf verspätet keinen Zustand veröffentlichen. Der Nachweis verwendet eine blockierte Enumeration-Seam, keinen echten UNC-Ausfall.

## Architektur und Security getrennt

Architekturprüfung verfolgt Besitzer von Regeln, Interface-Tiefe, Löschtest, Duplikation und reale Änderungsfolgen. Die beiden Korrekturen bleiben im vorhandenen Besitzer: qualifizierte Parameterauflösung und Snapshot-Lifecycle. Keine zusätzliche parallele Prozess-/Gate-Abstraktion. Dateilänge oder zusätzliche Typen allein ergeben keinen Medium-Befund; keine weitere praktisch relevante Vertiefung bestätigt.

Securityprüfung verfolgt tatsächliche Aufruferidentität und Daten von HTTP-/Workflow-/Modell-Eingaben bis zu DB, Prozess, Dateisystem, Netzwerk und Ausgabe. Operator-Autoren, Administratoren, installierte PowerShell-Module und genehmigte MCP-/Skill-Anbieter gelten entsprechend dem dokumentierten Trust-Modell als vertrauenswürdig; Folder-RBAC ist keine Code-Sandbox.

Kein zusätzlicher bestätigter Critical-/High-/Medium-Sicherheitsbefund. Live-Notifier-Emitter wurden gegen den API-Hinweis verfolgt: Schrittoutput/Error/Trace werden vor SignalR redigiert, terminale Workflowfehler ebenfalls. Fehlende Credential-IDs werfen im produktiven Store. Synthetische Agent-Schritttests scheitern an der Journalinitialisierung vor Toolöffnung und leihen keine Publisheridentität. Dokumentierte Agent-Team-/Subworkflow-Kapazitätsverträge werden nicht als neue Mängel ausgegeben. Anbieter-Proxies bleiben eine explizite Vertrauensannahme; Pfadprüfung ersetzt keine Betriebssystem-ACL. Seltene Rekursions-/Admin-Races und bloße Größenvermutungen ohne belegte tägliche Auswirkung wurden nicht zu Medium hochgestuft.

## Validierung und Grenzen

Release-Fokus zu #136: 4 erwartete Fehler vor der Korrektur, danach 110 bestanden, 0 fehlgeschlagen, 0 übersprungen. Zu #141: 2 erwartete Fehler, danach 102 bestanden, 0 fehlgeschlagen, 0 übersprungen. Gemeinsame .NET-Ausgaben wurden über Build-Slots koordiniert. Weitere gelesene Tests wurden nicht als neu ausgeführt gezählt.

Keine WinRM-, HA-, externen LLM-, echten UNC-Ausfall-, PostgreSQL-/SQLServer-Migrations- oder Produktionszugriffe. Vendorter Fremdcode, generierte Designer, repetitive Importtabellen und Testfixtures wurden vertrags-/risikoorientiert geprüft, nicht vollständig zeilenweise. Kein eigener vollständiger Projektsuitenlauf in diesem Teilauftrag. Diese Grenzen sind keine offenen bestätigten Befunde, erlauben aber keine Behauptung mathematischer Fehlerfreiheit. Die nächste unabhängige Quellenrunde muss den korrigierten Stand erneut prüfen.

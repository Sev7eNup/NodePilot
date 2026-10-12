# Runde 5 – Clients und Betrieb

Datum: 2026-10-12. Branch `audit/practical-impact-2026-10-12`, Basis `de71058`.
Änderungen seit `318ae01` werden zusätzlich verfolgt; frühere Berichte ersetzen keine frische Prüfung.
Nutzerscope: Critical/High und Medium mit tatsächlicher täglicher Nutzungsauswirkung.
Reine Randfälle, Low und Kosmetik werden nicht als Fixauftrag behandelt.

Angewandt: improve-codebase-architecture und audit-ai-slop-code. Root-CLAUDE, CONTEXT,
CLI-/MCP-CLAUDE, Threatmodell, ADR0005/0010/0012 und Betriebsverträge gelesen.
Startinventar der zugewiesenen Verzeichnisse: 538 versionierte Dateien einschließlich
Fixtures/Assets. Das ist keine Behauptung vollständiger Quellenabdeckung.

## Architektur- und Sicherheitsabdeckung

Die Prüfung ist risikobasiert über sämtliche zugewiesenen Modulgruppen abgeschlossen.
Vollständige Datei- oder Fixture-Zeilenabdeckung wird nicht behauptet. Die folgende Matrix
unterscheidet ausführlich gelesene Laufzeitpfade von gezielt geprüften Betriebs-/Fixture-Seams.

| Familie | Tatsächlich geprüfte Architektur-/Funktionspfade | Sicherheitsgegenprüfung und Grenzen |
| --- | --- | --- |
| CLI und gemeinsame Clients | Program, SessionResolver/Context, ApiClientFactory, WorkflowResolver, HTTP-/Agentclient und DTOs; Auth/Config/Users/Globals/Credentials/Machines/Folders/Permissions/Maintenance/Stats/Observability/Operations/Audit/SystemHealth/Agents/Settings/Secrets/Backup/Db/Workflow/Exec-Commandfamilien, Watcher und Ausgabe-Seams | TokenRefreshHandler, ClientSessionSecurity, TokenStore, ClientSessionFileCoordinator und ApiResponseReader: Origin/TLS, DPAPI, Refresh-CAS, Dateisperren, Fehlerklassifikation. Server-RBAC bleibt Autorität. Formatierer nicht jede Tabellenzelle gelesen. #135/#140. |
| Switcher | Reconciliation beider Stacks, SwitchCoordinator, AllowList/Workload, SCM-/Discovery-/Prozessseams, Config/Probe/CLI-Locator, ViewModels/AsyncCommand, UserInteraction, Logger und App-Code | Readiness, fail-closed Umschalten, SCOrch-Same-Origin-Pagination und Redirect-Verbot, Prozessargumente, ACL-/Konfigurationstrust. XAML-/Stilkatalog nicht zeilenweise geprüft; echte Dienste nicht geschaltet. #135. |
| MCP | Program/Config/Session/ApiClientFactory, sämtliche Toolfamilien, Ressourcen, Mapping/Patcher/DefinitionDiff, HTTP-Client samt Agents und sämtliche DTOs gelesen | Destructive-Tool-Gate, Server-RBAC, SecretRedactor bis API/Core verfolgt, Teilausgaben/Serialisierung und Ressourcenbegrenzung. #138/#140; volle MCP-Suite 215 grün. |
| Desktop | Alle Runtime-Module main/security/config/http/backendRestart/setupFlow/setup-preload/setup.html/skins, Packaging-Scripts, package.json, TS/Vitest-Konfiguration und README | Exakter IPC-Absender, Loopbackorigin, Zertifikatpin, Navigation/Download, Backendrestart, Electron-/Package-Grenzen. Kein Electron-/Installationslauf; vorhandene Testseams betrachtet, keine erneute vollständige Testsuite. |
| Deploy und Installer | Install-/Update-Entry-Points, Desktop-Update/Provisioning/Runtime, SetupContract/Adapter, ServiceControl/MachinePath/SwitcherConfig; ArtifactSecurity, Runtimepayload, Build-Artifact und beide Installerbuilds, Publish-Site | Ausführlich: Update-/Rollback-Reihenfolge, DB-Restore, Dienst-/Prozessbesitz, signierte/exakt manifestierte Extraktion, Settings-Erhalt, Runtimepins und Secrets. Install-/Inno-/Preflight-Großdateien anhand tatsächlicher privilegierter Seams und Aufrufer geprüft, nicht jede Wizardseite. Keine Installation ausgeführt. |
| Betriebsskripte | Gemeinsames nodepilot_http, disable-all, stress40 Python/PS und stress50, Demo-/Alertseeds, ContinuousInstall, dev-reset, Provider-/Nightlyrunner und Taskregistrierung | Authheader, Lock/Publish vor Execute, Exitcodes, Zeitgrenzen, eigene Prozess-/Container-/Datenbankressourcen vor Löschung. TestSuite Installer/Verifier/invasive Servicefälle anhand konkreter Mutations-/Cleanupseams; nicht jeder generierte Workflowkatalog vollständig gelesen. |
| Release-/AD-/LDAP-Lab und Capture | Server-/Desktopmatrix, Guest-/Computer-use-Seams, Gateverdict/Provenance, AD-SetupDirectory/ApiHost/Browserpolicy, LDAP-Container und Live-Testvertrag, Product-tour-Record-/Mockgrenze | Wegwerf-VM/Checkpoint-Vertrag, Remote-SQL-Baseline vor Cleanup, keine PASS-Ausgabe für fehlende Evidenz, LDAPS-Validierung, explizite Testaccounts, Loopback-/Mock-Capture. Nur Quellenprüfung: keine AD-/Hyper-V-/Computer-use-Abnahme behauptet. Detailfixtures und Bild-/Videorenderer nicht vollständig gelesen. |
| CI und Paketierung | ci/docs-pages/secret-scan/codeql, Directory.Build.props, Directory.Packages.props, global.json, Runtime-/Publishhygiene-/Electron-Gates | Jobberechtigungen, Fehlerweitergabe, Signature/Hash-Reihenfolge, native Runtimeversionen und transitive Advisory-Audits. Keine Pakete oder Lockfiles geändert. |
| Grafana | Compose/Prometheus/Datasources und Betriebs-README; repräsentative Workflow-/Activity-/Mission-Control-Abfragen und Queryverträge | Loopbackports, Login statt Anonymous, Entwicklungsmetrics-Opt-in, Providerlabel-/Metric-Konventionen. Keine vollständige visuelle Prüfung sämtlicher Dashboardpanels oder laufende Prometheus-Abfrage. |
| LoadHarness, Samples, TestCommons | Load Program/Options/APIclient/Seeder/ScenarioFactory/SignalRObserver/Templates und README; Samplevertrag/Windowsdiagnostik; ProviderTestDatabase/TestDbFactory/FakeSmtpServer/TestDatabaseAvailability | ManualTrigger/Publish/Total-Verträge, ausdrücklich bestätigter Noop-Lastrunner, zufällige eigene DB-Namen und Cleanupguard, Loopback-SMTP und Disposal. Keine echte Last, Samples oder Providerdatenbank gestartet; übrige reine Fixture-/Optionsdoubles risikobasiert eingeordnet. |

## Architektururteil und verworfene Kandidaten

Die tragfähigen Vertiefungen bleiben vorhandene Seams: zentrale HTTP-/Sessionverwaltung,
gemeinsame Redaction vor Projektion und serverseitige Filterung/Begrenzung. Für #135 wurde
keine zweite allgemeine Retry-Abstraktion eingeführt. Die gemeinsam genutzten Python-HTTP-
und PowerShell-Betriebshelfer bündeln reale Fehler-/Besitzverträge; bloße Zahl ähnlicher
Commands oder DTOs ist kein sinnvoller Refactorgrund.

Nicht als praktische Mediums verfolgt: absichtlich bekannte Kennwörter im isolierten
Samba-Testverzeichnis, vertrauenswürdige lokale Admin-/Buildkonfiguration, reine
Ausgabeformatierungsrandfälle, absichtlich begrenzte/fail-closed Switcherinventare,
best-effort SignalR-Beobachtung im Lastlabor und fehlende zeilenweise XAML-/Textkatalogprüfung.
Ein kompromittierter Installationsadministrator wird nicht als neue Berechtigungsgrenze
erfunden. Guest-Lab-TLS-Ausnahmen sind auf das ausdrücklich wegwerfbare lokale Lab begrenzt;
sie begründen keinen Produktions-TLS-Bypass.

## Kandidaten zur unabhängigen Gegenprüfung

### #135 Switcher verwechselt Dienststatus mit API-Bereitschaft (Medium, behoben)

`WindowsServiceControlGateway.StartAsync` wartet ausschließlich auf SCM Running.
`SwitchCoordinator` startet danach sofort Workloadreconciliation; deren erster
`np workflow list`-Fehler führt ohne Readiness-Wiederholung zum fail-closed Stop beider Stacks.
`DatabaseBootService.StartingAsync` dokumentiert und implementiert dagegen DB-Warten,
Migration und Recovery nach SCM Running, vor Kestrelstart.

Alltagsszenario: Wechsel vom laufenden SCOrch auf gestoppten NodePilot mit normaler
Boot-/Datenbankwartezeit. Die CLI trifft noch keinen Listener; der Switcher beendet die
gerade startende Instanz wieder und lässt beide Stacks gestoppt. Ein sehr schneller Boot
kann die Race gewinnen, beseitigt aber den fehlenden Bereitschaftsvertrag nicht.

Root hat die Quellkette unabhängig bestätigt. Die CLI unterscheidet jetzt tatsächliches
Socket-ConnectionRefused und HTTP503 durch Exitcode 5 von TLS-/Auth-/Konfigurationsfehlern.
Der Switcher wiederholt ausschließlich den initialen Inventarabruf innerhalb eines gemeinsamen
CommandTimeout-Budgets. Mutationen und abschließende Verifikation bleiben ohne Startup-Replay.
Der echte Reconciler war mit sequenziell noch nicht erreichbarer API zunächst rot; anschließend
132/132 Switcher-Tests grün. Zwei CLI-Klassifikationsfälle zuerst rot, danach 12/12 BaseCommand-
Tests grün. Negative Fälle decken permanente Fehler und fehlgeschlagene Schreib-/Verifikationsaufrufe
ab; eine dauerhaft unerreichbare API endet ohne Mutation am gemeinsamen Deadline-Budget.

### #138 MCP-Teilansichten verlieren Geheimnisschutz (Medium, behoben)

`WorkflowReadTools.GetWorkflowContract` reichte tatsächliche Parameterdefaults von Editor-
Sessions an das Modell weiter. `CanvasAssistantTools.GetWorkflowNode` redigierte nur einen
abgetrennten Knoten; der gemeinsame Redactor benötigt aber die übergeordnete nodes-Struktur
für aktivitätsspezifische Regeln. Derselbe normale Abruf eines einzelnen REST-/Trigger-Knotens
lieferte damit Inhalt, der beim vollen Definitionsabruf verborgen war. Root bestätigte beide Pfade.

Vorhandene Contractdefaults werden nun als *** ausgegeben, null und Deklarationen bleiben erhalten.
Die Toolbeschreibung verlangt, maskierte Parameter wegzulassen, damit der Server seinen echten
Default verwendet. Einzelknoten werden erst nach Redaction der vollständigen Definition projiziert.
Vier tatsächliche HTTP-/Toolserialisatfälle erst rot; anschließend ganze MCP-Suite 212/212 grün.
Eine alte Einzelnode-Klartext-URL-Assertion widersprach dem aktuellen gemeinsamen Opaque-URL-Vertrag;
sie prüft jetzt Maskierung und erhaltenen Aktivitätstyp. Keine API-Redaktionsregel geändert.

### #140 MCP und CLI lesen für kleine Anfragen die gesamte Ausführungshistorie (Medium, behoben)

Root bestätigte den ungegrenzten Seitenloop in NodePilotApiClient.ListExecutionsAsync. Der reguläre
Toolaufruf mit limit=50 begrenzt erst anschließend; die Fehlerdiagnose sucht ebenso clientseitig den
letzten Fehlversuch. Die Korrektur nutzt bestehende API-pageSize/status-Filter, mit echtem Total
und gezieltem neuesten Fehlversuch. Keine neue API und kein Abschneiden vor erforderlicher Filterung.
Vier reale HTTP-Vertragsfälle erst rot, anschließend volle MCP-Suite 215/215 grün. Ein Total von
einer Million verursacht weiterhin genau einen Listenrequest; Fehlerdiagnose liest einen serverseitig
gefilterten Fehlversuch und dessen Schritte. Bestehende Sortierung und erlaubte Limitgrenzen bleiben.

Der unabhängige CLI-Gegenlauf belegte denselben Fehler bei `np exec list --limit`: der Client
las alle Seiten, der Command kürzte erst die Ausgabe. Der Client erhält nun das positive Limit,
behält eine konstante Seitengröße von min(200, limit) und beendet nach Erreichen des Limits.
Ohne positives Limit bleibt der vollständige Abruf erhalten. Echte seitengrößenabhängige
HTTP-Fixtures belegten zwei rote Limitfälle und einen bereits grünen Defaultfall; anschließend
136/136 CLI-Command-/Client-/Tabellentests grün. Limit 250 liefert 250 unterschiedliche IDs
mit genau zwei Requests, Limit 10 einen Request; der Default liest weiterhin alle drei Seiten.

## Abhängigkeiten

Am 2026-10-12 lief `npm audit --json` für UI, Docs-UI und Desktop gegen die vorhandenen Lockfiles:
jeweils null bekannte Lücken, einschließlich Entwicklungs-/Buildwerkzeugen und Electron. Zusätzliche
omit-dev-Läufe ebenfalls null. `dotnet list NodePilot.slnx package --vulnerable --include-transitive
--no-restore --format json` erfasste 18 Projekte ohne bekannte Lücken; der nicht enthaltene Switcher
wurde separat genauso geprüft, ebenfalls null. Keine Pakete geändert/installiert. Das sind
Datenbankabgleiche bekannter Advisories, keine Behauptung vollständiger Quellsicherheit.

## Grenzen

Keine Installationen, Dienstmutationen, externen Workloads oder Deployments ausgeführt.
Nur die genannten abgestimmten .NET-Läufe wurden gestartet. Diese risikobasierte Quellenrunde
ist abgeschlossen und enthält drei bestätigte, behobene Medium-Familien; sie ist deshalb keine
Nullrunde. Nach den gezielten Gegenchecks ist kein weiterer konkreter Critical/High oder
Medium mit belegter Alltagseinwirkung in diesem Scope offen. Das ist keine Garantie gegen
unbekannte Fehler. Die unabhängige Rotation in Runde 6 muss den aktuellen Stand erneut prüfen.

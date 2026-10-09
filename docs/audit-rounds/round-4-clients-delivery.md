# Runde 4 – Clients und Auslieferung

## Aktueller Stand nach Erweiterung des Scopes

Der ursprüngliche Produktionsscope sowie die zusätzlich übertragenen Browserdemo, Docs-Website und NonDesigner-SPA-Komponenten (`src/nodepilot-ui/src/components/**` ohne `designer`, 73 TSX-Dateien plus CSS) sind vollständig frisch gelesen. Die folgenden chronologischen Notizen bewahren den Verlauf; frühere „offen“-Angaben werden durch diese aktuelle Übersicht ersetzt. Kein projektweites Abschlussurteil.

Zusätzlich zur untenstehenden ersten Matrix vollständig gelesene Produktionsfamilien: alle Deploy-Bodys einschließlich beider ISS-Dateien, Server-/Desktop-Buildhelfer, HAProxy und Konfigurationsvorlagen; alle produktiven Python/PowerShell/JS-Skripte (HTTP/Stress/SDK/Nightly/Reset/Seed/Logo/Feature-LOC, Release-Lab, AD/LDAP, Continuous, TechDemo, 15 TestSuite-Generatorfamilien/Installer/Verifier, ProductTour/Renderer). LoadHarness-Compose/Prometheus/README und Dashboard geprüft. Vier Samples-Workflow-JSONs samt Scripts/Edges, Schema, Skill-Bodys und Windows-/SCCM-Referenzen gelesen; ZIP 1.0.1 bytegleich zur entpackten Quelle, 1.0.0 textuell nur Versions-/application-Differenz. Grafana: sämtliche Queries, Variablen und Transformationen aller zehn Dashboards sowie Compose/Provider/Prometheus/README gegen Metrikproducer geprüft.

### Browserdemo und Docs-Website

- Browserdemo: alle 42 Produktions-TS/CSS-Dateien und SCCM-Seedgraph gelesen: Boot/Fetch-Hub-Isolation, sämtliche Handler, World/CRUD, Simulator/Timer/Cancel, Guided-Missions, Seeds/Tour/DOM-Ausgaben. Vollständige Demo-Vitestfamilie nach #117: **139/139 grün, acht Dateien**.
- Docs-Website: sämtliche produktiven Site-TS/JS-Bodys, HTML/CSS/Übersetzungen sowie elf Buildskripte und Buildkonfiguration gelesen. Routing/Legacyredirects, SEO/Prerender/Public-vs-Product-Assembly, Blog-Draftfilter, Medien/Origin/Headers, Navigation und Asset-Verträge geprüft. Root-Fix #112 (`Toc`-Dependency `lang` und `DocPage`-Prop) unabhängig gegengelesen und sinnvoll. Root meldete 221 Docs-Tests und Typecheck grün; kein eigener Wiederholungslauf behauptet.

### Später abgeschlossene Befunde

| ID | Severity | Ursache / Maßnahme | Tatsächlicher Nachweis |
|---|---|---|---|
| 105 | Medium funktional | Doppelte `//`-Properties in HA-Vorlage verhindern echten .NET-JSON-Providerstart. Eindeutige Kommentarnamen, Werte unverändert. | Gerenderte Vorlage mit realem Provider: 1 rot → 1 grün, `DeploymentTemplateConfigurationTests`. |
| 114 | Low funktional | LoadHarness-Dashboard verwendet falsche Histogramm-/ThreadPoolnamen, zugesagter Autoimport ohne Provider. Korrekte `_milliseconds_bucket` und ms-Einheiten; Queue bleibt Snapshot ohne Rate. Provider/Default-Prometheus-Datasource/Mount ergänzt. | Offline-Provisioning insgesamt 3/3 grün; kein Grafana-/Containerlauf. |
| 117 | Low funktional | Guided-Missionplan simuliert deaktivierte geplante Nodes erfolgreich. Aktive Node-Menge prüfen, sonst vorhandener Graphänderungsfehler. | Drei Missionfälle rot → grün; Demo139/139. |
| 128 | Low funktional | Modal-Inhalt verschluckt Escape vor dem äußeren Close-Handler. ModalShell, TypedPhraseConfirmDialog und EditCellDialog schließen nun gezielt; Editor-`preventDefault`, verschachtelte Portale und laufendes Speichern bleiben berücksichtigt. | Drei Fälle rot → Modal-/DbViewer-Fokus **41/41 grün**. |
| 130 | Medium funktional / Architektur | Settings-Background-Refetch ersetzt lokalen Draft und dessen ETag; vier parallele Save-/412-Implementierungen verbreiten dieselbe Invariante. Vorhandene SectionFormHelpers besitzen nun den Edit-Snapshot, gebundenen ETag, exakten Retry-Payload, Pendingzustand und bestätigte Übernahme zentral. Auth, Retention, SMTP und LLM benutzen dieselbe Seam. | Tatsächlicher TanStack-Reconnect mit Produktionsdefaults: Draftverlust rot. AdminSettings **96/96 grün**, danach zusätzliche SMTP-Secret-Regression mit Integrations-/Draft-Fokus **23/23 grün** (97 unterschiedliche Tests zusammen); `tsc -b` grün. |
| 132 | Low funktional | Offenes Operations-Detail aktualisiert Live-Status, behält aber laufenden Detail-Snapshot ohne endgültigen Fehler/Schrittergebnisse. Status im bestehenden Querykey erneuert Detail bei Übergang. | Gemountete Komponente Running→Failed: finaler Fehler fehlt rot → Operations-Detail/Seite **39/39 grün**. |

### NonDesigner-SPA: vollständige Modulmatrix

| Familie | Frisch gelesene Module / tatsächliche Prüfachsen |
|---|---|
| Einstieg und Layout | BrandLogo, ErrorBoundary, ProtectedRoute, AppLayout, Sidebar, TopBar, DatabaseOutageBanner; Auth-Epoch/Navigation, Query-Gates, Rollen-/Capabilityanzeigen, Host-/DB-Zustand und Eventcleanup. |
| Common | ConfirmHost, ContextMenuShell, CopyableId/CopyButton, EChart/LazyEChart, EmptyState, FolderBulkBar, Markdown, MobileCardList, ModalShell, RunWorkflowDialog, StatusBadge, ToastHost, TypedPhraseConfirmDialog; Text-/Markdown-Ausgabe, Portale/Escape, Aktionen und Fehlergrenzen. |
| AI | AiChatWidget samt CSS, AiPromptDialog, AiWorkflowChatPanel, ChatThreadMenu, UsageFooter, WorkflowGenerationDialog, KnowledgeChat; Scopewechsel, Abort/Lifecycle, Entwurf-/Apply-Grenze, HTML-/Linkausgabe, Capabilityguards. |
| DbViewer | EditCellDialog, QueryPane, TableGrid, TableList, useResizableColumns; Tabellenwechsel/React-Key, selektierte Zeilen, Savepending, SQL-/Exportconsumer und Statusfehler. |
| AdminSettings | AgentsSection, AiKnowledgeSection, AuthenticationSection, DbAdminSection, EnvOverrideBadge, EtagConflictDialog, IntegrationsSection, LoggingTelemetrySection, PerformanceSection, RetentionSection, RestartBanner, SecretField, SectionFormHelpers, SecuritySection, SupportEventsTable, SupportLogViewer, SystemInfoSection, TestProbeModal; sämtliche Formular-/Probe-/Save-/412-/Secret-/Overridepfade. Dateinamen teilweise als lesbare Modulnamen abgekürzt. |
| Dashboard / Operations | DashboardQuickActions, DurationTrend, FailureCauses, SystemHealthBanner; OpsDepartureBoard, OpsExecutionDrilldown, OpsMobileView, OpsStuckStrip, OpsTimeline, OpsTimelineBar; Scope-/Statusprojektion, Zeit-/Zoom-/Resizecleanup, Aktionen, terminale Diagnostik. |
| Alerting | AlertingRuleEditor, DeliveriesModal, SystemAlertsSection, SystemPolicyEditor; Formannahmen, Source-/Route-Auswahl, Enabled/Severity, Fehler und Savezustand. |
| Workflows / Globals | BulkMoveFolderDialog, ConcurrencyLimitDialog, SharedFolderContextMenu, SharedFolderPermissionsModal, SharedFolderTree, WorkflowBulkBar, GlobalFolderTree; reale Backendrechte als Autorität, Bulk-/Drag-/Drop-/Delete-Verträge, Wurzel-/Elternsicht und Baumaufbau. |

Security-Ergebnis dieses Zusatzscopes: kein bestätigter neuer C/H/M-Sicherheitsbefund. UI-Gates ersetzen keine API-Autorisierung; Backend-RBAC wurde an relevanten Consumern gegengeprüft. Architektur-Ergebnis: #130 ist eine belegte gemeinsame Formularinvariante, keine Abstraktion wegen Dateigröße. Deletion-Test: Entfernen der zentralen Seam würde Snapshot/ETag/Retry/Pending-Wissen erneut auf Formfamilien verteilen. Alerting-Editoren behalten verschiedene Fachsemantiken; gleiche JSX-Feldformen allein rechtfertigen keine weitere Hierarchie.

Gegenargumente geprüft: Produktions-QueryClient schaltet Window-Focus-Refetch aus; #130 betrifft tatsächlich Reconnect, nicht den zunächst vermuteten Tabfokus. Zwei 412-Konflikte hintereinander übernehmen keine abgelehnte Fremdversion; KeepMine sendet jeweils denselben gespeicherten Payload. SMTP-Secret bleibt während Refetch lokal und wird erst nach bestätigtem Save zurückgesetzt. DbViewer-Tabellenwechsel besitzt bereits einen React-Key. Alte AI-Chat-Antworten werden im alten Workflow-Scope gespeichert und überschreiben nicht den neuen sichtbaren Scope. SupportEvents begrenzt interaktive Historie ausdrücklich und bietet Export; kein behaupteter Datenverlust. Kleine Komfort-/Übersetzungsränder ohne reproduzierte erhebliche Wirkung wurden nicht zu Medium aufgewertet.

Unabhängige Gegenprüfung durch Root für #128/#130 sowie #99/#101/#102/#105/#114/#117 abgeschlossen. Root-Fixes #124 (CustomActivity-Warnings aktualisieren ID/ConcurrencyToken), #126 (begrenztes Maschinen-TestAll) wurden unabhängig an Consumer-/Serverpfaden nachvollzogen. Der Architekturtest `AdminSettingsFrontendSyncTests` erkennt jetzt beide tatsächlichen Formular-Einstiege `useSectionForm` und `useSectionEditor`; exakte Gleichheit mit Backend-Sections bleibt bestehen. Dessen .NET-Nachlauf liegt bei der zentralen Buildqueue, nicht als eigener ausgeführter Test behauptet.

### Security- und Architektururteil des abgeschlossenen Grundscopes

Nach Gegenprüfung und genannten Fixregressionen bleibt im vollständig gelesenen eigenen Scope einschließlich der Erweiterungen kein bestätigter offener kritischer, hoher oder mittlerer Befund. **R4 ist dennoch eine Findingsrunde**, keine neue fehlerfreie Gesamtprojektrunde. Die nächste vollständige Projektrunde und zentrale Integrationsnachweise liegen bei Root.

Deletion-Test: CLI/MCP-DTO-Duplikate haben einen dokumentierten Deployment-/Abhängigkeitsgrund. Core-Trustentscheidung, ArtifactSecurity/SetupContract und vorhandene Rollback-Seams wurden wiederverwendet. Demo-World/Handler bilden eine ausdrücklich fiktionale tablokale Grenze; Backend-Authentifizierung darin wäre kein sinnvoller Sicherheitsfix. Website und Produktdocs benötigen verschiedene Auslieferungswurzeln, die zentrale Assembly kennt diesen Unterschied. Dateigröße, statische Übersetzungen und schmale repetitive HTTP-Methoden allein sind keine Medium-Funde.

Weitere verworfene Kandidaten: Grafana `workflow_name` wird tatsächlich vom Engine-Meter geliefert; Prozess-CPU-`state` und .NET-`cpu_mode` sind verschiedene Producer. Demo-Request+init-Bodyoverride hatte keinen betroffenen produktiven Consumer. Website-HTML stammt aus lokalen author-owned Sprach-/Contentdateien; Such-/URL-Input erreicht Text-/validierte Routingseams, kein bestätigter XSS-Pfad. Malformed-MP4 und unterbrochene Medienregeneration betreffen explizite lokale Buildinputs; ohne normalen reproduzierten Releasefehler kein Medium.

### Präzise Prüfgrenzen

- Produktionsfamilien neu gelesen, nicht bloß Diffs. Begleitende Tests gezielt verfolgt: insbesondere große `Test-DeploymentTemplates.ps1`/`Test-SetupAdapter.ps1` sowie sämtliche Desktop-/Clienttestbodys wurden nicht vollständig manuell gelesen.
- TestSuite-Workflows stammen aus vollständig gelesenen Generatoren; nicht jede große generierte JSON-/Lab-Beispieldatei wurde manuell gelesen. Grafana-Layout/Bildpixel und sämtliche Rohmetrikzeilen sind keine vollständige visuelle/Runtimevalidierung. Medienkataloge gegen Consumer-/Pfadverträge geprüft, nicht jedes Bild/Video abgespielt.
- PS-Regressionen nutzen tatsächliche AST-Bodys und isolierte Tempdateien/ACLs/Fakes. Keine echten Installer/Dienste/Labs/Domänencontroller/SQL-/PostgreSQL-Server/externe Workflows. PS7 nicht installiert; seine Fehlerrückgabeform unter 5.1 simuliert.
- Kein neuer Live-Publish, Browser-/Apache-/Grafana-Test oder externer Paket-Advisoryscan dieses Teilprüfers. Offizielle Grafana-Dokumentation zur Providersemantik geprüft; keine Behauptung über den tatsächlichen Betrieb externer Komponenten.

## Chronologischer Prüfverlauf

Status: laufende vollständige Quellenrunde, kein Abschlussurteil.

Ausgangspunkt: `round-4-inventory.json`, 2026-10-09T11:33:25.773219+00:00,
3178 Dateien, SHA-256
`586be1aacd35e96be2906a90bb17a144def44e28778389473d959cf1db6a8e42`.
Erneute Anwendung von `improve-codebase-architecture` und `audit-ai-slop-code`;
frische Body-Lektüre mit rotiertem Besitz. CLAUDE/CONTEXT/Threat Model und beide
Client-CLAUDE-Dateien gelesen. Root hält den .NET-Buildslot für breite Suiten.

| Familie | Tatsächlicher Stand | Entry Points / Prüfachsen |
|---|---|---|
| CLI | Alle Produktions-C#-Dateien frisch vollständig gelesen | CommandRegistration und sämtliche Commands; HTTP-Methoden/DTOs, Passwort/SSO/Origin/Pin, Export/Import/Secrets, Folder-RBAC-Weitergabe, Settings-ETag, Debug/Run/Watch, Ausgabe und Exitcodes |
| MCP | Alle 30 C#-Produktionsdateien und Projektdatei vollständig gelesen | Alle zwölf Toolklassen, beide Resources, vollständiger HTTP-Adapter/Agents und DTOs; explizite Registrierung, destructive gate, Redaction/Merge, Whole-Definition-Validierung, Secret-Erhalt und HTTP-only |
| Desktop | Alle acht produktiven TS-Dateien, Setup-HTML, vier Packaging-Skripte und Konfiguration vollständig gelesen | Electron IPC/Navigation/Redirect/Download/Pin, Hand-off nur im Main-Prozess, Backend-Restart, package boundary; Tests noch vertieft offen |
| Switcher | Sämtliche C#-Bodys, XAML, Manifest/Projekt/Konfiguration und Betriebsvertrag vollständig gelesen | SCM-PID-Abgleich, Stop/Start/Fail-closed-Reihenfolge, Allowlist, CLI-ArgumentList/Password-stdin, Pagination/Origin; Redirect-Kandidat zur Gegenprüfung gemeldet |
| LoadHarness | Alle acht C#-Bodys, Projekt und Settings vollständig gelesen; übrige Betriebsfixtures folgen | Seeding→Publish→Execute, Terminal-Gate, PagedResponse.Total, Szenarien und SignalR-Batches/Observer |
| TestCommons | Alle 16 C#-Bodys und Projekt vollständig gelesen | Provider-isolierte Datenbanken, Stubs/Fakes, SMTP, Options/Logger; echte externe Provider nur mit explizitem Testanschluss |
| CI / Build | Alle vier Workflows, Directory.Build.props/targets, zentrale Packages/global.json/tools sowie Dependabot/CODEOWNERS vollständig gelesen | Fail-closed Prüfungen, Secrets, Paket-/Artifact-Grenzen; kein aktueller externer Advisory-Scan durch diesen Teilprüfer |
| deploy | ArtifactSecurity, ServiceControl, Preflight, Build-Artifact, Install/Update/Uninstall, SetupContract/SetupAdapter, DB-Provisioner, Runtimepayload, Publish/Hygiene/MachinePath/SwitcherConfig sowie DesktopRuntime/Provision/Update/Prepare/Uninstall/Build und Payloadguards vollständig gelesen; ISS/übrige Helfer folgen | Vertrauenswürdige Installationsinputs, Pfade/Secrets/Artifacts, ACL-/Rollback-Reihenfolge |
| scripts / Grafana / Samples | Inventar erstellt; Bodys folgen | Workflowfixtures, Lab-/Supportwerkzeuge, Lieferumfang |

## Security

Bestätigte neue Security-Funde dieser Teilrunde:

- #83: SCOrch-HTTP-AutoRedirect umging die bestehende Originprüfung für Konfigurations-
  und Pagination-URLs. Drei echte lokale Read/Start/Stop-Requests folgten einer fremden
  Origin. Begrenzte manuelle Redirects prüfen jeden Hop vor Credential-/Payload-Versand;
  gleiche Origin und 307-Body-Erhalt bleiben möglich. Switcher-Gesamtsuite 109/109 grün.
- #85: Installroot-Prüfung akzeptierte einen nicht vertrauenswürdigen Owner bei sicherer
  DACL. Der Owner konnte die DACL wieder ändern. Reale temporäre ACL reproduzierte den
  Fehler; Owner-Prüfung und Reparatur vor Backup/Copy ergänzt. Vollständiger
  ArtifactSecurity-Skriptlauf unter Windows PowerShell 5.1 grün. Kein echter Installlauf.

CLI-Hub ignorierte Session-TLS-
Optionen; die zunächst vermutete Pin-Sicherheitsabweichung wurde nach Gegenprüfung
verworfen: `PinnedCertificateHandlerFactory.Evaluate`, der Test
`Evaluate_ValidChainWithNonMatchingPin_StillAccepts` und `docs/mcp-server.md`
definieren ausdrücklich Chain-oder-Pin. Ein CA-gültiges anderes Zertifikat ist
also kein Bypass des bestehenden Vertrags. Funktionaler Folgecheck unten offen.

## Architektur / Funktion

- #80 funktional bestätigt: CLI `ExecWatcher.TryStreamWithSignalRAsync` baut den Hub ohne
  `session.Tls`. Ein über passende Pin-Ausnahme erlaubtes selbstsigniertes Backend
  kann REST bedienen, aber Live-Follow fällt stets auf Polling zurück. Echter
  lokaler TLS-Hub mit ausschließlich WebSockets reproduziert zwei Fehler (passender
  Pin und Insecure-Ausnahme), Stock/Wrong-Pin-Negativfälle sind grün. HTTP-Negotiation
  und WebSocket nutzen jetzt dieselbe bestehende `Evaluate`-Entscheidung. Zusammen mit
  Watch/TLS/Rotation 89/89 CLI-Tests grün, ohne Skip.
- #81 Client-Vertrag zur vom API-Prüfer bestätigten Schlüsselrotation: Die drei neuen
  Ergebnisspeicher NotificationRoutes/DispatchParameters/RuntimeSettingsFiles fehlten
  in CLI-DTO/Tabellenausgabe. JSON und Tabelle beide rot reproduziert, Parität ergänzt.
- #86: Der Server-Updater ersetzte die unterstützte exe-nahe Switcher-Konfiguration bis
  auf serverUrl durch die Vorlage. Ausführung seiner tatsächlichen AST-Statements gegen
  temporäre Dateien reproduzierte den Verlust. Vollständige Originalbytes bleiben nun
  nach Manifestprüfung und vor Start erhalten; unlesbare/ungültige Dateien stoppen vor
  Wipe, Restorefehler erreichen den Rollback. Bestehender URL-Fallback geprüft. Sämtliche
  SwitcherConfig-Prüfungen unter PowerShell 5.1 grün.
- Deletion test: gemeinsame Core-Client-Seam bündelt Session/Token/Trust/Response-
  Wissen für CLI und MCP. Die HTTP-Adapter/DTO-Duplikate sind durch CLAUDE bewusst
  getrennt; deren Löschung zugunsten einer API-Referenz würde den Dep-Graph ändern.
  CommandRegistration bündelt den produktiven und getesteten Commandbaum.
- Kein Medium allein aus fehlenden Komfortoptionen, einzelnen Markup-/YAML-
  Darstellungsrändern, Dateigröße oder repetitiven schmalen HTTP-Methoden abgeleitet.
- Switcher-Abbruch ohne Cleanup verworfen: Der einzige produktive Aufrufer übergibt
  `CancellationToken.None`; Fenster schließen wird während eines Wechsels verhindert.
  Interne Deadlines führen über den Fehlerpfad zur fail-closed Bereinigung.
- Switcher-Teilsicht der Allowlist vorerst nicht als Befund bewertet: Der Betriebsvertrag
  verlangt ausdrücklich ein Admin- oder Operator-Profil mit Zugriff auf alle Workflows.
  Eine pauschale Adminpflicht würde die dokumentierte Operator-Funktion einschränken.
- R3 M62 Integration nach breiter Root-Suite: drei alte BuildScript-Erwartungen angepasst,
  äquivalente PowerShell-Schreibweise `$($LASTEXITCODE)` verwendet. Der isolierte Lauf der
  am Sessionrand erfassten Throw-Expression beweist Exitcode und Text; das im Fehlerlog
  sichtbare `$:` war keine bewiesene Templatekorruption (FluentAssertions meldete dort
  selbst einen Formatfehler). 194/194 fokussierte Engine-Tests grün, kein Skip.

## Grenzen

Keine Live-Installationen, Dienste, realen Lab-/Domänen- oder Remote-Aktionen.
Weitere Module und zugehörige Tests sind noch offen, daher keine Aussage über
die vollständige Runde.

## Nachtrag der fortgesetzten Quellenprüfung

Frisch vollständig gelesen: sämtliche produktiven Server-/Desktop-Installer und Deploy-Helfer einschließlich `NodePilotServer.iss`, alle Release-Lab-, AD-/LDAP-Lab-Skripte, Product-Tour-Code, TestSuite-Generatorfamilien und `tech-demo/build_xl.py`. Die TestSuite-Generatoren wurden als Quelle geprüft; ihre Workflows wurden nicht installiert oder ausgeführt. Verbleibende Daten-/Dokument-/Testfamilien sind unten weiterhin ausdrücklich offen.

Weitere bestätigte und nach Gegenprüfung behobene Befunde:

| ID | Einordnung | Ursache / Maßnahme | Tatsächliche Prüfung |
|---|---|---|---|
| 92 | Medium Security | Desktop-Provisionierung veröffentlichte DB-Credentials im Service-Registrywert vor ACL-Härtung. Bestehende ACL-Seam jetzt vor Veröffentlichung, explizite Alt-ACEs entfernt. | Tatsächliche Provisionierungs-AST mit In-Memory-RegistrySecurity und Fake-Registryaufrufen: rot → grün, vollständiges `Test-DesktopDatabaseProvisioning.ps1`. Kein HKLM-Zugriff. |
| 93 | Medium funktional | Full-Desktop-Update aktualisierte nur app/desktop/pgsql, ließ deploy/tools alt. Alle fünf Payloads werden vor Backup/Stop geprüft und transaktional getauscht; Rollback entfernt auch neu angelegte optionale Altkomponenten. | Tatsächliche Update-AST gegen isolierte temporäre Dateien: rot → grün; `Test-DesktopUpdatePayload.ps1` und bestehendes `Test-DesktopUpdate.ps1` grün. |
| 95 | Low funktional | `Copy-Item -LiteralPath` mit Wildcard kopierte keine Offline-Runtimepayloads. Literal-Verzeichnisauflistung mit Filter und Copy je konkreter Datei. | Tatsächlicher Build-Branch gegen temporäres `[offline]`-Verzeichnis und Dummydateien: rot → grün, `Test-ServerRuntimeStaging.ps1`. Keine EXE-Ausführung. |
| 98 | High Datenverlust | Remote-SQL-Lab verweigerte vorhandene DB im Preflight, löschte sie aber trotzdem im finally; Login-Baseline blieb dabei ebenfalls falsch. Cleanup nur nach erfolgreicher, typgeprüfter Nichtvorhanden-Baseline; Remotingfehler terminieren. | Tatsächliche Preflight/finally-AST, sieben Fake-Remotingfälle: bestehende DB rot → grün, fehlende/ungültige/nichtterminierend fehlerhafte Rückgabe sowie bestehender Login geprüft. `Test-RemoteSqlCleanup.ps1`; keine SQL-/VM-Aktion. |
| 99 | Medium funktional | Testsuite-Installer/Verifier vertrauten auf auf 500 beschränkte Workflowliste. Vollständiger `/names`-Katalog, Detailabfrage nur für vorhandene relevante Workflows. | Tatsächliche Consumer-AST mit dem API-Vertrag List500/Names501: rot → grün. Detailstatements sichern Checkoutname und Disabledstatus; `Test-WorkflowCatalogue.ps1`. API-Vertrag gegen `WorkflowsController.GetAll/GetNames` gegengeprüft, kein Live-API-Test. |

Noch nicht abschließend klassifizierte Kandidaten: Failover-Smoketest ungültiger `-ComputerName`-Parameter und verworfener 503-Body (#101 Low freigegeben, Fix ausstehend); invasive TestSuite-Janitoren löschen parallel aktive Testfixtures (#102 Gegenprüfung ausstehend); doppelte `//`-JSON-Schlüssel in HA-Vorlage (Gegenprüfung ausstehend).

Noch offen: Samples-Workflow-/Ressourcendaten und ZIP-Parität, Grafana-Dashboard-/Metrikdaten, übrige begleitende Deployment-Testskripte sowie Dokument-/JSON-Beispielfamilien. Keine vollständige R4-Entwarnung.

- #101 Low funktional behoben: `Test-Failover.ps1` benutzt für Serviceaktionen jetzt PowerShell-Remoting und bewahrt den 503-Followerbody. `Test-FailoverFixtures.ps1` ist rot → grün; erfasst werden die PS5.1-WebResponse- und PS7-HttpResponseMessage-Form sowie Fake-Remote-Start/Stop. Keine Dienste angesprochen; PowerShell 7 selbst ist nicht installiert.
- #102 Low funktional behoben: gegenseitiges Löschen aktiver Test-Service-/Taskfixtures durch Janitor und Teardown. Beide Cleanupwege sind auf die eigene Familie beschränkt; das bestehende Pro-Workflow-Limit 1 schützt gleiche Familien. Tatsächlich generierte PowerShell-Cleanupskripte mit Fake-Ressourcen rot → grün (`Test-InvasiveFixtureOwnership.ps1`). Generator aktualisiert nur die zwei betroffenen JSON-Workflows; 51 Workflows / 398 Cases insgesamt unverändert.
- #107 Medium funktional, genaue Ursache nach Gegenprüfung korrigiert: Die Dashboard-Providerdatei existierte lokal, war jedoch durch `.gitignore` ausgeschlossen und fehlte im versionierten Checkout. Gezielte Ausnahme macht die vorhandene, unveränderte Providerdatei zum überprüfbaren neuen Repositoryinhalt. `grafana/test_provisioning.py` (Python/PyYAML) reproduziert den Packagingfehler und prüft anschließend Providerpfad, Compose-Mounts, zehn eindeutige Dashboard-UIDs und Datenquellenreferenzen. Rot → grün. Laut [offizieller Grafana-Provisionierungsdokumentation](https://grafana.com/docs/grafana/latest/administration/provisioning/#dashboards) werden Dashboarddateien über YAML-Provider unter `provisioning/dashboards` geladen; ein alleiniger JSON-Mount ersetzt diese Registrierung nicht. Kein Container-/Grafana-Runtimetest durchgeführt.

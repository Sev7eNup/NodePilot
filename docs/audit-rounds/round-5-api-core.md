# Runde 5 – API und Core

Abgeschlossen am 2026-10-12 auf `audit/practical-impact-2026-10-12`, Ausgangspunkt `de71058`. Frische Quellenprüfung; frühere Berichte oder grüne Tests wurden nicht als Nullfundbeweis verwendet. Dieser Teilprüfer änderte keinen Produktcode und führte keine Builds oder Tests aus.

Angewendet: `improve-codebase-architecture` und `audit-ai-slop-code`. Bewertungsumfang gemäß Nutzer: Critical/High und nur Medium mit konkreter täglicher Nutzungsauswirkung. Low, kosmetische Änderungen, hypothetische Edge Cases und abstrakte Refactors bleiben außerhalb des Fixumfangs. Grundlage: CLAUDE/CONTEXT, Trusted-Operator-Modell, ADR 0007/0009/0014/0016/0017 und die jeweiligen Funktionsverträge.

## Ergebnis

**Security:** kein neuer bestätigter Critical-/High-Befund oder Medium mit belegter alltäglicher Auswirkung in diesem Teilbereich. **Architektur/Funktion:** ebenfalls kein neuer eigener Befund dieser Kategorien. Zwei von Root gemeldete alltägliche UI-Lebenszyklusfehler wurden unabhängig bestätigt (#137 und #139); Umsetzung und Tests liegen bei Root. Das ist ein begrenztes Audit-Ergebnis, kein Beweis vollständiger Fehlerfreiheit.

## Tatsächliche Quellenabdeckung

Die nachfolgenden Implementierungsfamilien wurden frisch in ihren Codebodys gelesen, einschließlich unveränderter Dateien. Kommentare wurden teilweise separat gelesen. Trunkierte Ausgaben wurden für fehlende Codebereiche nachgelesen.

| Familie | Geprüfte Module und Einstiegspunkte |
| --- | --- |
| Controller | Alle 50 Dateien unter `Api/Controllers`, einschließlich Basen/Mapper. Login/Refresh/Logout, OIDC, SCIM, Benutzerverwaltung; Ressourcen-CRUD; Workflow-Lesen, Entwurf/Lock/Publish/Rollback, Import/Export; Execute/Retry/Cancel/Debug; Trigger/Webhook; Agent-/Knowledge-/Chat-/Team-Entwurf; Backup/Restore/DBAdmin; Settings/Secrets; Audit/AppLog/Diagnostics; Dashboard/Operations/Telemetry; Alerting/Maintenance. |
| Auth/Security | Sämtliche Dateien unter `Api/Security`, einschließlich TokenValidity, SessionIssuer, ResourceAuthorization, Folder-/Gruppenscope, externe Freshness, Revocation/Cancellation, Admin-Invariante, CSRF, Bootstrap/BreakGlass, HMAC/Replay, Script-Linter, Subworkflow-Resolver, RestrictedFileWriter und SwitcherKeyStore/Middleware. |
| LDAP/OIDC/SCIM | Vollständige Unterfamilien: LDAP-Verbindungsadapter, Authenticator, Circuit/Health, Mapper und DirectorySync; OIDC-Mapper/TicketStore/Optionen; SCIM-Authentication, typisierte Protokollantworten und kompletter ProvisioningService. |
| Live | ExecutionHub, HubRevocationSweeper, WorkflowLiveSubscriptions, SignalRExecutionNotifier, DatabaseAvailabilityHubFilter. Join/Rejoin, frische Berechtigung, Tree-Mutation, Gruppenaustrag und Versand zusammen verfolgt. |
| Backup/DBAdmin | Alle Backup-Parts, kompletter Restore-Service/State/Targets/Canonical/FolderShape/SecretRewriter sowie FileReader/Export; alle acht DbAdmin-Dateien mit Metadata, Policy, QueryBuilder/Executor, ReadOnly und SecretColumns. |
| Dashboard/Services | Sämtliche Dashboard-Aggregate, Cache/Warmup, Rollup, Histogramm, Trends, Fehlerursachen und Retry-/Historical-Berechnungen; AgentRecovery, DatabaseSecretRotation, HostIdentity, MachineStats, SettingsTestProbe, WorkflowFacts/Warmup, ContractDeriver, Ordering, Portability und VersionDefinitionProtector. |
| Dispatch | Alle fünf Dateien: Intentannahme, verschlüsselte Parameter, Lease/Abholung, aktuelle Laufberechtigung, Engine-Übergabe, Capacity-Deferral, terminale Fehler und Worker-Shutdown. |
| API-AI-Adapter | Alle neun Dateien: TeamDraftResolver/Inventory/DTO, LLM-Fehlerabbildung, Settings-/SQL-Knowledge, SSE; Modellvorschlag bis überprüfbarer Patch, Scope vor Wissenszugriff. |
| Querschnitt | Alle Diagnostics-, Logging-, Audit-, Filter-, Export-, HealthChecks-, Observability- und Telemetry-Dateien; AppLog-Formatparser/Tail/Download besonders seit `318ae01`. |
| Core-Definitionen | Alle 13 WorkflowDefinitions-Dateien: Document/Parser/Index, Structural/Edge-Validation, Analyzer/DataBus/Facts, Contract, Dependencies, Layout und Secret-Redaction. |
| Core-Aktivitäten/Agenten | Alle Activities- und Agents-Dateien, eingebettete Activity-Konfigurationsreferenz vollständig gelesen, csproj-Ressourceneinbindung; Custom-Type/Parameter/Hash/Validation, Agent-Konfiguration und Budget. |
| Übriger Core | Alle Clients-Dateien (DPAPI, atomare Sessiondatei, Refresh-Singleflight, Origin, TLS/Pin/Fehler), Audit, PerformanceSizing, Enums, Exceptions, DispatchIntent, ProxyBypassPattern, WorkflowCallGraphBuilder, SqlStatementInspector, TelemetryConstants, TimeZoneResolver, Database/EventLog-TriggerSettings und Validation. |

Ausdrücklich an Root delegiert: `Core/Models`, `Core/Interfaces`, `Api/Dtos` als Vertragsprüfung. Hosting, Configuration und Program/CompositionRoot wurden ebenfalls an Root übergeben; dessen risikobasierte Abdeckung und Grenzen stehen in [round-5-hosting-configuration.md](round-5-hosting-configuration.md). Hier wird nicht behauptet, jede dortige Mapperzuweisung selbst gelesen zu haben. Build-/obj-/bin-Ausgaben und lokale IDE-Caches sind keine Produktquellenprüfung.

## Testverträge – gelesen, nicht ausgeführt

Frisch vollständig gelesen: `SwitcherKeyTests` und `AppLogControllerTests`. Gezielt Code und Assertions gelesen: `AiAgentTeamControllerTests` (135–242), `AgentTeamDraftResolverTests` (Pfad-/Toolfälle 180–224), `DashboardTreeLockTests` (60–169), `TokenValidityMiddlewareTests` (507–558), `ScimProvisioningServiceTests` (149–260), `BackupRestoreServiceTests` (122–277) und `AppLogTailReaderTests` (85–166). Zusätzlich Methoden-/Fallinventar der AppLog-Parser-, Dashboard-Isolation-, TeamResolver-, TokenValidity-, SCIM- und BackupRestore-Suiten gesichtet.

Geprüfte Verträge: Switcher-Key gilt nur lokal und für fünf Requestformen; Logsuche kann redigierte Secrets nicht erraten; Downloads redigieren mehrzeilige Schlüssel; falsche Rollen trotz gültiger Session werden abgewiesen; SCIM-Folderentzug erhöht Stamp, widerruft Session und beendet Execution; Restore unterscheidet nicht erfolgten Commit von verlorenem Commit-ACK; Dashboard verwirft Antworten aus überholter Foldergeneration, ohne lange Aggregate unter dem TreeLock zu berechnen. Keine Behauptung, sämtliche Testbodys gelesen oder alle Provider zur Laufzeit geprüft zu haben.

## Kandidaten und Gegenargumente

- **Team-Inventar, globales Limit vor Run-Filter:** `AgentTeamInventoryBuilder.RunnableWorkflowsAsync` begrenzt alphabetisch auf 500 und filtert danach Folder-Run. Später einsortierte Workflows können im Entwurf fehlen. Im aktuellen Scope zurückgestellt: kein belegter normaler täglicher Ausfall, explizites Promptbudget und manuell überprüf-/erweiterbare Toolauswahl. Root bestätigt die Einordnung; keine Änderung.
- **Zwei Tabs desselben Nutzers:** per-user Lock und Save/Publish ohne Clientversion können konkurrierende Drafts überschreiben. Root ausdrücklich zurückgestellt: gleichzeitige Bearbeitung statt belegtem gewöhnlichem sequenziellem Ein-Tab-Weg, History/Autosynchronisierung vorhanden, breiter Versionsvertragswechsel aller Clients unverhältnismäßig.
- **PostgreSQL-Escape-Strings im SQL-Lexer:** `SkipQuotedLiteral` berücksichtigt verdoppelte Quotes, aber keine Backslash-Escapes in `E`-Strings. Ein gezielt gebauter Admin-SQL-Ausdruck könnte die Identifier-Sicht verschieben. Kein Live-PostgreSQL-Repro, keine bestätigte Severity: Admin-only Werkzeug und gezielte Spezialsyntax statt belegtem täglichem Funktionsfehler. Mit Root im aktuellen Umfang zurückgestellt.
- **Agent-Registry fehlt in portabler Sicherung:** kein Vertragsbruch; `docs/ai-agents.md` schließt Registries ausdrücklich aus und verlangt native Datenbank-/Schlüsselsicherung.
- **Retry ohne erneutes Maintenance-Gate:** dokumentierte Incident-Recovery-Semantik, kein behaupteter Autorisierungsbypass.
- **Raw-Output im Live-Notifier:** Runtime-Agent prüfte Emitter zusätzlich frisch; StepRunner liefert redigierte Output/Error/Trace-Werte, WorkflowEngine redigiert terminale Fehler. Kein Raw-Output-Leak dieser Kette bestätigt.

## Architektur und Sicherheits-Trace

ResourceAuthorizationService bündelt globale Rollenkappe, Folder-Vererbung und authoritygebundene Gruppenzuordnung. Entfernung würde dieselben Sicherheitsregeln in vielen Controllern und Runtime-Aufrufern vervielfachen. LDAP/OIDC/SCIM besitzen unterschiedliche Identitäts- und Freshnessverträge; bloße Ähnlichkeit rechtfertigt keine Zusammenlegung.

Backup-Parts kapseln echte Ressourcenunterschiede, während ResourceReferences/SecretRewriter gemeinsame Remap-/Secretregeln bereitstellen. WorkflowDefinitionDocument wird von API, Engine und AI für Parser/Index/Facts genutzt. Das sind konkrete Vertiefungen mit mehreren Verbrauchern; kein zusätzlicher Wrapper oder kosmetisches Dateisplitting ist erforderlich. Große Controller/Restore-Dateien allein ergeben keinen Medium-Befund.

Authentifizierung prüft JWT, JTI, Session-ID, SecurityStamp und globale Rolle serverseitig. LDAP/OIDC-Autorität und frische Gruppensnapshots werden nicht durch UI-Rollen ersetzt. Gruppenentzug führt zu dauerhafter Session-/Execution-Invalidierung; Postcommit-Arbeit hängt nicht einfach am abgebrochenen HTTP-Request. Der Switcher-Schlüssel ist ein dokumentierter lokaler privilegierter Zugang mit festen Requestformen; Remote-/Forwarding-Konstellationen und konkurrierende Authentifizierung werden abgewiesen.

AI-HTTP-Endpunkte starten oder speichern Modellempfehlungen nicht unmittelbar. Team-Entwürfe liefern überprüfbare Patches mit Issues; Workflowwissen ist read-scoped, globaler SQL-Wissenszugang serverseitig Admin-gebunden. DbAdmin-Seams begrenzen Tabellen, geschützte Spalten und Schreibzugriffe. AppLog ist Admin-only, begrenzt Scans, kennzeichnet unvollständige Abdeckung und redigiert vor Suche und Download.

## Unabhängige Root-Gegenchecks

**#137:** PropertiesPanel ohne Node-Key erhält beim normalen Schrittwechsel den initialen Draft von AiAgentConfig/JsonSchemaField/Lines. Identische innere Agent-IDs schützen nicht; ResultSchema liegt außerhalb. Key-Bindung an Workflow-/Node-ID ist die passende bestehende Lebenszyklus-Seam.

**#139:** EdgePropertiesPanel initialisiert `mode` nur einmal; EditorRightPanel verwendet beim Kantenwechsel denselben Komponentenplatz. Wechsel von einfacher auf Ausdruckskante zeigt einen falschen Modus. `setCondition` verändert nur die einfache Bedingung, während ConditionEvaluator den versteckten Ausdruck priorisiert. Normale Bearbeitung zeigt damit etwas anderes als die wirksame Bedingung. Root besitzt beide Fixes und Rerender-Regressionen.

## Grenzen

Keine Live-Installation, kein Deployment, keine realen Dienste, Datenbankserver oder externen Auth-/Modellprovider. Keine neue Dependency-Advisory-Abfrage dieses Teilprüfers. Kein Volltestlauf durch diesen Prüfer. Fremde `image-manifest.json` blieb unverändert. Delegierte Prüfungen und nicht ausgeführte Testverträge sind oben getrennt; außerhalb des engeren Nutzerumfangs zurückgestellte Kandidaten werden nicht als behoben ausgegeben.

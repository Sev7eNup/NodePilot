# Runde 4 – API und Core (abgeschlossen)

Startinventar: `round-4-inventory.json`, 2026-10-09T11:33:25.773219+00:00,
3178 Dateien, SHA-256 `586be1aacd35e96be2906a90bb17a144def44e28778389473d959cf1db6a8e42`.

Frischer rotierter Bereich: gesamtes `NodePilot.Api` und `NodePilot.Core` einschließlich
unveränderter Module und der zugehörigen Verträge/Tests. Beide Skills werden angewandt:
`improve-codebase-architecture` (Verantwortungsgrenzen, tiefe Module, gemeinsame Seams,
Deletion-Test) und `audit-ai-slop-code` (konkrete Eintrittspunkte, Serverautorisierung,
realistische Angreifer-/Deploymentannahmen und Gegenbeweise). CONTEXT und Threatmodel
wurden erneut vollständig gelesen; ADR 0004/0007/0009/0011/0014 für die ersten Familien.

**Alle produktiven API-/Core-Modulfamilien wurden frisch vollständig gelesen.**
Die Runde ist wegen der bestätigten Befunde keine Nullrunde. Alle sechs eigenen bestätigten
Medium-Familien (#77, #79, #91, #97, #100, #113) sind korrigiert und gezielt geprüft.
Runtime-Konfiguration mit möglichen Geheimnissen wird nicht als Quelltextinventar gelesen.
Testdateien werden gezielt zur Verifikation gelesen; keine Behauptung, alle Fixtures seien
zeilenweise geprüft. Build-Ausführung erfolgt ausschließlich nach gemeinsamem Slotplan.

## Tatsächlich geprüfte Familien

| Familie / tatsächliche Eintrittspunkte | Architekturprüfung | Sicherheitsprüfung / Ergebnis |
| --- | --- | --- |
| Program vollständig, AuthenticationSetup, SecurityPipelineSetup, RateLimitingSetup | Singleton-/Scoped-Weiterleitung, Options-Snapshots, Middleware-Reihenfolge und Zuständigkeiten | Auth vor TokenValidity/CSRF/Authorization, Header-/Cookiepriorität, öffentliches SPA und geschützte Metrics getrennt; keine weiteren C/H/M bestätigt |
| AuthController vollständig, OidcAuthController, AuthSessionIssuer, AuthCookieOptionsBuilder, TokenValidityMiddleware, CsrfMiddleware, UserSessionInvalidation | Gemeinsamer Issuer mit atomarer Refreshrotation; Credential-Nachweis und Reload müssen dieselbe Epoche behalten | **#77 Medium behoben**, reale Issuer-/JWT-/TokenValidity-Regressionen und 157/157 Fokusfälle grün |
| ResourceAuthorizationService, DirectoryGroupPrincipal, SubWorkflowAuthorizationResolver, ExternalAuthorizationEvaluator, DirectoryMembershipReconciler | Gemeinsame authority-genaue Gruppen-Seam für HTTP und Hintergrund; pro Request begrenzte Caches | **#79 Medium bestätigt**, frische Admission/GlobalRole-Gruppen halten veraltete reine Ordnergruppen nicht frisch; Loader muss deren LastSeenAt berücksichtigen |
| OidcIdentityMapper vollständig und OidcTicketStore | Atomare Identitäts-/Membership-Reconciliation, Single-use Ticketdelete, keine automatische Username-Zusammenführung | Vollständige vs. overage Gruppen-Snapshots verfolgt, Issuer/Subject und Case-Verträge geprüft; #79 aus partiellen Snapshots erklärt |
| SCIM Authentication/Authorize, Users/Groups/Base-Controller, ProvisioningService bis zum Regex-Tail, AdminScimGroupsController | Dünne HTTP-Adapter, transaktionale Service-Seam mit Audit und Postcommit-Cancellation; partielle Gruppensnapshots | Gegencheck #79 bestätigt realen Zustand durch einzelne SCIM Group-PUT/PATCH; separate SCIM-Bearertokens und Authoritygrenzen geprüft |
| ExecutionHub einschließlich aller Registrys, HubRevocationSweeper, WorkflowLiveSubscriptions, DatabaseAvailabilityHubFilter, SignalRExecutionNotifier | Subscription-Ownership, gebündelte Revalidierung und nicht abbrechbare Postcommit-Bereinigung | #79 einschließlich bestehender Streams behoben; 101/101 Fokusfälle grün; #100 verwendet denselben Move-Seam |
| Alle 48 Controllerdateien und zugehörige DTOs (45), einschließlich Agents/AgentSkills/MCP/Ai, Workflowediting/-portability/-telemetry, Ausführung/Debug, Betrieb/Settings/Backup/DbAdmin/Alarmierung | HTTP-Adapter gegen Dienst-/Runtimeverträge, Mutationsownership, Checkout-CAS, gemeinsame Fehlerbehandlung und Redaction verfolgt | Rollen/Folders vor Lesedaten und Mutationen, konkurrierende Änderungen, Import-/Exportgrenzen; #91/#97/#100 unten dokumentiert |
| Gesamtes Security-Verzeichnis (60 Dateien), einschließlich LDAP, OIDC, SCIM, Token/Session/CSRF, Bootstrap und Admin-Invarianten | Gemeinsame Identitäts-/Gruppen-Seams, transaktionale Änderungen und Cachegenerationen | #77/#79; AD-Vollsnapshot nicht mit OIDC-Einzelgruppensnapshot verwechselt; Authority- und Berechtigungsgrenzen gegengeprüft |
| Hosting (26), HealthCheck, Program, Projektdatei, beide versionierten appsettings und launchSettings | Lebensdauer, Boot/Readiness, Failover/Fencing, Options-Sizing, Middleware und Publish-Artefakte | Produktionsdefaults, TLS/Hostguard, Development-Ausnahmen und Ausschluss lokaler Runtime-/Developmentdateien beim Publish geprüft |
| Configuration (22) einschließlich SettingsSections, Bootvalidatoren, RuntimeOverridesWriter und verschlüsseltem Provider | Gemeinsamer Schema-/Secret-/ETag- und Reloadvertrag; kein zusätzlicher universeller Konfigurationsadapter nötig | Atomare Listen, Sentinelwerte, Keyrotation/Recovery, Env-/CLI-Priorität; Rotationfix #81 wird von architecture_explore verantwortet |
| Alle Services (48): Backup einschließlich Parts, DbAdmin, Dashboard, Workflowfacts/Version/Contract, Recovery/Seeder/Stats/Probe/HostIdentity | Verantwortungsgrenzen, Importtransaktion, Cacheownership, gebündelte Datenabfragen, bestehende tiefe Module | #91/#97/#100 behoben; #113 Commit-ACK noch in Regression. Globaler administrativer SQL-Write ist ein expliziter Wartungsvertrag |
| Ai (5), ExecutionDispatch (5), Audit (2), Diagnostics (5), Logging (5), Export, Filter, Telemetry und Observability-Services | Streaming, zentraler Dispatch, Fehler-/Logprojektion und Redaction; DTOs zum Verbraucher verfolgt | SQL-/Settings-Knowledge, Cancellation, Secretidentifier, Liveausgabe und Logexport geprüft; kein weiterer bestätigter C/H/M |
| Core WorkflowDefinitions (13), Validation (3), Operations und Interfaces (24) | Parser/Facts/Analyzer/Contract/Layout statt separater Controllerinterpretationen; Callgraph und Storeverträge | Struktur-/Kantenprüfung, Redaction, Subworkflowreferenzen, Alias- und Publishervertrag; #91 betrifft nur die API-Readprojektion |
| Core Activities (11 einschließlich des gesamten eingebetteten Configkatalogs) und Agents (4) | Zentraler Katalog/typisierte Konfiguration, Custom-Parametervertrag, Budget- und Toolauswahlseams | Ein-/Ausgabe- und Promptkontrakte, keine aus Toolbeschreibungen abgeleiteten Grants; relevante Catalog-/Executor-Vertragstests gelesen |
| Core Models (43), Enums (12), Exceptions (2), Audit (9), ExecutionDispatch | Persistente Domänenbegriffe, Auditdetails/Actorstaging, Fehler- und Zustandsverträge gegen API-Verbrauch | Secret-/Benutzer-/Directory-/Execution-/Custom-/Alarm-/Maintenance-/Host-/Outboxdaten und Idempotency-Ownership geprüft |
| Core Clients (14) | Gemeinsame HTTP-Fehler/Paging/TLS, Session-CAS und prozessübergreifende Tokenkoordination | Zertifikatspin/-rotation, Originbegrenzung beim Refresh, DPAPI-Dateien und Sessionwechsel geprüft; Backend ist konfigurierte Vertrauensgrenze |
| Core Security, Configuration, Net, Time, Triggers (2), Telemetry und Projektdatei | SQL-Lexer gemeinsam mit API/MCP, reine Sizingentscheidung ohne Core→Hosting-Abhängigkeit, zentrale Proxy-/Zeitverträge | Konservative SQL-Prädikate sind kein Sandboxversprechen; Sizinggrenzen, Triggernormalisierung, Proxyglobs und Abhängigkeitsrichtung geprüft |

## Gegenargumente / verworfene Kandidaten

- #77 erfordert ein enges Interleaving mit einem autorisierten Passwortwechsel. Es ist
  keine allgemeine Passwortresetumgehung ohne zuvor passendes Passwort/gültiges Token.
  Die normale spätere Tokenprüfung verwirft alte Stamps; der Fehler ist gerade deren
  unbeabsichtigte Übernahme in einen neu erzeugten Token.
- Ein deaktivierter Benutzer erhält durch bloßes spätes Minting keinen generell nutzbaren
  Token: TokenValidity prüft den aktuellen Aktivstatus. Diese Beobachtung begrenzt #77.
- SCIM-Provisioner und Admin-Konfiguration sind privilegierte Vertrauensgrenzen. Ein
  absichtlich ungültiger Provisioner-Payload oder fehlerhafte TrustedProxy-Konfiguration
  wird nicht ohne konkrete Nutzerfolge als Medium erklärt.
- Das öffentliche SPA-Fallback und statische Dateien sind dokumentiert; geschützte API-
  und Metrics-Endpunkte behalten serverseitige Authentisierung. Kein aus bloßer
  Middleware-Reihenfolge abgeleiteter Bypass.
- Root-UI-Gegenprüfung #82: alter Operations-Store kann verspätete Events behalten,
  aber die Seite filtert anhand ihrer aktuellen Workflow-Scope. Low statt unbelegter
  Cross-Folder-Offenlegung; vorhandene AuthBoundary-Seam ist ausreichend.

## Fortschreibung nach Auth-/Hub-Regressionslauf

- #77 und der HTTP-/Subworkflow-Anteil von #79 sind korrigiert: vier der acht neuen
  realen Auth-/Ordnerfälle waren zuvor rot; der gemeinsame Auth-Fokus ist 157/157 grün.
  Passwortnachweis und Refresh übernehmen keine spätere Sicherheitsversion mehr.
- Der Hub-Anteil von #79 ist ebenfalls korrigiert: drei der fünf neuen Sweep-Fälle
  waren zuvor rot, zwei positive Fälle blieben grün. Der gemeinsame Hub-/Folder-/
  Resource-/Subworkflow-Fokus ist 101/101 grün, ohne übersprungene Fälle. Der Sweep
  prüft OIDC-Ordnerscopes vorausschauend für seinen nächsten Takt, isoliert vom normalen
  Requestcache; Workflow-/Execution-Zuordnungen werden gebündelt gelesen und Scopes
  je Benutzer wiederverwendet. Ein fehlgeschlagenes Entfernen beendet die Verbindung.
- Hosting inzwischen vollständig gelesen: DbContextSetup, ClusterSetup/Fencing/
  FailoverRecovery, DatabaseBootService, AvailabilityOptions/Probe/Middleware,
  ReadinessGate, RecoveryAudit, UnavailableResponse/ExceptionHandler, Timeout-/Capacity-
  Handler, RemoteExecutionSetup, KestrelHttpsConfigurator, DataProtectionSetup,
  DocsSiteSetup, OpenApiSetup, BackgroundServicesSetup, LoggingSetup,
  ThreadPoolTuningService und SecurityHardeningWarnings sowie DatabaseReadyHealthCheck.
  Provider-/Retry-/Fencing-Lebenszyklen und Ressourcenfreigabe geprüft; kein zusätzlicher
  C/H/M-Befund. Statische Docs stammen aus vertrauenswürdigem Deployment, nicht aus
  dem schreibbaren Knowledge-Korpus; daraus kein gleicher Symlink-Befund abgeleitet.
- LDAP-/Directory-Lebenszyklen vollständig gelesen: ExternalUserMapper,
  DirectorySynchronizationService, ExternalAuthorizationStalenessService,
  SystemLdapConnectionAdapter, LdapAuthenticator/CircuitBreaker/Endpoint/
  UsernameNormalizer/ILdapConnectionAdapter/GlobalRoleResolver/HealthCheck,
  ActiveDirectoryAuthenticationConfiguration, LdapOptions und WindowsAuthOptions.
  LDAPS/Referrals, DC-Konsens, Teilfehler, JIT/Identity-Kollisionen, Leaderfences,
  Suspendierung und Postcommit-Invalidierung verfolgt; kein zusätzlicher C/H/M-Befund.
- Ergänzende Security-Quellen vollständig: ActiveAuthenticationConfiguration,
  ExternalLoginThrottle, AuthenticationPolicyOptions, LeaderRequiredMiddleware,
  OidcCallbackLeaderFenceMiddleware, AdminAccountMutationGate, BreakGlassAccountPolicy,
  EnterpriseRecoveryInvariant, AuthSessionCleanupService, RevokedTokensCleanupService,
  JwtKeyResolver, AdminBootstrap und RestrictedFileWriter. Abgelaufene Revocation-
  Einträge erlauben wegen des persistenten Sessionvertrags keinen belegten Replay.
- ExecutionHub einschließlich aller Registry-Methoden, HubRevocationSweeper,
  DatabaseAvailabilityHubFilter und WorkflowLiveSubscriptions vollständig gelesen.
- UsersController, SharedFolderPermissionsController, SharedWorkflowFoldersController,
  FolderScopedQueries, ResourceAuthorizationGateExtensions, WorkflowsControllerBase
  und WorkflowsController vollständig gelesen. Quellenprüfung umfasst Listen-/Detail-
  Redaction, Checkout-/Versionskonkurrenz, Foldermove/Delete, Publisherautorität,
  Enable/Disable und Concurrency-Gate. Kein zusätzlicher C/H/M-Befund bestätigt.

## Weiterer tatsächlicher Quellenfortschritt

Weiterer tatsächlicher Quellenfortschritt: WorkflowEditingController,
WorkflowTelemetryController, WorkflowImportExportController, ExecutionsController,
ExecutionDebugController, OperationsController, TriggersController,
ExternalTriggerController, WebhooksController und SignalRExecutionNotifier vollständig.
Sämtliche ExecutionDispatch-Dateien sowie WorkflowPortability, ContractDeriver,
VersionDefinitionProtector, DefinitionFactsCache/Warmup ebenfalls vollständig.
Verfolgt wurden Publisher-/Run-/Editrechte, Checkout-CAS, verschlüsselte Outboxdaten,
Retry-/Maintenance-Ausnahmen, HMAC-Kanonisierung und Replay-Ownership, Importremapping,
Redaction und gebündelte Liveausgabe. HMAC-Claims bleiben gemäß explizitem Storevertrag
auch bei späterem Admissionfehler verbraucht; erneutes Senden benötigt eine neue Delivery-ID.

**#91 Medium bestätigt und behoben:** Beide Contract-Routen lieferten Literaldefaults aus
manualTrigger-Parametern an bloße Leser aus, obwohl der reguläre Detailpfad diese maskiert.
Der gemeinsame Controller-Seam maskiert nur vorhandene Defaults bei fehlendem Editrecht.
Die Core-Ableitung und Runtimewerte bleiben unverändert. Sechs echte Rollen-/Folderfälle
(beide Routen pro Fall) ergaben vorher vier rote und zwei positive Fälle; danach 70/70
Workflow-/Contracttests grün. ContractMappingTable wurde zur Erhaltung von null-vs.-Default
und zur Vermeidung unbeabsichtigter Parameterübernahme vollständig gegengelesen.

Weiterhin vollständig gelesen: CredentialsController, MachinesController,
GlobalVariablesController, GlobalVariableFoldersController, CustomActivitiesController,
ActivityCatalogController und MachineStepStatsCache. Globale Ressourcen bleiben gemäß
Trustmodell global; Custom-Live-Mutationen verwenden den Store-CAS. Ferner AuditController,
SystemController, DiagnosticsController, ObservabilityController, beide Audit-Dateien,
alle fünf Diagnostics-Dateien, CsvWriter sowie ScimDiscoveryController, SecretComparer,
RoleExtensions, LeaderOnlyAttribute, WorkflowScriptLinter, ExternalExecutionCancellation,
WebhookHmacSecurity, ExternalTriggerKeyScopeResolver, AgentPublishValidation und
ScheduleCronValidation. DTOs Workflow/ImportExport/Execution/Debug/StepTest/Trigger und
CustomActivity vollständig. Core ContractDefinition/SecretRedactor/SecretKeys und
CustomActivityParameters im tatsächlichen API-Verbrauch geprüft.

Weiterer vollständiger Quellenblock: BackupController, BackupService, BackupFileReader,
BackupRestoreService, sämtliche zehn Backup-Parts, IBackupPart, RestoreState,
BackupRestoreModels, WorkflowRestoreTargets, FolderTreeShape, BackupCanonicalJson und
WorkflowDefinitionSecretRewriter; ADR 0001 vollständig nachgelesen. Der Restorepfad wurde
von verschlüsseltem Archiv über Referenzabbildung und Transaktion bis zur Live-Revoke-
Nachbereitung verfolgt. #97 Medium wurde unabhängig bestätigt: fehlendes Laden alter
Alarmrouten/-targets beim Überschreiben. Zwei echte Overwrite-Fälle rot, Skip positiv;
nach Include beider Navigationen im vorhandenen Loader 84/84 Backup-Fälle grün.

DbAdminController und alle acht DbAdmin-Service-/Vertragsdateien vollständig gelesen:
Metadaten → Spaltenpolicy → typisierte Zellmutation, Benutzer-Invariantengate,
SQL-Read-Guard, Secret-Identifier/Whole-row-Sperre, Transaktion und Audit. Ein weiterer
Kandidat wurde unabhängig als #100 Medium bestätigt: regulärer Workflow.FolderId-Zell-PATCH
ohne Live-Abonnemententzug. Echter lokaler Viewer trat über ResourceAuthorizationService
und Hub bei; der reale Notifier lieferte nach dem Move weiter Ausgaben (RED). Der Zellpfad
nutzt nun TreeLock/Capture/Revoke inklusive Projektion-/Dashboardcache-Invalidierung.
Namens-Zelländerung erhält den Feed und alle 229 DbAdmin-/Move-/Notifier-Fälle sind grün.
Der explizit bestätigte Raw-SQL-Schreibmodus ist dagegen gemäß
Optionsvertrag ein administrativer Bypass und kein neuer Befund.

DashboardController und alle sieben Dashboard-Service-Dateien vollständig gelesen:
Scope-Schlüssel, Cachegeneration, Warmup-Scopeownership, Rollup-/Rohdatenpfad,
Duration-Quantile, Retry-Deduplikation und Fehlermeldungsredaktion. AlertingController,
SystemAlertingController, AlertingRuleMapping und MaintenanceWindowsController komplett;
ADR 0008 bestätigt bewusst globale Admin/Operator-Leserechte für Alarmverwaltung, daher
wurde diese Oberfläche nicht ohne Gegenbeleg als Folder-RBAC-Umgehung eingestuft.

AdminSettingsController und sämtliche Configuration-Dateien einschließlich sieben
Bootvalidatoren komplett gelesen: alle Adapterfamilien, Secret-Sentinel-Roundtrip,
atomare Listen, Env-/CLI-Priorität, ETag-Mutex, Verschlüsselung, Rotation/Backups,
Runtime-Provider und Startup-/Reloadvertrag. Ebenso SecretsController,
AgentRecoveryService und ProvisioningSeeder vollständig. Ebenso nun sämtliche API-DTOs,
kleine LDAP-Verträge, Loggingformatter, Observability-Services, SettingsTestProbe,
DatabaseSecretRotation, HostIdentityProvider, ExecutionStatsRollupService, ApiMetrics,
ApiProblems und ApiProblemDetailsResultFilter. Diese DTOs wurden gegen die zuvor gelesenen
Controller/Adapter verfolgt; nicht nur als Dateiinventar gezählt.

Core inzwischen vollständig: WorkflowDefinitions (Parser, Struktur-/Kantenprüfung,
Analyzer, DataBus, Layout, References, Secret-/Contract- und Facts-Ableitung), Validation,
Operations, sämtliche Interfaces, Agents, Enums, Exceptions, ExecutionDispatch, Triggers,
Time, Net und Telemetry. Activities-C# komplett, eingebetteter Configkatalog noch offen.
Modelle Workflow/Version/Graph/Metadata/Execution/StepExecution und Auth-/Directory-/
SharedFolder-/GlobalFolderfamilie vollständig; zugehörige API-Verwendung oben geprüft.
ADR 0013/0015/0016/0017 erneut vollständig gelesen. Kein zusätzlicher C/H/M-Befund in
diesem Quellenblock bestätigt. Alias-Großschreibung und Analyseheuristiken sind keine
neue Autorisierungsgrenze; Layout verändert keine ausführbaren Node-Konfigurationen.

## Abschluss der Quellenabdeckung / Grenzen

Der zuletzt offene Block wurde ebenfalls vollständig gelesen: sämtliche verbleibenden
Core-Modelle, Audit und Clients, PerformanceSizing, SqlStatementInspector und alle 557 Zeilen
des eingebetteten Configkatalogs. API-Projektdatei, versionierte Default-/Development-
Konfiguration und launchSettings sind abgeschlossen. Die Matrix beschreibt tatsächlich
gelesene produktive Modulgruppen; die Dateizahlen sind das Abschlussinventar dieser beiden
Projekte und werden nicht als Beweis für eine Zeilenprüfung sämtlicher Tests verwendet.

Zusätzlich wurden passende bestehende Contracttests (unter anderem Sizing, ActivityConfig,
Client-TLS/Refresh, Workflowdefinitionen und Restore) sowie alle eigenen neuen Regressionen
gegen die Produktivverträge gelesen. Es liefen keine realen LDAP-/OIDC-/SCIM-Provider,
externen Datenbanken oder produktiven Restorevorgänge. Die konkreten Laufnachweise oben
sind fokussierte lokale Tests; breite Suitezahlen werden zentral im Hauptrundenbericht geführt.

Backup-Commit-ACK #113 wurde durch einen echten SQLite-Commit mit anschließend geworfener
ACK-Ausnahme bestätigt: Workflow dauerhaft verschoben, Settings fälschlich kompensiert.
Der positive Vorcommit-Fehler behielt dagegen korrekt den alten Zustand. Ein stabiler
Auditmarker in derselben Transaktion ermöglicht nun eine unabhängige, auf fünf Sekunden
begrenzte Verifikation nach Freigabe der Transaktion. Bestätigter Commit durchläuft die
normale Live-Bereinigung; fehlender Marker erlaubt die Kompensation; unerreichbare
Verifikation behält Settings, entzieht Livezugriffe vorsorglich und verbietet automatisches
Replay. Kein neuer Retrybesitzer und keine neue Persistenztabelle. Der dritte Fehlerfall
prüft unerreichbare Verifikation. Alle 87 Backup-Fälle sind grün. Es wurden keine echten
Netzwerkausfälle behauptet: die Interceptoren werfen vor bzw. nach dem realen lokalen Commit.

Der Architektur-Deletion-Test führte zu bestehenden gemeinsamen Seams statt neuen Schichten:
DirectoryGroupPrincipal übernimmt Gruppenfrische für mehrere Leser, WorkflowLiveSubscriptions
besitzt auch den DbAdmin-Move, der Contract-Controller maskiert die gemeinsame Ausgabe und
der bestehende Backup-Loader lädt für Overwrite die tatsächlich ersetzten Childcollections.
Die großen Controller-/Restoredateien allein wurden nicht als Medium eingestuft; die
Trennung nach tatsächlichen Verträgen und die vorhandenen Regressionen tragen die Korrekturen.

## Ergänzung aus dem nachgelagerten Designer-Review: #129

Die bewusste 500er Begrenzung des Arrayendpunkts war mit rein clientseitigen Ordnerfiltern
der UI unvereinbar. Ein gemeinsamer `/workflows/paged`-Read verwendet dieselbe begrenzte
Response-/Statsprojektion; RBAC, Ordner, Suchtext und globale Sortierung gelten vor Paging.
Maximal 200 Zeilen pro Antwort, optional höchstens zehn IDs für den Recents-Batch.
Der reale CLI-/MCP-Arrayverbraucher bleibt erhalten und ist im bestehenden Endpointgap-
Register ausdrücklich dokumentiert, ohne die Coverageprüfung allgemein aufzuweichen.
86/86 Workflow-/RBAC-Fokusfälle grün; danach 17/17 Pagingfälle mit realer SQLite-Ausführung
und Offline-Übersetzung aller neun Sorts in beiden Richtungen für PostgreSQL/SQL Server.
Keine Live-Providerprüfung behauptet. Details/Designer-Restscope in round-4-designer.md.

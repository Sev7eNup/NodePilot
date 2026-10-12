# Runde 5 – ergänzende Hosting-/Konfigurationsprüfung

Übernahme durch Root aus dem API-Scope, Basis `de71058`, 12. Oktober 2026.
Risikobasierte Quellenprüfung; kein bestätigtes Critical/High/praktisches Medium.

Gelesene zentrale Pfade: `Program.cs` als Composition Root, AuthenticationSetup,
SecurityPipelineSetup, RateLimitingSetup, KestrelHttpsConfigurator,
DataProtectionSetup, DocsSiteSetup, DbContextSetup, DatabaseBootService,
DatabaseReadinessGate, AvailabilityMiddleware/Probe und Fehlerantworten,
DatabaseRecoveryAuditService, ClusterFencingHost/FailoverRecoveryHost,
BackgroundServicesSetup, RemoteExecutionSetup, LoggingSetup, OpenApiSetup,
ThreadPoolTuningService. Geprüft wurden Reihenfolge der Middleware,
Session-/CSRF-Einbindung, Defaultschemes und OIDC, DB-Start/503/Recovery,
Zertifikatsbindung, Host-/Proxygrenzen, DI-Lebenszeiten und anonyme statische Pfade.

Konfiguration: RuntimeOverridesSetup/Writer, EncryptingJsonConfigurationProvider,
SettingsSchema und sicherheitsrelevante SettingsSections-Pfade, insbesondere
SMTP/LLM/Auth/Telemetry sowie Secret-Erhalten/Ersetzen. ETag-Prüfung und Schreiben
liegen unter derselben Sperre; Masken werden nicht als neue Geheimnisse gespeichert.
ProviderAtomicGuidList, AuthenticationOptionBinding, EffectiveSourceDetector,
LlmProxyOptionsPostConfigure, BootValidatorRunner und Auth-/TLS-/Deployment-/
Secrets-Validatoren wurden gegen die Consumerverträge geprüft.

Die umfangreichen tabellarischen Settings-Mapper und Options-/Warnungskataloge
wurden risikoorientiert betrachtet, nicht jede wiederholte skalare Zuweisung einzeln
als unabhängig verifiziert gewertet. Keine rein größenbasierte Aufspaltung:
SettingsSectionAdapter und RuntimeOverridesWriter bilden bereits die gemeinsamen
Validierungs-/Persistenzgrenzen. Keine Produktänderung oder neuer Test in diesem
Teilbereich; Auth/Settings-Controller und Consumer sind zusätzlich durch andere
Reviewer im API-/Runtime-Scope abgedeckt.

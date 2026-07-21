using System.Collections.Immutable;
using NodePilot.Core.Audit;
using NodePilot.Ai;
using NodePilot.Api.Dtos.Settings;
using NodePilot.Api.Security.Ldap;
using NodePilot.Engine.Options;
using NodePilot.Scheduler.Options;
using NodePilot.Telemetry;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Static catalog of all configuration sections exposed through the Admin Settings API.
/// Each entry pins down:
/// <list type="bullet">
///   <item><b>SectionPath:</b> dot-/colon-separated path into <see cref="IConfiguration"/></item>
///   <item><b>OptionsType:</b> the strongly-typed POCO the section binds to</item>
///   <item><b>DtoType:</b> the API-facing DTO (Secret fields surface as masked strings)</item>
///   <item><b>SecretFieldPaths:</b> keys inside the section whose values must be masked
///   on read and accept the <c>__unchanged__</c> sentinel on write</item>
///   <item><b>IsHotReloadable:</b> false means a Save also writes the restart-marker so
///   the UI can surface the orange banner</item>
///   <item><b>AuditCode:</b> the action string written to the audit log on a successful Save</item>
/// </list>
///
/// <para>This is metadata only. Section-specific payload mapping, defaults, validation
/// surface, secret handling, and JSON persistence shape live in explicitly registered
/// Settings Section Adapters.</para>
/// </summary>
public static class SettingsSchema
{
    public const string UnchangedSecretSentinel = "__unchanged__";

    public static readonly ImmutableArray<SettingsSectionDescriptor> Sections = ImmutableArray.Create(
        new SettingsSectionDescriptor(
            SectionPath: "Smtp",
            DisplayName: "SMTP",
            OptionsType: typeof(SmtpOptions),
            DtoType: typeof(SmtpSettingsDto),
            SecretFieldPaths: ImmutableArray.Create("Password"),
            // Hot-reload: SmtpNotificationSink + EmailActivity read IOptionsMonitor<SmtpOptions>.CurrentValue
            // per send, so a Settings-UI save takes effect without a restart.
            IsHotReloadable: true,
            AuditCode: AuditActions.SettingsSmtpUpdated),
        new SettingsSectionDescriptor(
            SectionPath: "Llm",
            DisplayName: "LLM (KI)",
            OptionsType: typeof(LlmOptions),
            DtoType: typeof(LlmSettingsDto),
            SecretFieldPaths: ImmutableArray.Create("ApiKey"),
            // Hot-reload: ILlmClientFactory + the controller gates read IOptionsMonitor<LlmOptions>.CurrentValue
            // per use, so a Settings-UI save (incl. the Llm:Enabled kill-switch) takes effect without a restart.
            IsHotReloadable: true,
            AuditCode: AuditActions.SettingsLlmUpdated),
        new SettingsSectionDescriptor(
            SectionPath: "AiKnowledge",
            DisplayName: "AI-Wissen (Chat)",
            OptionsType: typeof(AiKnowledgeOptions),
            DtoType: typeof(AiKnowledgeSettingsDto),
            SecretFieldPaths: ImmutableArray<string>.Empty,
            // Hot-reload: the knowledge chat orchestrator, tool registry, and the capabilities
            // endpoint read IOptionsMonitor<AiKnowledgeOptions>.CurrentValue per use, so a
            // Settings-UI save (source toggles, root paths) takes effect without a restart.
            IsHotReloadable: true,
            AuditCode: AuditActions.SettingsAiKnowledgeUpdated),
        new SettingsSectionDescriptor(
            SectionPath: "Retention",
            DisplayName: "Retention",
            OptionsType: typeof(RetentionOptions),
            DtoType: typeof(RetentionSettingsDto),
            SecretFieldPaths: ImmutableArray<string>.Empty,
            // Hot-reload: the retention sweepers read IOptionsMonitor<RetentionOptions>.CurrentValue (or
            // IConfiguration) per pass with sleep-and-continue, so a Settings-UI save takes effect without a
            // restart. ArchivePath changes re-probe on the next pass.
            IsHotReloadable: true,
            AuditCode: AuditActions.SettingsRetentionUpdated),
        new SettingsSectionDescriptor(
            // Authentication is a logical pair (Ldap + Windows). Persist under a top-level
            // "Authentication" key so the override file naturally nests "Authentication.Ldap"
            // and "Authentication.Windows" sub-blocks — same layout the host already reads.
            SectionPath: "Authentication",
            DisplayName: "Authentifizierung",
            OptionsType: typeof(LdapOptions),
            DtoType: typeof(AuthenticationSettingsDto),
            // Directory, OIDC and SCIM credentials are masked/encrypted. The LDAP
            // test-probe accepts the sentinel and resolves it against persisted options.
            SecretFieldPaths: ImmutableArray.Create(
                "Ldap.ServicePassword", "Oidc.ClientSecret", "Scim.BearerToken",
                "Scim.PreviousBearerToken"),
            // LDAP options are bound via IOptionsMonitor (AuthController reads CurrentValue
            // on every request), but the Negotiate scheme is registered at startup so a
            // toggle on Windows:Enabled needs a restart. Conservative: report as Restart.
            IsHotReloadable: false,
            AuditCode: AuditActions.SettingsAuthenticationUpdated),
        new SettingsSectionDescriptor(
            // Logging is its own root: format, log-levels, file sink, redaction.
            SectionPath: "Logging",
            DisplayName: "Logging",
            OptionsType: typeof(object),  // Serilog reads from raw IConfiguration, no POCO binder
            DtoType: typeof(LoggingSettingsDto),
            SecretFieldPaths: ImmutableArray<string>.Empty,
            // Restart-required: Serilog reads from raw IConfiguration once at boot; the logger pipeline
            // (sinks/format/levels) is not re-built in-process. No live consumer.
            IsHotReloadable: false,
            AuditCode: AuditActions.SettingsLoggingUpdated),
        new SettingsSectionDescriptor(
            // OpenTelemetry: full OTLP/Sampling/Exporters/Prometheus block. Prometheus
            // password + bearer token are the secrets.
            SectionPath: "OpenTelemetry",
            DisplayName: "OpenTelemetry",
            OptionsType: typeof(NodePilotTelemetryOptions),
            DtoType: typeof(OpenTelemetrySettingsDto),
            SecretFieldPaths: ImmutableArray.Create("Prometheus.Password", "Prometheus.BearerToken"),
            // Restart-required: the OTel SDK + exporters are built once at boot (NodePilot.Telemetry
            // setup); no in-process rebuild of the exporter pipeline.
            IsHotReloadable: false,
            AuditCode: AuditActions.SettingsOpentelemetryUpdated),
        new SettingsSectionDescriptor(
            SectionPath: "Stats",
            DisplayName: "Stats",
            OptionsType: typeof(object),
            DtoType: typeof(StatsSettingsDto),
            SecretFieldPaths: ImmutableArray<string>.Empty,
            // Hot-reload: WorkflowStatsRefresher re-reads Stats:RefreshIntervalMinutes / WindowDays per pass
            // from IConfiguration, so a Settings-UI save takes effect without a restart.
            IsHotReloadable: true,
            AuditCode: AuditActions.SettingsStatsUpdated),
        // DbAdmin SQL console controls. Hot-reload-capable: the executor consumes
        // IOptionsMonitor<DbAdminOptions>, so settings-UI edits land without restart.
        // No secrets in this section — AllowWriteQueries is the only sensitive field
        // and it's a boolean, not a credential.
        new SettingsSectionDescriptor(
            SectionPath: "DbAdmin",
            DisplayName: "Database Admin",
            OptionsType: typeof(NodePilot.Api.Services.DbAdmin.DbAdminOptions),
            DtoType: typeof(DbAdminSettingsDto),
            SecretFieldPaths: ImmutableArray<string>.Empty,
            IsHotReloadable: true,
            AuditCode: AuditActions.SettingsDbadminUpdated),
        // Security hardening — seven small flat sections grouped under the UI's "Sicherheit" tab.
        new SettingsSectionDescriptor("RestApi", "REST API Outbound", typeof(NodePilot.Engine.Options.RestApiProxyOptions),
            typeof(RestApiSettingsDto), ImmutableArray.Create("Proxy.Password"),
            // Restart-required (mixed section): RestApiActivity binds RestApiProxyOptions once at boot into the
            // activity's outbound HTTP client config; the BlockPrivateNetworks hardening flag is live,
            // but section-granularity can't split them → conservative restart.
            false, AuditActions.SettingsRestApiUpdated),
        // Hot-reload: PathGuard reads FileSystemOperation:RejectTraversal / AllowedRoots from the live
        // IConfiguration indexer on every file-op validation call (FileOperation/FolderOperation/
        // TextFileEdit/Zip/FileHash/XmlQuery/JsonQuery/StartProgram), so a Settings-UI save takes
        // effect without a restart.
        new SettingsSectionDescriptor("FileSystemOperation", "File-System Activities", typeof(object),
            typeof(FileSystemOperationSettingsDto), ImmutableArray<string>.Empty, true,
            AuditActions.SettingsFilesystemOperationUpdated),
        // Hot-reload: SqlActivity reads SqlActivity:RequireConnectionRef from the live IConfiguration
        // indexer on every execution (ResolveConnectionString), so a Settings-UI save takes effect
        // without a restart.
        new SettingsSectionDescriptor("SqlActivity", "SQL Activity", typeof(object),
            typeof(SqlActivitySettingsDto), ImmutableArray<string>.Empty, true,
            AuditActions.SettingsSqlActivityUpdated),
        // Hot-reload: StartProgramActivity reads StartProgram:DisallowShellExecute from the live
        // IConfiguration indexer per execution, so a Settings-UI save takes effect without a restart.
        new SettingsSectionDescriptor("StartProgram", "Start Program", typeof(object),
            typeof(StartProgramSettingsDto), ImmutableArray<string>.Empty, true,
            AuditActions.SettingsStartProgramUpdated),
        // Hot-reload: WebhooksController reads Webhook:RequireSecret from the live IConfiguration
        // indexer on every webhook hit, so a Settings-UI save takes effect without a restart.
        new SettingsSectionDescriptor("Webhook", "Webhook Triggers", typeof(object),
            typeof(WebhookSettingsDto), ImmutableArray<string>.Empty, true,
            AuditActions.SettingsWebhookUpdated),
        // Hot-reload: ExternalTriggerController reads ExternalTrigger:ApiKey from the live
        // IConfiguration indexer per request (apiKey is the active-when-set toggle), so a Settings-UI
        // save takes effect without a restart.
        new SettingsSectionDescriptor("ExternalTrigger", "External Trigger API", typeof(object),
            typeof(ExternalTriggerSettingsDto), ImmutableArray.Create("ApiKey"), true,
            AuditActions.SettingsExternalTriggerUpdated),
        new SettingsSectionDescriptor("Security", "Allowed Hosts", typeof(object),
            typeof(SecuritySettingsDto), ImmutableArray<string>.Empty,
            // Restart-required: StrictAllowedHosts is read once at boot by the host middleware setup;
            // the allowed-hosts list is not re-evaluated per request.
            false, AuditActions.SettingsSecurityUpdated),
        // Performance tuning. All strict-startup — values are cached at boot, save persists
        // them and the operator restarts. The Remote section combines security flag,
        // WinRm timeouts and the connection-pool tuning under one atomic save.
        new SettingsSectionDescriptor("Engine", "Engine Concurrency", typeof(object),
            typeof(EngineSettingsDto), ImmutableArray<string>.Empty,
            // Restart-required: WorkflowEngine concurrency caps are cached at boot; no in-process
            // re-tune of the engine's in-flight/queue bounds.
            false, AuditActions.SettingsEngineUpdated),
        new SettingsSectionDescriptor("ExecutionDispatch", "Execution Dispatch Queue", typeof(object),
            typeof(ExecutionDispatchSettingsDto), ImmutableArray<string>.Empty,
            // Restart-required: ExecutionDispatchWorker queue/channel sizing is constructed at boot.
            false, AuditActions.SettingsExecutionDispatchUpdated),
        new SettingsSectionDescriptor("Threading", "ThreadPool Pre-Warming", typeof(object),
            typeof(ThreadingSettingsDto), ImmutableArray<string>.Empty,
            // Hot-reload: ThreadPoolTuningService re-applies Threading:MinWorkerThreads / MinIoCompletionThreads
            // from the live IConfiguration on start + on every config reload (ChangeToken.OnChange), so a
            // Settings-UI save re-tunes the pool without a restart.
            true, AuditActions.SettingsThreadingUpdated),
        new SettingsSectionDescriptor("Remote", "Remote (WinRM)", typeof(object),
            typeof(RemoteSettingsDto), ImmutableArray<string>.Empty,
            // Restart-required (mixed section): Remote:Provider + RequireWinRmSsl + WinRm timeouts + the
            // connection-pool tuning are bound once at boot into the WinRM session factory; section
            // granularity can't split live vs boot-fested keys → conservative restart.
            false, AuditActions.SettingsRemoteUpdated)
    );

    public static SettingsSectionDescriptor? Find(string sectionPath) =>
        Sections.FirstOrDefault(s => string.Equals(s.SectionPath, sectionPath, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Metadata for one editable section. Used by the controller to map between
/// configuration paths, options types, DTOs, and audit codes without per-section
/// switch statements.
/// </summary>
public sealed record SettingsSectionDescriptor(
    string SectionPath,
    string DisplayName,
    Type OptionsType,
    Type DtoType,
    ImmutableArray<string> SecretFieldPaths,
    bool IsHotReloadable,
    string AuditCode);

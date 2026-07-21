using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.Engine;
using NodePilot.Ai;
using NodePilot.Api.Configuration;
using NodePilot.Api.Hosting;
using NodePilot.Telemetry;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Options;
using Serilog;

// Bootstrap config: read appsettings + env BEFORE the host so the bootstrap logger
// picks the same file format the host logger will use later (avoids CMTrace/JSON
// auto-detection mismatches on the rolling file's first lines).
var bootstrapConfig = LoggingSetup.BuildBootstrapConfiguration();

// Perf: ThreadPool starts with MinThreads = ProcessorCount and only injects new
// workers at ~1-2 per second beyond that. Burst workloads (50+ simultaneous
// /api/execute, webhook fan-in, scheduled-trigger storms) wait seconds for the
// pool to grow. Pre-warming MinThreads to a generous floor eliminates the
// cold-start latency at the cost of some idle threads when load is light.
// Tunable via Threading:MinWorkerThreads / Threading:MinIoCompletionThreads.
{
    var defaultMinWorkers = Math.Max(200, Environment.ProcessorCount * 16);
    var defaultMinIoc = Math.Max(200, Environment.ProcessorCount * 16);
    var minWorkers = bootstrapConfig.GetValue<int?>("Threading:MinWorkerThreads") ?? defaultMinWorkers;
    var minIoc = bootstrapConfig.GetValue<int?>("Threading:MinIoCompletionThreads") ?? defaultMinIoc;
    if (minWorkers > 0 && minIoc > 0)
        ThreadPool.SetMinThreads(minWorkers, minIoc);
}

// Perf: Global step-concurrency cap. Caps the number of in-flight workflow steps
// across ALL executions to ProcessorCount * 32 (default 512 on a 16-core box). Sized
// to absorb a bursty 50-execution × 12-branch fan-out without queueing most steps on
// the gate — keeps the junction-race cancellation path narrow. Set to <=0 to disable.
// startWorkflow(waitForCompletion=true) releases its parent step slot while waiting
// for the child, so queued child steps can still make progress under this cap.
{
    var defaultMaxSteps = Environment.ProcessorCount * 32;
    var maxSteps = bootstrapConfig.GetValue<int?>("Engine:MaxConcurrentSteps") ?? defaultMaxSteps;
    NodePilot.Engine.Execution.WorkflowScheduler.Configure(maxSteps);
}

Log.Logger = LoggingSetup.BuildBootstrapLogger(bootstrapConfig);

var builder = WebApplication.CreateBuilder(args);

// Splice the UI-managed runtime overrides JSON file into the configuration source list,
// directly after appsettings.{Env}.json so the override beats the Installer-Bootstrap
// but still loses to EnvVars/CLI (Deployment-Policy supersedes UI). The default
// builder appends sources to the end, which would put the override AFTER EnvVars and
// silently overrule env-injected secrets — wrong for container/K8s deployments.
//
// The same call builds the secret protector from a bootstrap snapshot (everything
// EXCEPT the override file) so `enc:v1:...`-prefixed values in the override file get
// transparently decrypted during configuration load. Secrets:* / Cluster:Enabled /
// Credentials:DpapiScope must therefore be set in appsettings + EnvVars, never in the
// override file itself — that's enforced by treating those keys as strict-bootstrap
// in the Settings UI (read-only display in the System-Info section).
var (runtimeOverridesPath, _) = builder.AddRuntimeOverridesJson();
builder.Services.AddRuntimeOverridesWriter(runtimeOverridesPath);

// Surface Serilog sink failures (file locked, disk full, flush error) to stderr instead
// of swallowing them. Without this a broken rolling-file sink looks identical to "process
// is just quiet", which once cost us an hour chasing a non-existent bug.
Serilog.Debugging.SelfLog.Enable(Console.Error);

// Serilog is reconfigured through the host so it can read appsettings + DI services;
// the OpenTelemetry sink is added when telemetry is enabled.
LoggingSetup.ConfigureHostLogging(builder.Host);
builder.Host.UseWindowsService();

// Production: bind Kestrel directly to HTTPS using a cert from the Windows cert store.
// No-op when Kestrel:Https:Enabled is false (dev/test default), so the existing --urls
// and launchSettings-driven binding continues to work unchanged.
builder.ConfigureKestrelFromWindowsCertStore();

// OpenTelemetry (traces, metrics, bridged logs via built-in ILogger)
builder.Services.AddNodePilotTelemetry(builder.Configuration, builder.Environment);

// Database: PostgreSQL (default) or SQL Server. SQLite is not supported as an app DB
// provider — it's only used in tests as an in-memory backend (see DbContextSetup).
builder.Services.AddNodePilotDbContext(builder.Configuration);
DbContextSetup.WarnAboutInlinePasswords(builder.Configuration, builder.Environment);

// Authentication, rate limiting + reverse-proxy header trust — see Hosting/* for details.
builder.Services.AddNodePilotDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddNodePilotAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddNodePilotForwardedHeaders(builder.Configuration);
builder.Services.AddNodePilotRateLimiting();
builder.Services.Configure<HostFilteringOptions>(options =>
{
    var allowedHosts = builder.Configuration["AllowedHosts"];
    if (!string.IsNullOrWhiteSpace(allowedHosts))
    {
        options.AllowedHosts = allowedHosts.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
});

// ProblemDetails + global exception handler — in production, uncaught exceptions would
// otherwise return raw stack traces (or worse, SQL error messages) to the client.
builder.Services.AddProblemDetails();
// Security-audit finding H-3: ExecutionCapacityException → 503 + Retry-After. Must be
// registered BEFORE the default handler, otherwise the generic ProblemDetails mapping
// wins first and the client sees a 500 instead of the correct 503.
builder.Services.AddExceptionHandler<NodePilot.Api.Hosting.CapacityExceptionHandler>();

// Health checks — exposed as /healthz/live (process alive, no deps) and /healthz/ready
// (DB reachable). Kept [AllowAnonymous] in the endpoint-mapping step below so Kubernetes /
// Docker probes don't need a JWT. The DB-reachability tag separates the two probes: liveness
// stays green even when the DB is down so the orchestrator doesn't restart us, while
// readiness flips red and traffic routes elsewhere.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<NodePilotDbContext>("database", tags: new[] { "ready" })
    .AddCheck<NodePilot.Api.Security.Ldap.LdapHealthCheck>("ldap", tags: new[] { "directory" });

// Core services
// Pluggable secret protector — DPAPI by default, AES-GCM available for cluster
// deployments. Picks the implementation from Secrets:Provider; both CredentialStore and
// GlobalVariableStore route their encrypt/decrypt through it transparently.
builder.Services.AddNodePilotSecretProtector(builder.Configuration);
// RBAC Tier A: scoped per-request authorization service. Folder-permission lookups are
// cached for the lifetime of the request so list endpoints with N workflows resolve the
// accessible-folder set once, then do O(1) set-membership tests per row.
builder.Services.AddScoped<NodePilot.Core.Interfaces.IResourceAuthorizationService,
    NodePilot.Api.Security.ResourceAuthorizationService>();
// Sub-workflow runtime authorization (Defense-in-Depth for startWorkflow / forEach):
// resolves the effective principal from the parent execution + workflow row and gates
// cross-folder calls. Engine-side, no ClaimsPrincipal involved.
builder.Services.AddScoped<NodePilot.Core.Interfaces.ISubWorkflowAuthorizationResolver,
    NodePilot.Api.Security.SubWorkflowAuthorizationResolver>();
builder.Services.AddScoped<ICredentialStore, CredentialStore>();
builder.Services.AddScoped<IGlobalVariableStore, GlobalVariableStore>();
builder.Services.AddScoped<IGlobalVariableFolderStore, GlobalVariableFolderStore>();
builder.Services.AddScoped<ICustomActivityDefinitionStore, CustomActivityDefinitionStore>();
// Execution-log tools for the AI chat assistant: read-only history, always redacted (see the reader's docs).
builder.Services.AddScoped<NodePilot.Core.Interfaces.IExecutionLogReader, NodePilot.Data.ExecutionLogReader>();
// Instance-wide operational/workflow reader for the global "AI Chat" knowledge assistant:
// RBAC-scoped, secret-redacted, read-only (see the reader's docs).
builder.Services.AddScoped<NodePilot.Core.Interfaces.IOperationalKnowledgeReader, NodePilot.Data.OperationalKnowledgeReader>();
builder.Services.AddScoped<IMaintenanceWindowStore, MaintenanceWindowStore>();
builder.Services.AddScoped<INotificationRuleStore, NotificationRuleStore>();
// Singleton evaluator: an immutable in-memory snapshot read on the dispatch hot path, refreshed
// by the (non-leader-gated) MaintenanceWindowSnapshotService and inline after window CRUD.
builder.Services.AddSingleton<IMaintenanceWindowEvaluator, MaintenanceWindowEvaluator>();
builder.Services.Configure<NodePilot.Api.ExecutionDispatch.ExecutionDispatchOptions>(
    builder.Configuration.GetSection(NodePilot.Api.ExecutionDispatch.ExecutionDispatchOptions.SectionName));
builder.Services.AddSingleton<NodePilot.Api.ExecutionDispatch.ExecutionDispatchQueue>();
builder.Services.AddSingleton<NodePilot.Core.Interfaces.IExecutionDispatchQueue>(
    sp => sp.GetRequiredService<NodePilot.Api.ExecutionDispatch.ExecutionDispatchQueue>());
builder.Services.AddScoped<NodePilot.Api.ExecutionDispatch.ExecutionDispatchService>();
builder.Services.AddScoped<NodePilot.Core.ExecutionDispatch.IWorkflowExecutionDispatcher>(
    sp => sp.GetRequiredService<NodePilot.Api.ExecutionDispatch.ExecutionDispatchService>());

// Audit: captures who/when/what for every mutation (workflow/machine/credential/auth/user).
// Requires IHttpContextAccessor so the writer can pull the authenticated principal + remote IP.
builder.Services.AddHttpContextAccessor();
// Stager builds the entry with redaction + 4 KiB cap; HTTP-flow AuditWriter wraps it with
// HttpContext-derived actor resolution and SaveChangesAsync persistence. Non-HTTP callers
// (CredentialStore, TriggerOrchestrator, DbAdminController) consume the stager directly so
// every audit row goes through the same redaction + cap pipeline.
builder.Services.AddSingleton<NodePilot.Core.Audit.IAuditStager, NodePilot.Core.Audit.AuditStager>();
builder.Services.AddScoped<NodePilot.Core.Audit.IAuditWriter, NodePilot.Api.Audit.AuditWriter>();

// System-configuration backup (ADR 0001). Parts + orchestrator are scoped — they pull the
// request DbContext and the scoped credential/global stores. Each part is registered against
// the shared IBackupPart so BackupService receives the full set via IEnumerable<IBackupPart>.
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.FolderBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.UserBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.CredentialBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.MachineBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.GlobalVariableFolderBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.GlobalVariableBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.CustomActivityBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.WorkflowBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.AlertingBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.IBackupPart, NodePilot.Api.Services.Backup.Parts.SettingsBackupPart>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.BackupService>();
builder.Services.AddScoped<NodePilot.Api.Services.Backup.BackupRestoreService>();

// Admin Settings test-probe — SMTP/LLM/etc. connectivity diagnostics. Scoped because
// each probe call may need request-scoped state in future (e.g. http context for audit).
builder.Services.AddScoped<NodePilot.Api.Services.SettingsTestProbe>();
builder.Services.AddScoped<NodePilot.Api.Configuration.ISettingsSectionAdapterRegistry>(sp =>
    NodePilot.Api.Configuration.SettingsSectionAdapters.CreateDefault(
        (IConfigurationRoot)sp.GetRequiredService<IConfiguration>(),
        sp.GetRequiredService<ISecretProtector>(),
        sp.GetRequiredService<IOptionsMonitor<NodePilot.Engine.Options.SmtpOptions>>(),
        sp.GetRequiredService<IOptionsMonitor<NodePilot.Ai.LlmOptions>>(),
        sp.GetRequiredService<IOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>>(),
        sp.GetRequiredService<IOptionsMonitor<NodePilot.Api.Security.Ldap.LdapOptions>>(),
        sp.GetRequiredService<IOptionsMonitor<NodePilot.Api.Security.Ldap.WindowsAuthOptions>>(),
        sp.GetRequiredService<IOptionsMonitor<NodePilot.Telemetry.NodePilotTelemetryOptions>>(),
        sp.GetRequiredService<IOptionsMonitor<NodePilot.Ai.AiKnowledgeOptions>>()));

// DB-Admin viewer: schema metadata is stable for the lifetime of the process (migrations only
// run at startup), so a singleton is correct and avoids re-reflecting on every request.
builder.Services.AddSingleton<NodePilot.Api.Services.DbAdmin.DbAdminMetadataService>();
builder.Services.Configure<NodePilot.Api.Services.DbAdmin.DbAdminOptions>(
    builder.Configuration.GetSection(NodePilot.Api.Services.DbAdmin.DbAdminOptions.SectionName));
// Query executor is scoped because it pulls the request's DbContext and uses its connection.
builder.Services.AddScoped<NodePilot.Api.Services.DbAdmin.DbAdminQueryExecutor>();

// Remote-execution provider (WinRM by default; "noop" for load tests against a real host).
builder.Services.AddNodePilotRemoteExecution(builder.Configuration);
builder.Services.AddScoped<IWorkflowEngine, WorkflowEngine>();
builder.Services.AddScoped<IStepTester, StepTester>();
builder.Services.AddScoped<IStepTestContextProvider, StepTestContextProvider>();
builder.Services.AddSingleton<NodePilot.Api.Services.IWorkflowContractDeriver, NodePilot.Api.Services.WorkflowContractDeriver>();
// Host identity (machine name / FQDN / domain) for the SPA header. Resolved once from the
// local OS network config and cached — see HostIdentityProvider. Surfaced via /api/system/host-info.
builder.Services.AddSingleton<NodePilot.Core.Interfaces.IHostIdentityProvider, NodePilot.Api.Services.HostIdentityProvider>();
// Perf finding 1.4: ActivityRegistry is a singleton that holds an activityType → Type map. The map is
// built once via a bootstrap scope; per-step lookups resolve fresh executor instances from
// the per-step scope passed into GetExecutor(string, IServiceProvider). No more per-step
// dictionary rebuild on the hot path.
builder.Services.AddSingleton<ActivityRegistry>(sp => new ActivityRegistry(sp));
builder.Services.AddSingleton<NodePilot.Engine.PowerShell.PowerShellEngineFactory>();
// OutputRedactor is stateless once built from config — LogActivity and any future consumer
// pull it from DI. Singleton so the compiled regex list is built once per process. Also
// registered as IAuditDetailsRedactor so the audit stager (Core, no Engine reference) can
// apply the same redaction without a project-graph cycle.
builder.Services.AddSingleton<NodePilot.Engine.Security.OutputRedactor>();
builder.Services.AddSingleton<NodePilot.Core.Audit.IAuditDetailsRedactor>(
    sp => sp.GetRequiredService<NodePilot.Engine.Security.OutputRedactor>());
builder.Services.AddSingleton<NodePilot.Api.Diagnostics.ISupportLogFileResolver, NodePilot.Api.Diagnostics.SupportLogFileResolver>();
// Support-event DB projection: the channel is a singleton (shared between the Serilog sink and
// the background flush service); the flush service is a HostedService that runs on every node,
// not leader-only — see the XML doc comment on SupportEventFlushService for the reasoning.
builder.Services.AddSingleton<NodePilot.Api.Diagnostics.SupportEventChannel>();
builder.Services.AddHostedService<NodePilot.Api.Diagnostics.SupportEventFlushService>();
builder.Services.AddSingleton<NodePilot.Api.Hubs.SignalRExecutionNotifier>();
builder.Services.AddSingleton<IExecutionNotifier>(sp => sp.GetRequiredService<NodePilot.Api.Hubs.SignalRExecutionNotifier>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<NodePilot.Api.Hubs.SignalRExecutionNotifier>());

// Activity + trigger executors are auto-discovered by scanning NodePilot.Engine for every
// concrete IActivityExecutor. New activity = add the file, no DI wiring needed. See
// NodePilot.Engine.ServiceCollectionExtensions.AddNodePilotActivities.
builder.Services.AddNodePilotActivities();
builder.Services.AddNodePilotEngineOptions(builder.Configuration);

// Retention sweeper settings — bound once, consumed by three BackgroundServices via IOptions.
builder.Services.Configure<NodePilot.Scheduler.Options.RetentionOptions>(
    builder.Configuration.GetSection(NodePilot.Scheduler.Options.RetentionOptions.SectionName));

// Named client for RestApiActivity / outgoing HTTP from workflows. AllowAutoRedirect is
// disabled so NodePilot.Engine.Activities.RestApiActivity can re-run the SSRF guard on every
// hop and strip Authorization-style headers when the origin changes (security-audit findings H6 and L2).
// Proxy/noProxy is driven from RestApi:Proxy:* via RestApiHttpClientProvider — Enabled=false
// (default) explicitly sets UseProxy=false so the named client never silently picks up the
// Windows system proxy. Pool + DNS TTL defaults are fine; we revalidate every hop explicitly
// so rebinding windows are closed by the guard rather than by connection reuse.
builder.Services.AddHttpClient("NodePilot", c =>
    {
        // Disable HttpClient's own 100s default. RestApiActivity enforces the per-step
        // timeout via a linked CTS — HttpClient's internal timer would otherwise cap
        // intentionally-unbounded calls (timeoutSeconds = null) at 100s.
        c.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(sp =>
        NodePilot.Engine.Security.RestApiHttpClientProvider.BuildDefaultHandler(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NodePilot.Engine.Options.RestApiProxyOptions>>().Value,
            sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<NodePilot.Engine.Security.RestApiHttpClientProvider>();

// AI assistant: dedicated named HttpClient + options + prompt catalog. Deliberately registered
// AFTER the "NodePilot" client and with its own handler — otherwise the RestApi SSRF guard would
// block local endpoints (e.g. Ollama at 127.0.0.1:11434). Master switch is Llm:Enabled (default false).
builder.Services.AddNodePilotAi(builder.Configuration);

builder.Services.AddSignalR();
builder.Services.AddControllers(opt =>
{
    opt.Filters.Add<NodePilot.Api.Filters.ApiProblemDetailsResultFilter>();
}).AddJsonOptions(opt =>
{
    // String-enums on BOTH directions so DTOs containing FolderPrincipalType / SharedFolderRole
    // / UserRole / AuthProvider round-trip as readable strings ("User", "FolderOperator", ...).
    // Without this, AddControllers() defaults serialize enums as numbers — UI and CLI both
    // type these fields as strings, so the read path silently mis-matched (UI's
    // `principalType === 'User'` returns false for numeric 0, CLI fails deserialisation
    // when the JSON value is a number into a string property). System.Text.Json in .NET 10
    // already accepts strings on input, so this change is purely about the response shape.
    opt.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
});
builder.Services.AddNodePilotOpenApi();

// Long-running workers: trigger orchestration, retention sweepers, revocation cleanup,
// SignalR-revocation sweeper. See Hosting/BackgroundServicesSetup.cs for the full list.
builder.Services.AddNodePilotBackgroundServices();

// Active/Passive HA cluster wiring (Cluster:Enabled). Registers a no-op state provider in
// single-node mode and the real lease-managing BackgroundService in cluster mode. Every
// component that gates on "am I the leader" reads IClusterStateProvider.
//
// Boot-validator pipeline replaces the old standalone ClusterConfigValidator.Validate call.
// All boot-validators (Cluster, SecretsConsistency, LoggingFormat, ...) are auto-discovered
// via reflection and aggregated so the operator sees every misconfiguration in one shot
// instead of fixing-restarting-fixing one-at-a-time. The Settings API reuses the same
// pipeline against the simulated post-save config so a bad Save is rejected with 400.
BootValidatorRunner.RunAll(
    builder.Configuration,
    warningLogger: msg => Log.Warning("{Message}", msg));
builder.Services.AddNodePilotCluster(builder.Configuration);

// Loud-on-boot warnings for permissive defaults that need attention in production.
SecurityHardeningWarnings.LogRetentionDisabledWarnings(builder.Configuration);
SecurityHardeningWarnings.LogSecurityHardeningWarnings(builder.Configuration, builder.Environment);

builder.Services.AddCors(options =>
{
    options.AddPolicy("DevCors", policy =>
        policy.WithOrigins("http://localhost:5173")
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials());
});

var app = builder.Build();

// Clear the runtime-overrides "restart required" marker once the host is fully up. We
// register on ApplicationStarted (NOT directly after Build) so the marker only goes away
// when the process genuinely runs — if Build succeeds but a startup hook (migrations,
// boot validators, etc.) throws between Build and Run, the operator must still see the
// banner indicating that pending settings are not active.
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        app.Services.GetRequiredService<RuntimeOverridesWriter>().ClearRestartMarker();
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogWarning(ex, "Failed to clear runtime-overrides restart marker on startup. UI will continue to show the banner until the next save or restart.");
    }

    // Support Log: one-time startup banner. Gives the support team the version, DB provider,
    // and environment at a glance from the Support Log itself — no need for the operator to
    // go dig through config files.
    try
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        var env = app.Services.GetRequiredService<IWebHostEnvironment>();
        var dbProvider = app.Configuration["Database:Provider"] ?? "unknown";
        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["SupportLog"] = true,
            ["support.event_type"] = "SYSTEM_BOOT",
            ["app_version"] = version,
            ["environment"] = env.EnvironmentName,
            ["db_provider"] = dbProvider,
            ["support.message"] = $"started version={version} env={env.EnvironmentName} db={dbProvider}",
        }))
        {
            logger.LogInformation(
                "NodePilot.Api started — version={Version} env={Environment} db={DbProvider}",
                version, env.EnvironmentName, dbProvider);
        }
    }
    catch
    {
        // Best-effort — a failure to emit the support banner must not abort boot.
    }
});

// Database initialization — delegates to MigrationBootstrapper which calls
// db.Database.Migrate() against the active provider (SQL Server or PostgreSQL).
// Single provider-agnostic migration set; no legacy SchemaPatcher fallback.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
    var bootstrapDbLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    MigrationBootstrapper.Bootstrap(db, bootstrapDbLogger);

    // Surface the active secret protector so operators see "DPAPI" vs "AES-GCM" in the
    // boot log without grepping config. Single line, INFO level, only at startup.
    scope.ServiceProvider.GetRequiredService<NodePilot.Data.Security.SecretProtectorRegistry.IStartupLogger>().Log();

    // Sweep orphaned Running executions left over from a previous process instance
    // (crash / kill / upgrade). Without this the UI would show ghost "Running" rows
    // forever because there is no in-memory CancellationTokenSource for them anymore.
    //
    // CLUSTER MODE: skipped here. A starting follower must NOT clobber the active leader's
    // running rows. Recovery instead runs from ClusterLeaderService.OnLeadershipAcquired
    // and predicates on OwnerNodeId != ourNodeId, see StartupRecovery overload.
    var recoveryLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    if (!builder.Configuration.GetValue<bool>("Cluster:Enabled"))
    {
        await NodePilot.Engine.Execution.StartupRecovery.RecoverOrphanedExecutionsAsync(db, recoveryLogger);
    }
    else
    {
        recoveryLogger.LogInformation(
            "Cluster:Enabled=true — skipping boot-time orphan recovery. Will run on first leadership acquisition.");
    }

    // Admin bootstrap: if there are no users yet, write a one-shot token file that the
    // first login must present. Without this the first HTTP caller of /api/auth/login
    // would auto-become Admin — trivial takeover on a freshly deployed instance.
    var bootstrapLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var usersExist = db.Users.Any();
    await NodePilot.Api.Security.EnterpriseRecoveryInvariant.EnsureAsync(
        db, builder.Configuration);
    NodePilot.Api.Security.AdminBootstrap.EnsureBootstrapTokenIfNeeded(
        app.Environment, usersExist, bootstrapLogger, builder.Configuration);
}

// Production-only pipeline hardening: global exception handler + HSTS + CSP + standard
// security response headers. Must be registered before other middleware so it wraps them.
app.UseHostFiltering();
app.UseNodePilotSecurityHeaders();

// Swagger (OpenAPI JSON at /openapi/v1.json, interactive UI at /swagger) — gated by
// Swagger:DisableInNonDevelopment in non-dev environments.
var openApiLogger = app.Services.GetRequiredService<ILogger<Program>>();
app.UseNodePilotOpenApi(openApiLogger);

if (app.Environment.IsDevelopment())
{
    app.UseCors("DevCors");
}

// Must run before rate-limiter + auth so Connection.RemoteIpAddress reflects the true
// client IP behind a reverse proxy. Safe when no proxy is present — with no matching
// X-Forwarded-* headers it's a no-op.
app.UseForwardedHeaders();

// HTTP → HTTPS redirect only active when Kestrel was configured with a cert-store binding
// and RedirectHttpToHttps is true. No-op in dev/test.
app.UseNodePilotHttpsRedirection();

app.UseStaticFiles();
app.UseRateLimiter();
// The OIDC remote handler consumes /signin-oidc inside UseAuthentication and therefore
// never reaches the normal post-auth leader firewall. Fence it first so a directly-hit
// follower cannot exchange a code or persist an external authentication ticket.
if (builder.Configuration.GetValue<bool>("Cluster:Enabled"))
{
    app.UseMiddleware<NodePilot.Api.Security.OidcCallbackLeaderFenceMiddleware>();
}
app.UseAuthentication();
// HA defense-in-depth: refuse mutating API + hub traffic on follower nodes with 503,
// even if the LB mis-routes. Gated behind Cluster:Enabled so single-node deployments
// pay zero cost. Placed AFTER UseAuthentication so 401 still wins over 503 on a totally
// unauthenticated request — the operator-facing message is more useful that way.
if (builder.Configuration.GetValue<bool>("Cluster:Enabled"))
{
    app.UseMiddleware<NodePilot.Api.Security.LeaderRequiredMiddleware>();
}
// Revocation + IsActive check — runs after JwtBearer has parsed the token and populated
// ctx.User, but before authorization so disabled users cannot satisfy [Authorize(Roles=...)].
app.UseMiddleware<NodePilot.Api.Security.TokenValidityMiddleware>();
// Audit H-5 companion: CSRF double-submit for cookie-authenticated mutating requests.
// Placed after authentication so Bearer-authenticated requests (which always set the
// Authorization header) are skipped cheaply via the header-absence check. Safe methods
// and the login endpoint are exempt inside the middleware.
app.UseMiddleware<NodePilot.Api.Security.CsrfMiddleware>();
app.UseAuthorization();

// Prometheus scrape endpoint (enabled via OpenTelemetry:Exporters:PrometheusScrape = true).
// Gated behind [Authorize] so ops metrics (workflow names, execution counts, error rates)
// are not broadcast to anonymous clients. For pull-based scrapers that can't present a
// JWT, set OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous=true to opt back out.
if (builder.Configuration.GetValue<bool>("OpenTelemetry:Enabled")
    && builder.Configuration.GetValue<bool>("OpenTelemetry:Exporters:PrometheusScrape"))
{
    var scrape = app.MapPrometheusScrapingEndpoint();
    if (!builder.Configuration.GetValue<bool>("OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous"))
        scrape.RequireAuthorization();
}

app.MapControllers();
app.MapHub<NodePilot.Api.Hubs.ExecutionHub>("/hubs/execution", options =>
{
    // SignalR authenticates once at the handshake. Without this option an otherwise
    // valid WebSocket can outlive JWT expiry indefinitely; close it at expiry so the
    // reconnect performs the full JWT + SecurityStamp validation again.
    options.CloseOnAuthenticationExpiration = true;
});

// Liveness — cheap process-alive ping; returns 200 as long as the host can respond at all.
// No DB probe here: a transient DB blip must not cause Kubernetes/Docker to restart the pod.
app.MapHealthChecks("/healthz/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false, // exclude all registered checks; only the endpoint responds
}).AllowAnonymous();

// Readiness — includes the "ready"-tagged checks (DB reachability). Flips to 503 when the
// database is unreachable so the orchestrator routes traffic away while the process keeps
// running.
app.MapHealthChecks("/healthz/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

// Directory dependency status is separate from service readiness. A DC outage must fail
// external authorization closed, but it must not remove every HA node from traffic and
// thereby block the local break-glass recovery path.
app.MapHealthChecks("/healthz/directory", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("directory"),
}).AllowAnonymous();

// Leader-health — fail-closed probe used by the load-balancer to identify the active node
// in active/passive HA. See ClusterSetup.ComputeLeaderHealth for the exact contract.
var clusterTtlSeconds = builder.Configuration.GetValue("Cluster:LeaseTtlSeconds", 30);
app.MapGet("/healthz/leader", (NodePilot.Core.Interfaces.IClusterStateProvider cluster) =>
    ClusterSetup.ComputeLeaderHealth(cluster, TimeSpan.FromSeconds(clusterTtlSeconds), DateTime.UtcNow))
    .AllowAnonymous();

// SPA fallback
app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program;

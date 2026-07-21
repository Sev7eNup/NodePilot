using Quartz;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Long-running background workers that the API hosts: trigger orchestration, retention
/// sweepers, and revocation cleanup. Deliberately registered as a single batch so the
/// Program.cs reads as a list of "what runs in the background", and so the always-on vs.
/// opt-in distinction lives in one place.
/// </summary>
public static class BackgroundServicesSetup
{
    public static IServiceCollection AddNodePilotBackgroundServices(this IServiceCollection services)
    {
        // Trigger infrastructure: Quartz (for scheduleTrigger) + orchestrator + per-type sources
        services.AddQuartz();
        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
        services.AddTransient<NodePilot.Scheduler.Sources.ScheduleTriggerSource>();
        services.AddHostedService<NodePilot.Scheduler.TriggerOrchestrator>();
        services.AddHostedService<NodePilot.Api.ExecutionDispatch.ExecutionDispatchWorker>();

        // Hot-reload companion to the cold-start ThreadPool.SetMinThreads prewarm in Program.cs:
        // re-applies Threading:MinWorkerThreads / MinIoCompletionThreads from the live config on
        // start and on every config reload (Admin-Settings-UI save → appsettings.runtime.json).
        services.AddHostedService<ThreadPoolTuningService>();

        // Maintenance-window snapshot refresher. Always on (no opt-out), NOT leader-gated — every
        // node must evaluate windows for the API/webhook traffic it serves.
        services.AddHostedService<NodePilot.Scheduler.MaintenanceWindowSnapshotService>();

        // Execution-history retention: opt-in via Retention:Executions:Enabled=true. No-op when off.
        services.AddHostedService<NodePilot.Scheduler.ExecutionRetentionService>();

        // Audit-log retention: opt-in via Retention:AuditLog:Enabled=true. Default 365-day
        // window — audit typically needs longer retention than execution history for
        // compliance.
        services.AddHostedService<NodePilot.Scheduler.AuditLogRetentionService>();

        // WorkflowVersions history trimmer — count-based per workflow (keep latest N).
        // Protects against unbounded history-table growth for workflows under CI/CD-style
        // frequent edits.
        services.AddHostedService<NodePilot.Scheduler.WorkflowVersionsRetentionService>();

        // Idempotency-key cache sweeper — always on, prunes rows past their 24h TTL.
        services.AddHostedService<NodePilot.Scheduler.IdempotencyKeyCleanupService>();

        // Alerting: leader-gated notification dispatcher + its delivery sinks (Email + generic
        // webhook). Opt-in by data — idle until a NotificationRule exists. The dispatch watermark
        // is seeded to "now" on first run so existing execution history is never back-alerted.
        services.AddSingleton<NodePilot.Core.Interfaces.INotificationSink, NodePilot.Engine.Notifications.SmtpNotificationSink>();
        services.AddSingleton<NodePilot.Core.Interfaces.INotificationSink, NodePilot.Engine.Notifications.WebhookNotificationSink>();
        // Infra/workflow-state signals (backlog, machine health, credential expiry, schedule health, …) are
        // modular ISystemAlertSources evaluated per system policy (ADR 0008) — registered just below. The
        // legacy IGaugeSignalProvider path was removed once the sources fully covered it (one pipeline).
        services.AddHostedService<NodePilot.Scheduler.NotificationDispatcher>();

        // System-alert sources (ADR 0008): the modular, self-describing forward path for alerting. The
        // read-only catalog (GET /api/alerting/system/catalog) is built from these; the policy evaluator
        // and REST/UI surfaces land in later phases. Stateless + side-effect free → singletons.
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.BacklogSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.PendingSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.CancelRateSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.MachineUnreachableSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.ServiceStaleSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.CredentialExpirySource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.WorkflowNoRecentSuccessSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.ScheduleMissedSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.ExecutionResultSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.StuckExecutionSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.WorkflowHealthSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertSource, NodePilot.Scheduler.SystemAlerts.Sources.AlertDeliveryFailureSource>();
        services.AddSingleton<NodePilot.Scheduler.SystemAlerts.ISystemAlertCatalog, NodePilot.Scheduler.SystemAlerts.SystemAlertCatalog>();

        // Trims the alerting delivery ledger (terminal NotificationDeliveryAttempt + stale suppression
        // rows) so it doesn't grow unbounded. Opt-out via Retention:Notifications:Enabled=false. Leader-only.
        services.AddHostedService<NodePilot.Scheduler.NotificationRetentionService>();

        // Support-Event retention: trims SupportEvents older than Retention:SupportEvents:MaxAgeDays
        // (default 90, matcht Plain-Text-File-Retention). Leader-only.
        services.AddHostedService<NodePilot.Scheduler.SupportEventRetentionService>();

        // Workflow-KPI aggregate refresher — populates WorkflowStats so dashboards and list
        // endpoints don't scan WorkflowExecutions per request. Always-on (cheap enough).
        services.AddHostedService<NodePilot.Scheduler.WorkflowStatsRefresher>();

        // Daily sweep of the RevokedTokens table. Every authenticated request queries this
        // table in TokenValidityMiddleware — without cleanup the row count grows unbounded
        // over months and drags p99 auth-check latency with it (audit M12). Safe to delete
        // any row past ExpiresAt because JwtBearer's lifetime-validator already rejects
        // tokens past their exp claim.
        services.AddHostedService<NodePilot.Api.Security.RevokedTokensCleanupService>();
        services.AddHostedService<NodePilot.Api.Security.AuthSessionCleanupService>();
        services.AddHostedService<NodePilot.Api.Security.Ldap.DirectorySynchronizationService>();
        services.AddHostedService<NodePilot.Api.Security.ExternalAuthorizationStalenessService>();

        // Sweeper for SignalR connections whose authenticating token has been revoked or
        // whose user has been deactivated (audit M2). SignalR authenticates once at
        // handshake time and does not re-check per frame — without this a logout /
        // IsActive=false or an expired authorization snapshot leaves a listening
        // connection live until it is proactively disconnected.
        services.AddHostedService<NodePilot.Api.Hubs.HubRevocationSweeper>();

        return services;
    }
}

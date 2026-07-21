using System.Diagnostics.Metrics;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Telemetry;

/// <summary>
/// API-layer metrics (SignalR transport, auth outcomes, external-trigger usage).
/// Subscribed by the OTel pipeline via <c>AddMeter(TelemetryConstants.Meters.Api)</c>.
/// </summary>
public static class ApiMetrics
{
    public static readonly Meter Meter = new(TelemetryConstants.Meters.Api, "1.0.0");

    public static readonly UpDownCounter<long> SignalRConnectionsActive = Meter.CreateUpDownCounter<long>(
        "nodepilot.signalr.connections.active", unit: "1", description: "Number of currently connected SignalR clients on the execution hub.");

    public static readonly Counter<long> SignalRMessagesSent = Meter.CreateCounter<long>(
        "nodepilot.signalr.messages.sent", unit: "1", description: "SignalR broadcasts sent from the server, tagged by event_type.");

    // Webhook ingress — counts every hit, tagged by accept/reject and reason. Lets ops
    // distinguish "nobody is calling us" from "everyone hits the wrong path/secret".
    public static readonly Counter<long> WebhookRequests = Meter.CreateCounter<long>(
        "nodepilot.webhook.requests", unit: "1",
        description: "Webhook hits, tagged by result (accepted/rejected) and reason.");

    // Auth — login attempts, token revocations, account lockouts. Drives the security
    // dashboard panels that today have to be assembled from grep'd log lines.
    public static readonly Counter<long> AuthLoginAttempts = Meter.CreateCounter<long>(
        "nodepilot.auth.login.attempts", unit: "1",
        description: "Login attempts, tagged by result (success/failure) and reason (bad_password/locked/disabled/unknown_user).");

    public static readonly Counter<long> AuthTokenRevocations = Meter.CreateCounter<long>(
        "nodepilot.auth.token.revocations", unit: "1",
        description: "JWT revocations recorded, tagged by reason.");

    public static readonly Counter<long> AuthLockouts = Meter.CreateCounter<long>(
        "nodepilot.auth.lockouts", unit: "1",
        description: "Account lockouts triggered by repeated bad-password attempts.");

    // Idempotency — replay rate is a load-shed metric: high replay = healthy retrying caller.
    public static readonly Counter<long> IdempotencyKeyHits = Meter.CreateCounter<long>(
        "nodepilot.idempotency.replays", unit: "1",
        description: "External-trigger requests with an Idempotency-Key, tagged by result (cached/fresh).");

    // External-trigger auth failures (X-Api-Key) — separate from JWT auth so the panels
    // for "API consumers" stay distinct from "interactive users".
    public static readonly Counter<long> ExternalTriggerAuthFailures = Meter.CreateCounter<long>(
        "nodepilot.external_trigger.auth_failures", unit: "1",
        description: "External /api/trigger requests rejected on X-Api-Key validation.");

    // AI/LLM — call count, latency, errors, token usage. Tagged by kind (script/workflow)
    // so the dashboard can distinguish the two features without separate metric families.
    public static readonly Counter<long> LlmCalls = Meter.CreateCounter<long>(
        "nodepilot.llm.calls", unit: "1",
        description: "LLM completion calls, tagged by kind (script/workflow) and result.");

    public static readonly Histogram<double> LlmCallDuration = Meter.CreateHistogram<double>(
        "nodepilot.llm.call.duration", unit: "ms",
        description: "LLM completion call latency, tagged by kind and model.");

    public static readonly Counter<long> LlmErrors = Meter.CreateCounter<long>(
        "nodepilot.llm.errors", unit: "1",
        description: "LLM call failures, tagged by kind and error_kind.");

    public static readonly Counter<long> LlmTokens = Meter.CreateCounter<long>(
        "nodepilot.llm.tokens", unit: "1",
        description: "LLM token usage, tagged by kind, model, and token_type (prompt/completion).");

    // Rate limiting — rejections per policy. Without this, 429s are invisible in OTel dashboards.
    public static readonly Counter<long> RateLimitRejections = Meter.CreateCounter<long>(
        "nodepilot.rate_limit.rejections", unit: "1",
        description: "Requests rejected by rate-limiting policies, tagged by policy name.");

    // Workflow lifecycle — create/update/delete/enable/disable/duplicate/lock/unlock/publish/force-unlock/rollback.
    public static readonly Counter<long> WorkflowOperations = Meter.CreateCounter<long>(
        "nodepilot.workflow.operations", unit: "1",
        description: "Workflow lifecycle operations, tagged by operation and result.");

    // Import/export
    public static readonly Counter<long> ImportExportOperations = Meter.CreateCounter<long>(
        "nodepilot.import_export.operations", unit: "1",
        description: "Workflow import/export operations, tagged by operation and result.");

    public static readonly Histogram<double> ImportExportDuration = Meter.CreateHistogram<double>(
        "nodepilot.import_export.duration", unit: "ms",
        description: "Duration of import/export operations, tagged by operation.");

    // Machine test-connection
    public static readonly Counter<long> MachineTestConnections = Meter.CreateCounter<long>(
        "nodepilot.machine.test_connections", unit: "1",
        description: "Machine connectivity tests, tagged by result (success/failure).");

    public static readonly Histogram<double> MachineTestDuration = Meter.CreateHistogram<double>(
        "nodepilot.machine.test_connection.duration", unit: "ms",
        description: "Duration of machine connectivity tests.");

    // Credential CRUD (API-layer, distinct from DPAPI-level DataMetrics)
    public static readonly Counter<long> CredentialOperations = Meter.CreateCounter<long>(
        "nodepilot.credential.crud", unit: "1",
        description: "Credential CRUD operations, tagged by operation and result.");

    // Global variable CRUD
    public static readonly Counter<long> GlobalVariableOperations = Meter.CreateCounter<long>(
        "nodepilot.global_variable.crud", unit: "1",
        description: "Global variable CRUD operations, tagged by operation and result.");

    // Maintenance window CRUD
    public static readonly Counter<long> MaintenanceWindowOperations = Meter.CreateCounter<long>(
        "nodepilot.maintenance_window.crud", unit: "1",
        description: "Maintenance window CRUD operations, tagged by operation and result.");

    // Runs blocked by an active maintenance window on an API admission path (manual/webhook/
    // external/dispatch), tagged by source and scope. The scheduler-side trigger blocks are
    // counted separately by SchedulerMetrics.MaintenanceWindowBlocks.
    public static readonly Counter<long> MaintenanceWindowBlocks = Meter.CreateCounter<long>(
        "nodepilot.maintenance_window.api_blocks", unit: "1",
        description: "Runs blocked by a maintenance window on an API path, tagged by source.");

    // Admin force-runs through an active blackout. Loud, separate signal so SOC review can find
    // every bypass.
    public static readonly Counter<long> MaintenanceWindowOverrides = Meter.CreateCounter<long>(
        "nodepilot.maintenance_window.overrides", unit: "1",
        description: "Admin force-runs that bypassed an active maintenance window.");

    // Execution dispatch worker pool
    public static readonly Counter<long> DispatchItemsProcessed = Meter.CreateCounter<long>(
        "nodepilot.dispatch.items_processed", unit: "1",
        description: "Items processed by the execution dispatch worker pool, tagged by result.");

    // Revoked-tokens cleanup
    public static readonly Counter<long> RevokedTokensDeleted = Meter.CreateCounter<long>(
        "nodepilot.security.revoked_tokens.deleted", unit: "1",
        description: "Rows deleted by the revoked-tokens cleanup sweep.");

    public static readonly Histogram<double> RevokedTokensSweepDuration = Meter.CreateHistogram<double>(
        "nodepilot.security.revoked_tokens.sweep.duration", unit: "ms",
        description: "Duration of a revoked-tokens cleanup sweep.");
}

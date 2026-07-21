using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Api.Security;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Controllers;

/// <summary>
/// External trigger surface: <c>POST /api/trigger/{workflowNameOrId}</c>. Anonymous transport,
/// gated by an <c>X-Api-Key</c> header that is matched against <c>ExternalTrigger:ApiKey</c>
/// in constant time. Idempotency-Key handling lives here too — internal callers (UI, scheduler,
/// CLI) hit <see cref="ExecutionsController.Execute"/> instead, which is owner-tagged via JWT.
/// </summary>
[ApiController]
public class ExternalTriggerController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly ExecutionDispatchService _executionDispatch;
    private readonly IAuditWriter _audit;
    private readonly IMaintenanceWindowEvaluator _maintenance;
    private readonly OutputRedactor _redactor;

    public ExternalTriggerController(
        NodePilotDbContext db,
        ExecutionDispatchService executionDispatch,
        IAuditWriter audit,
        IMaintenanceWindowEvaluator maintenance,
        OutputRedactor redactor)
    {
        _db = db;
        _executionDispatch = executionDispatch;
        _audit = audit;
        _maintenance = maintenance;
        _redactor = redactor;
    }

    // External-trigger API key must be at least 32 bytes (256 bits) to stop brute-force
    // attempts over the network. Shorter keys are rejected at request time so a fat-fingered
    // value in appsettings.json does not silently become a weak secret.
    internal const int MinExternalApiKeyBytes = 32;

    // L-7 (security audit 2026-05-15): the external-trigger surface is API-key-authenticated and
    // therefore carries no role. ExecutionsController redacts ErrorMessage / ReturnData /
    // InputParametersJson for every caller below Admin/Operator; the API-key holder must get the
    // same treatment, otherwise step-stdout tokens or webhook-body secrets leak through the
    // trigger response. Instance (not static) so it can reach the injected OutputRedactor.
    private ExecutionResponse ToResponse(WorkflowExecution execution) => new(
        execution.Id,
        execution.WorkflowId,
        execution.Status.ToString(),
        execution.StartedAt,
        execution.CompletedAt,
        execution.TriggeredBy,
        _redactor.Redact(execution.ErrorMessage),
        execution.TraceId,
        execution.SpanId,
        _redactor.Redact(execution.ReturnData),
        _redactor.Redact(execution.InputParametersJson));

    private static async Task<WorkflowExecution?> FindIdempotencyReplayAsync(
        NodePilotDbContext db,
        string idempotencyKey,
        Guid workflowId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var existing = await db.IdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == idempotencyKey && k.WorkflowId == workflowId && k.ExpiresAt > now, ct);
        if (existing is null) return null;

        return await db.WorkflowExecutions.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == existing.ExecutionId, ct);
    }

    private static async Task RemoveIdempotencyKeyAsync(
        NodePilotDbContext db,
        string idempotencyKey,
        Guid workflowId,
        CancellationToken ct)
    {
        var key = await db.IdempotencyKeys
            .FirstOrDefaultAsync(k => k.Key == idempotencyKey && k.WorkflowId == workflowId, ct);
        if (key is null) return;

        db.IdempotencyKeys.Remove(key);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// External trigger endpoint — start a workflow by name or ID with parameters.
    /// Requires an API key via X-Api-Key header; the key is configured in appsettings
    /// under ExternalTrigger:ApiKey (or env NODEPILOT__ExternalTrigger__ApiKey). If
    /// the config is missing or empty, this endpoint is disabled entirely.
    /// Example: POST /api/trigger/Deploy%20App {"parameters": {"version": "2.1.0"}}
    /// </summary>
    [HttpPost("/api/trigger/{workflowNameOrId}")]
    [AllowAnonymous]
    // H-1: without this policy, an attacker who discovers the API key (or a legitimate
    // integration with a bug) can fire workflows at unlimited RPS — every trigger spawns
    // engine/DB work. The "trigger" policy (30/min per IP) is defined in Program.cs.
    [EnableRateLimiting("trigger")]
    public async Task<ActionResult<ExecutionResponse>> ExternalTrigger(
        string workflowNameOrId,
        [FromBody] ExecuteWorkflowRequest? request,
        [FromServices] IConfiguration config,
        [FromServices] ILogger<ExternalTriggerController> logger,
        CancellationToken ct)
    {
        var expectedKey = config["ExternalTrigger:ApiKey"];
        // Don't distinguish "no key configured" from "wrong key" in the response — both
        // return 401. Previously this returned 503, which confirmed to an unauthenticated
        // caller that the endpoint existed but was unconfigured, aiding discovery.
        if (string.IsNullOrWhiteSpace(expectedKey)
            || System.Text.Encoding.UTF8.GetByteCount(expectedKey) < MinExternalApiKeyBytes)
        {
            if (string.IsNullOrWhiteSpace(expectedKey))
                logger.LogDebug("External trigger rejected: ExternalTrigger:ApiKey not configured.");
            else
                logger.LogWarning("External trigger rejected: ExternalTrigger:ApiKey is shorter than {Min} bytes. Rotate the key.", MinExternalApiKeyBytes);

            // Still run FixedTimeEquals on dummy data so the response time does not reveal
            // whether the server is misconfigured versus presenting the wrong key.
            _ = SecretComparer.FixedTimeEquals(Request.Headers["X-Api-Key"].ToString(), new string('x', MinExternalApiKeyBytes));
            NodePilot.Api.Telemetry.ApiMetrics.ExternalTriggerAuthFailures.Add(1);
            return Unauthorized(new { message = "Invalid or missing X-Api-Key header" });
        }

        if (!Request.Headers.TryGetValue("X-Api-Key", out var presented)
            || !SecretComparer.FixedTimeEquals(presented.ToString(), expectedKey))
        {
            NodePilot.Api.Telemetry.ApiMetrics.ExternalTriggerAuthFailures.Add(1);
            return Unauthorized(new { message = "Invalid or missing X-Api-Key header" });
        }

        Workflow? workflow = null;
        if (Guid.TryParse(workflowNameOrId, out var guid))
            workflow = await _db.Workflows.FindAsync([guid], ct);

        if (workflow is null)
        {
            var resolved = await WorkflowNameResolver.ResolveByNameAsync(_db.Workflows, workflowNameOrId, ct);
            // Ambiguous names collapse into the same uniform 404 as missing/disabled (M-29):
            // an API-key holder must not learn how many workflows share a name. The caller's
            // remedy is the GUID; the ambiguity is visible to admins via GetByName's 409.
            workflow = resolved.Workflow;
        }

        // M-29: uniform 404 to prevent workflow-name enumeration via external-trigger API key.
        // Previously this returned 404 for "not found" but 400 for "exists but disabled", which
        // let a holder of a valid API key confirm which names exist even when disabled.
        // Now all non-executable cases (missing / disabled) collapse to the same 404.
        if (workflow is null || !workflow.IsEnabled)
            return NotFound(new { message = $"Workflow '{workflowNameOrId}' not found or not executable" });

        // Maintenance-window gate. MUST run BEFORE the idempotency-key transaction below: if a
        // blocked fire consumed its key, a legitimate retry after the window reopens would replay
        // the cached Cancelled "ghost" for the whole 24h TTL instead of actually running. Uniform
        // 404 (M-29) so an API-key holder can't distinguish "blocked" from "disabled"/"missing".
        var maintenanceVerdict = _maintenance.Evaluate(workflow.Id, workflow.FolderId, DateTime.UtcNow);
        if (maintenanceVerdict.Blocked)
        {
            NodePilot.Api.Telemetry.ApiMetrics.MaintenanceWindowBlocks.Add(1,
                new("source", "api"), new("scope", "external_trigger"));
            await _audit.LogAsync(AuditActions.ExecutionBlockedMaintenanceWindow, "Workflow", workflow.Id,
                AuditDetails.Json(("source", "api"), ("windowId", maintenanceVerdict.WindowId),
                    ("windowName", maintenanceVerdict.WindowName), ("mode", maintenanceVerdict.Mode?.ToString())), ct);
            return NotFound(new { message = $"Workflow '{workflowNameOrId}' not found or not executable" });
        }

        // Idempotency-Key handling: if the caller supplies one, a replay of the same key
        // returns the original execution instead of firing the workflow a second time. Keys
        // are scoped per workflow so two different runbooks can reuse the same caller token.
        // Limit: 200 chars (matches column); empty/whitespace treated as "no key".
        string? idempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var hdr)
            ? hdr.ToString().Trim() : null;
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            if (idempotencyKey.Length > 200)
                return BadRequest(new { message = "Idempotency-Key must be 200 characters or less" });

            var replay = await FindIdempotencyReplayAsync(_db, idempotencyKey, workflow.Id, ct);
            if (replay is not null)
            {
                Response.Headers["Idempotent-Replayed"] = "true";
                NodePilot.Api.Telemetry.ApiMetrics.IdempotencyKeyHits.Add(1,
                    new KeyValuePair<string, object?>("result", "cached"));
                return Ok(ToResponse(replay));
            }

        }

        // C-2: tag the execution with the initiating user so Resume can enforce
        // an owner-check and overrides from a different Operator's token are rejected.
        var startedByUserId = this.GetCurrentUserId() ?? workflow.PublishedByUserId;
        var parameters = request?.Parameters is null
            ? null
            : new Dictionary<string, string>(request.Parameters);
        if (parameters is not null
            && NodePilot.Engine.Activities.WorkflowRecursion.FindReservedKey(parameters.Keys) is { } reservedKey)
        {
            return BadRequest(new { message = $"Input parameter '{reservedKey}' is reserved (keys starting with '__' are used by the engine)." });
        }
        var timeoutSeconds = request?.TimeoutSeconds;
        var dispatchIntent = new WorkflowDispatchIntent(
            workflow.Id,
            "api",
            parameters,
            timeoutSeconds,
            DebugEnabled: false,
            StartedByUserId: startedByUserId,
            RequireWorkflowEnabled: true,
            MissingWorkflowMessage: "Queued external trigger was not dispatched because the workflow no longer exists or is disabled.",
            PreOwnershipFailurePrefix: "Queued external trigger failed before the engine could take ownership",
            EnqueueFailureMessage: "Queued external trigger was not dispatched because the request was cancelled before enqueue completed.");
        WorkflowExecution pending;

        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            try
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var now = DateTime.UtcNow;
                var existingKey = await _db.IdempotencyKeys
                    .FirstOrDefaultAsync(k => k.Key == idempotencyKey && k.WorkflowId == workflow.Id, ct);
                if (existingKey is not null && existingKey.ExpiresAt > now)
                {
                    var replay = await _db.WorkflowExecutions.AsNoTracking()
                        .FirstOrDefaultAsync(e => e.Id == existingKey.ExecutionId, ct);
                    if (replay is not null)
                    {
                        Response.Headers["Idempotent-Replayed"] = "true";
                        NodePilot.Api.Telemetry.ApiMetrics.IdempotencyKeyHits.Add(1,
                            new KeyValuePair<string, object?>("result", "cached"));
                        return Ok(ToResponse(replay));
                    }

                    _db.IdempotencyKeys.Remove(existingKey);
                }
                else if (existingKey is not null)
                {
                    _db.IdempotencyKeys.Remove(existingKey);
                }

                pending = _executionDispatch.AddPendingExecution(dispatchIntent);
                _db.IdempotencyKeys.Add(new IdempotencyKey
                {
                    Id = Guid.NewGuid(),
                    Key = idempotencyKey,
                    WorkflowId = workflow.Id,
                    ExecutionId = pending.Id,
                    FirstSeenAt = now,
                    ExpiresAt = now.AddHours(24),
                });
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                NodePilot.Api.Telemetry.ApiMetrics.IdempotencyKeyHits.Add(1,
                    new KeyValuePair<string, object?>("result", "fresh"));
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();
                var replay = await FindIdempotencyReplayAsync(_db, idempotencyKey, workflow.Id, ct);
                if (replay is not null)
                {
                    Response.Headers["Idempotent-Replayed"] = "true";
                    NodePilot.Api.Telemetry.ApiMetrics.IdempotencyKeyHits.Add(1,
                        new KeyValuePair<string, object?>("result", "cached"));
                    return Ok(ToResponse(replay));
                }

                return Conflict(new { message = "Idempotency-Key is currently being processed; retry with the same key." });
            }

            try
            {
                await _executionDispatch.EnqueueAsync(pending, dispatchIntent, ct);
            }
            catch
            {
                await RemoveIdempotencyKeyAsync(_db, idempotencyKey, workflow.Id, CancellationToken.None);
                throw;
            }
        }
        else
        {
            pending = await _executionDispatch.DispatchAsync(dispatchIntent, ct);
        }

        // Audit-trail for the API-keyed external trigger surface. Idempotency replays return
        // earlier (above) and are NOT logged again — only fresh fires emit an audit event.
        await _audit.LogAsync(AuditActions.ExternalTriggerFired, "Workflow", workflow.Id,
            AuditDetails.Json(
                ("workflowName", workflow.Name),
                ("executionId", pending.Id),
                ("idempotencyKeyUsed", !string.IsNullOrEmpty(idempotencyKey)),
                ("parameterCount", parameters?.Count ?? 0)),
            ct);

        if (HttpContext is not null)
            Response.Headers.Location = $"/api/executions/{pending.Id}";
        return Accepted(ToResponse(pending));
    }
}

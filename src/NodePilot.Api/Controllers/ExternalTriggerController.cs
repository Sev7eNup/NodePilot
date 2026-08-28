using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Controllers;

/// <summary>
/// External trigger surface: <c>POST /api/trigger/{workflowNameOrId}</c>. Anonymous transport,
/// gated by an <c>X-Api-Key</c> header that is matched against a configured, workflow-scoped
/// external-trigger key in constant time. Idempotency-Key handling lives here too — internal callers (UI, scheduler,
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

    // M-32: caps on the anonymous-reachable trigger payload. Every parameter is copied into the
    // execution's variable dictionary and resolved into each step's config, so an unbounded
    // dictionary is engine work, not just bytes. The ceilings are far above any realistic runbook
    // (a webhook-style fan-in tops out at a few dozen fields) and exist to bound the worst case.
    internal const int MaxTriggerBodyBytes = 256 * 1024;
    internal const int MaxTriggerParameterCount = 200;
    internal const int MaxTriggerParameterKeyLength = 200;
    internal const int MaxTriggerParameterValueLength = 8 * 1024;

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

    /// <summary>
    /// Answers an idempotency replay: flag the response, count the cached hit, hand back the
    /// original execution. Three call sites reach it (pre-check, unique-violation race, and the
    /// replay decided inside the transaction) and they must answer identically.
    /// </summary>
    private OkObjectResult IdempotentReplay(WorkflowExecution replay)
    {
        Response.Headers["Idempotent-Replayed"] = "true";
        NodePilot.Api.Telemetry.ApiMetrics.IdempotencyKeyHits.Add(1,
            new KeyValuePair<string, object?>("result", "cached"));
        return Ok(ToResponse(replay));
    }

    private static async Task<WorkflowExecution?> FindIdempotencyReplayAsync(
        NodePilotDbContext db,
        string idempotencyStorageKey,
        Guid workflowId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var existing = await db.IdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == idempotencyStorageKey && k.WorkflowId == workflowId && k.ExpiresAt > now, ct);
        if (existing is null) return null;

        var execution = await db.WorkflowExecutions.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == existing.ExecutionId, ct);
        return execution is not null && !IsNeverStartedRecoveryCancellation(execution)
            ? execution
            : null;
    }

    private static bool IsNeverStartedRecoveryCancellation(WorkflowExecution execution)
        => execution.Status == ExecutionStatus.Cancelled
           && execution.CancelledBy is "reconciler-pending" or "failover-pending";

    /// <summary>
    /// Produces the database key for one caller-supplied Idempotency-Key. The authenticated key
    /// principal is part of the digest domain, so two integrations cannot replay or reserve one
    /// another's token even when they target the same workflow. The raw header and key principal
    /// are never persisted. The fixed-size v1 value fits the existing 200-character column.
    /// </summary>
    internal static string BuildIdempotencyStorageKey(string keyPrincipalId, string clientKey)
    {
        const string domain = "nodepilot:external-trigger:idempotency:v1";
        var domainBytes = Encoding.UTF8.GetBytes(domain);
        var principalBytes = Encoding.UTF8.GetBytes(keyPrincipalId);
        var clientKeyBytes = Encoding.UTF8.GetBytes(clientKey);
        var material = new byte[
            4 + domainBytes.Length + 4 + principalBytes.Length + 4 + clientKeyBytes.Length];
        var offset = 0;
        WriteLengthPrefixed(domainBytes, material, ref offset);
        WriteLengthPrefixed(principalBytes, material, ref offset);
        WriteLengthPrefixed(clientKeyBytes, material, ref offset);
        try
        {
            var digest = SHA256.HashData(material);
            try
            {
                return $"ext:v1:{Convert.ToHexString(digest)}";
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(principalBytes);
            CryptographicOperations.ZeroMemory(clientKeyBytes);
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static void WriteLengthPrefixed(
        ReadOnlySpan<byte> value,
        Span<byte> destination,
        ref int offset)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.Slice(offset, 4), value.Length);
        offset += 4;
        value.CopyTo(destination[offset..]);
        offset += value.Length;
    }

    /// <summary>
    /// The external API is one transport for a workflow's manual entry point; it is not an
    /// instance-wide bypass around the workflow definition. Parse the authoritative definition
    /// instead of trusting the denormalized TriggerTypesJson column so a stale or malformed row
    /// fails closed. Disabled manual-trigger nodes are omitted from TriggerDescriptors.
    /// </summary>
    private static bool AllowsExternalTrigger(Workflow workflow)
        => WorkflowDefinitionDocument.TryParse(workflow.DefinitionJson, out var definition)
           && definition is not null
           && definition.TriggerDescriptors.Any(trigger => trigger.IsManual);

    /// <summary>
    /// External trigger endpoint — start a workflow by name or ID with parameters.
    /// Requires an API key via X-Api-Key header. Preferred configuration uses SHA-256 hashes
    /// under ExternalTrigger:Keys:&lt;integration&gt; with a GUID-only AllowedWorkflowIds scope.
    /// The legacy ExternalTrigger:ApiKey is inert unless its own AllowedWorkflowIds is set.
    /// Example: POST /api/trigger/Deploy%20App {"parameters": {"version": "2.1.0"}}
    /// </summary>
    [HttpPost("/api/trigger/{workflowNameOrId}")]
    [AllowAnonymous]
    // H-1: without this policy, an attacker who discovers the API key (or a legitimate
    // integration with a bug) can fire workflows at unlimited RPS — every trigger spawns
    // engine/DB work. The "trigger" policy (30/min per IP) is defined in RateLimitingSetup.cs.
    [EnableRateLimiting("trigger")]
    // M-32: the body is model-bound in full BEFORE the X-Api-Key comparison below, so an
    // unauthenticated caller decides how much the server allocates per attempt. The rate limiter
    // bounds requests per minute, never bytes per request, and without an endpoint limit this
    // inherits Kestrel's 30 MiB default.
    [RequestSizeLimit(MaxTriggerBodyBytes)]
    public async Task<ActionResult<ExecutionResponse>> ExternalTrigger(
        string workflowNameOrId,
        [FromBody] ExecuteWorkflowRequest? request,
        [FromServices] IConfiguration config,
        [FromServices] ILogger<ExternalTriggerController> logger,
        CancellationToken ct)
    {
        Request.Headers.TryGetValue("X-Api-Key", out var presented);
        var keyScope = ExternalTriggerKeyScopeResolver.Authenticate(
            config, presented.ToString(), MinExternalApiKeyBytes);
        if (keyScope is null)
        {
            logger.LogDebug("External trigger rejected: no configured key scope matched.");
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

        // Uniform 404 prevents workflow-name and external-trigger-scope enumeration by an API-key
        // holder. A workflow must explicitly contain an enabled manualTrigger (the catalogued
        // Manual/API entry point); merely being enabled is no longer sufficient. This keeps the
        // instance-wide key from acting as a start-any-workflow capability.
        if (workflow is null
            || !workflow.IsEnabled
            || !keyScope.AllowedWorkflowIds.Contains(workflow.Id)
            || !AllowsExternalTrigger(workflow))
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

        // Idempotency-Key handling: if the caller supplies one, a replay by the same
        // authenticated key principal returns the original execution. The DB sees only a
        // versioned digest scoped by principal + workflow, never the raw header; another
        // integration can therefore reuse the same client token without replay/preemption.
        // Limit: 200 client characters; empty/whitespace is treated as "no key".
        string? idempotencyKey = Request.Headers.TryGetValue("Idempotency-Key", out var hdr)
            ? hdr.ToString().Trim() : null;
        string? idempotencyStorageKey = null;
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            if (idempotencyKey.Length > 200)
                return BadRequest(new { message = "Idempotency-Key must be 200 characters or less" });

            idempotencyStorageKey = BuildIdempotencyStorageKey(keyScope.PrincipalId, idempotencyKey);
            var replay = await FindIdempotencyReplayAsync(_db, idempotencyStorageKey, workflow.Id, ct);
            if (replay is not null)
                return IdempotentReplay(replay);

        }

        // C-2: tag the execution with the initiating user so Resume can enforce
        // an owner-check and overrides from a different Operator's token are rejected.
        var startedByUserId = this.GetCurrentUserId() ?? workflow.PublishedByUserId;
        var parameters = request?.Parameters is null
            ? null
            : new Dictionary<string, string>(request.Parameters);
        // M-32: shape first, then semantics. Bound count and per-entry length before the
        // reserved-key scan so neither this check nor anything downstream walks an unbounded map.
        if (parameters is not null)
        {
            if (parameters.Count > MaxTriggerParameterCount)
                return BadRequest(new { message = $"At most {MaxTriggerParameterCount} input parameters are allowed." });

            foreach (var (key, value) in parameters)
            {
                // Do not echo an oversized key back — the length is the whole complaint.
                if (key.Length > MaxTriggerParameterKeyLength)
                    return BadRequest(new { message = $"Input parameter names must be {MaxTriggerParameterKeyLength} characters or less." });
                if (value is not null && value.Length > MaxTriggerParameterValueLength)
                    return BadRequest(new { message = $"Input parameter '{key}' exceeds {MaxTriggerParameterValueLength} characters." });
            }
        }

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
            PreOwnershipFailurePrefix: "Queued external trigger failed before the engine could take ownership");
        WorkflowExecution pending;

        if (idempotencyStorageKey is not null)
        {
            var scopedIdempotencyKey = idempotencyStorageKey;
            (WorkflowExecution? Replayed, WorkflowExecution? Fresh) outcome;
            try
            {
                // The configured providers both enable EnableRetryOnFailure (see
                // DbContextSetup), and a retrying execution strategy refuses user-initiated
                // transactions unless the whole unit runs inside strategy.ExecuteAsync —
                // otherwise EF throws InvalidOperationException before the first query. That
                // exception is not a DbUpdateException, so the catch below would not have
                // absorbed it and every keyed call returned 500. Tests run on SQLite, which
                // has no retrying strategy, so the suite could never observe it; the guard
                // test in ExternalTriggerTransactionGuardTests covers the shape instead.
                var strategy = _db.Database.CreateExecutionStrategy();
                outcome = await strategy.ExecuteAsync(async () =>
                {
                    // A retried attempt must not inherit the previous attempt's staged
                    // execution + key rows, which would insert two of each on commit.
                    // Only reads happened before this point, so nothing else is lost.
                    _db.ChangeTracker.Clear();

                    await using var tx = await _db.Database.BeginTransactionAsync(ct);
                    var now = DateTime.UtcNow;
                    var existingKey = await _db.IdempotencyKeys
                        .FirstOrDefaultAsync(k => k.Key == scopedIdempotencyKey && k.WorkflowId == workflow.Id, ct);
                    if (existingKey is not null && existingKey.ExpiresAt > now)
                    {
                        var cached = await _db.WorkflowExecutions.AsNoTracking()
                            .FirstOrDefaultAsync(e => e.Id == existingKey.ExecutionId, ct);
                        if (cached is not null && !IsNeverStartedRecoveryCancellation(cached))
                            return (cached, null);

                        // Missing executions and executions proven never to have crossed engine
                        // ownership are stale reservations. Replacing the row inside this
                        // transaction lets the same caller retry after restart without weakening
                        // deduplication for in-flight executions with ambiguous side effects.
                        _db.IdempotencyKeys.Remove(existingKey);
                    }
                    else if (existingKey is not null)
                    {
                        _db.IdempotencyKeys.Remove(existingKey);
                    }

                    var created = _executionDispatch.AddPendingExecution(dispatchIntent);
                    _db.IdempotencyKeys.Add(new IdempotencyKey
                    {
                        Id = Guid.NewGuid(),
                        Key = scopedIdempotencyKey,
                        WorkflowId = workflow.Id,
                        ExecutionId = created.Id,
                        FirstSeenAt = now,
                        ExpiresAt = now.AddHours(24),
                    });
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return ((WorkflowExecution?)null, (WorkflowExecution?)created);
                });
            }
            catch (DbUpdateException)
            {
                _db.ChangeTracker.Clear();
                var replay = await FindIdempotencyReplayAsync(_db, scopedIdempotencyKey, workflow.Id, ct);
                if (replay is not null)
                    return IdempotentReplay(replay);

                return Conflict(new { message = "Idempotency-Key is currently being processed; retry with the same key." });
            }

            // Replay is decided inside the transaction but answered out here: an early return
            // from within strategy.ExecuteAsync would escape the retry unit.
            if (outcome.Replayed is not null)
                return IdempotentReplay(outcome.Replayed);

            pending = outcome.Fresh!;
            NodePilot.Api.Telemetry.ApiMetrics.IdempotencyKeyHits.Add(1,
                new KeyValuePair<string, object?>("result", "fresh"));

            _executionDispatch.NotifyCommitted();
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
                ("integrationId", keyScope.IntegrationId),
                ("executionId", pending.Id),
                ("idempotencyKeyUsed", idempotencyStorageKey is not null),
                ("parameterCount", parameters?.Count ?? 0)),
            ct);

        if (HttpContext is not null)
            Response.Headers.Location = $"/api/executions/{pending.Id}";
        return Accepted(ToResponse(pending));
    }
}

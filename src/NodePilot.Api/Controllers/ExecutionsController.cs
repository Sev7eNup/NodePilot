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
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ExecutionsController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly IWorkflowEngine _engine;
    private readonly ExecutionDispatchService _executionDispatch;
    private readonly OutputRedactor _redactor;
    private readonly IAuditWriter _audit;
    private readonly NodePilot.Core.Interfaces.IResourceAuthorizationService _authz;
    private readonly IMaintenanceWindowEvaluator _maintenance;

    public ExecutionsController(
        NodePilotDbContext db,
        IWorkflowEngine engine,
        ExecutionDispatchService executionDispatch,
        OutputRedactor redactor,
        IAuditWriter audit,
        NodePilot.Core.Interfaces.IResourceAuthorizationService authz,
        IMaintenanceWindowEvaluator maintenance)
    {
        _db = db;
        _engine = engine;
        _executionDispatch = executionDispatch;
        _redactor = redactor;
        _audit = audit;
        _authz = authz;
        _maintenance = maintenance;
    }

    /// <summary>
    /// RBAC: applies the accessible-folder filter to a WorkflowExecutions query so list
    /// endpoints never leak a row whose workflow's folder the user can't read. Returns
    /// the original query unchanged for global Admin (Unrestricted set).
    /// </summary>
    private async Task<IQueryable<NodePilot.Core.Models.WorkflowExecution>> ApplyExecutionAccessFilterAsync(
        IQueryable<NodePilot.Core.Models.WorkflowExecution> query, CancellationToken ct)
    {
        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        if (accessible.IsUnrestricted) return query;
        if (accessible.FolderIds.Count == 0)
            return query.Where(_ => false);
        // Inner-join semantics: pulls each execution's workflow folder via the navigation
        // property. Translates to a single JOIN on Postgres + SQL Server.
        return query.Where(e => accessible.FolderIds.Contains(e.Workflow.FolderId));
    }

    /// <summary>
    /// Viewer-scoped redaction for fields that may carry secrets passed via trigger payloads
    /// (webhook bodies, manual-trigger params) or leaked through step output. Admin/Operator
    /// see the raw values — they need them to debug script failures. Every other role gets the
    /// OutputRedactor treatment so an audit-only account cannot bulk-scrape executions for
    /// webhook-body secrets or step-stdout tokens.
    /// </summary>
    private bool IsPrivileged => User.IsPrivileged();
    private string? Scrub(string? value)
        => IsPrivileged ? value : _redactor.Redact(value);

    // Instance (not static) and routes user-facing fields through Scrub() so any future read
    // path that grabs this helper inherits the same role-gradient redaction as GetAll/GetById.
    // For Admin/Operator (the only callers today: Execute + Retry) Scrub() is a pass-through,
    // so behavior is unchanged.
    private ExecutionResponse ToResponse(WorkflowExecution execution) => new(
        execution.Id,
        execution.WorkflowId,
        execution.Status.ToString(),
        execution.StartedAt,
        execution.CompletedAt,
        execution.TriggeredBy,
        Scrub(execution.ErrorMessage),
        execution.TraceId,
        execution.SpanId,
        Scrub(execution.ReturnData),
        Scrub(execution.InputParametersJson));

    [HttpGet]
    public async Task<ActionResult<List<ExecutionResponse>>> GetAll(
        [FromQuery] Guid? workflowId,
        [FromQuery] bool activeOnly = false,
        [FromQuery] bool terminalOnly = false,
        CancellationToken ct = default)
    {
        var query = _db.WorkflowExecutions.AsNoTracking().AsQueryable();
        // RBAC: hide executions whose workflow's folder the user can't read.
        query = await ApplyExecutionAccessFilterAsync(query, ct);

        if (workflowId.HasValue)
            query = query.Where(e => e.WorkflowId == workflowId.Value);

        if (activeOnly)
            query = query.Where(e =>
                e.Status == ExecutionStatus.Running ||
                e.Status == ExecutionStatus.Pending ||
                e.Status == ExecutionStatus.Paused);

        // The UI's History tab uses this filter so running jobs aren't shown twice
        // (once in the live panel, once in the history list) — live = what's running now,
        // history = what already finished. Combining this with activeOnly yields an empty
        // result set, which is a valid (if pointless) state.
        if (terminalOnly)
            query = query.Where(e =>
                e.Status == ExecutionStatus.Succeeded ||
                e.Status == ExecutionStatus.Failed ||
                e.Status == ExecutionStatus.Cancelled);

        var rows = await query
            .OrderByDescending(e => e.StartedAt)
            .Take(500)
            .Select(e => new { e.Id, e.WorkflowId, e.Status, e.StartedAt, e.CompletedAt,
                e.TriggeredBy, e.ErrorMessage, e.TraceId, e.SpanId, e.ReturnData, e.InputParametersJson,
                e.StartedByUserId, e.ParentExecutionId })
            .ToListAsync(ct);

        // The extra history-list columns are resolved via four batched queries — each one
        // matches against the IN-list of up to 500 row IDs collected above. Doing a sub-select
        // per row would mean 500 round-trips on Postgres + SQL Server; this approach stays at
        // 4 queries regardless of how many rows are in the window.
        var execIds = rows.Select(r => r.Id).ToList();

        // 1) StartedByUserId -> Username. Only runs that have a human initiator carry this —
        // trigger-driven runs (scheduler/webhook/file/db/eventlog) have StartedByUserId=null
        // and the UI shows a "—" for those.
        var userIds = rows
            .Where(r => r.StartedByUserId.HasValue)
            .Select(r => r.StartedByUserId!.Value)
            .Distinct()
            .ToList();
        var userNames = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Username })
                .ToDictionaryAsync(x => x.Id, x => x.Username, ct);

        // 2) ParentExecutionId -> the parent's workflow name (used for the sub-workflow badge).
        // Top-level runs have no entry in this dict; the UI simply renders nothing for them.
        var parentIds = rows
            .Where(r => r.ParentExecutionId.HasValue)
            .Select(r => r.ParentExecutionId!.Value)
            .Distinct()
            .ToList();
        var parentNames = parentIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.WorkflowExecutions.AsNoTracking()
                .Where(p => parentIds.Contains(p.Id))
                .Select(p => new { p.Id, WorkflowName = p.Workflow.Name })
                .ToDictionaryAsync(x => x.Id, x => x.WorkflowName, ct);

        // 3) Step progress: total + skipped counts per execution in a single GROUP BY. This is
        // provider-portable — EF Core translates it into a compact aggregate SQL statement on
        // both production database backends as well as the SQLite test backend.
        var stepCounts = await _db.StepExecutions.AsNoTracking()
            .Where(s => execIds.Contains(s.WorkflowExecutionId))
            .GroupBy(s => s.WorkflowExecutionId)
            .Select(g => new
            {
                ExecId = g.Key,
                Total = g.Count(),
                Skipped = g.Count(s => s.Status == ExecutionStatus.Skipped),
            })
            .ToDictionaryAsync(x => x.ExecId, ct);

        // 4) Failed-step list: ALL failed steps per execution, sorted chronologically. Parallel
        // branches can fail at the same time; the grid joins them into a comma-separated list.
        // A narrow projection plus a C#-side GroupBy is robust and cheap here (in practice there
        // are only ever a handful of failed rows per execution).
        var failedStepRows = await _db.StepExecutions.AsNoTracking()
            .Where(s => execIds.Contains(s.WorkflowExecutionId) && s.Status == ExecutionStatus.Failed)
            .OrderBy(s => s.StartedAt)
            .Select(s => new { s.WorkflowExecutionId, s.StepId, s.StepName })
            .ToListAsync(ct);
        var failedStepsByExec = failedStepRows
            .GroupBy(s => s.WorkflowExecutionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<FailedStepRef>)g
                    .Select(s => new FailedStepRef(s.StepId, s.StepName))
                    .ToList());

        var executions = rows.Select(e =>
        {
            string? username = null;
            if (e.StartedByUserId.HasValue) userNames.TryGetValue(e.StartedByUserId.Value, out username);

            string? parentName = null;
            if (e.ParentExecutionId.HasValue) parentNames.TryGetValue(e.ParentExecutionId.Value, out parentName);

            stepCounts.TryGetValue(e.Id, out var counts);
            var stepsTotal = counts?.Total ?? 0;
            var stepsCompleted = counts is null ? 0 : counts.Total - counts.Skipped;

            failedStepsByExec.TryGetValue(e.Id, out var failed);

            return new ExecutionResponse(
                e.Id, e.WorkflowId, e.Status.ToString(), e.StartedAt, e.CompletedAt,
                e.TriggeredBy, Scrub(e.ErrorMessage), e.TraceId, e.SpanId,
                Scrub(e.ReturnData), Scrub(e.InputParametersJson),
                StartedByUsername: username,
                ParentExecutionId: e.ParentExecutionId,
                ParentWorkflowName: parentName,
                StepsTotal: stepsTotal,
                StepsCompleted: stepsCompleted,
                FailedSteps: failed);
        }).ToList();

        return Ok(executions);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ExecutionResponse>> GetById(Guid id, CancellationToken ct)
    {
        var e = await _db.WorkflowExecutions.AsNoTracking()
            .Include(x => x.Workflow)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(_authz, e.Workflow, NodePilot.Core.Interfaces.ResourceOp.Read, ct) is { } d) return d;

        // Sub-workflow runs carry a parent link; resolve the parent's workflow name so the
        // detail view can render a navigable parent chip. Top-level runs skip the lookup.
        string? parentName = null;
        if (e.ParentExecutionId.HasValue)
        {
            parentName = await _db.WorkflowExecutions.AsNoTracking()
                .Where(p => p.Id == e.ParentExecutionId.Value)
                .Select(p => p.Workflow.Name)
                .FirstOrDefaultAsync(ct);
        }

        return Ok(new ExecutionResponse(
            e.Id, e.WorkflowId, e.Status.ToString(), e.StartedAt, e.CompletedAt,
            e.TriggeredBy, Scrub(e.ErrorMessage), e.TraceId, e.SpanId,
            Scrub(e.ReturnData), Scrub(e.InputParametersJson),
            ParentExecutionId: e.ParentExecutionId,
            ParentWorkflowName: parentName));
    }

    [HttpGet("{id:guid}/steps")]
    public async Task<ActionResult<List<StepExecutionResponse>>> GetSteps(Guid id, CancellationToken ct)
    {
        // RBAC: load the parent execution + workflow so we can authz-check before
        // returning the per-step rows. A user without folder access never sees step
        // output for that workflow.
        var exec = await _db.WorkflowExecutions.AsNoTracking()
            .Include(e => e.Workflow)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (exec is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(_authz, exec.Workflow, NodePilot.Core.Interfaces.ResourceOp.Read, ct) is { } d) return d;

        var raw = await _db.StepExecutions
            .AsNoTracking()
            .Where(s => s.WorkflowExecutionId == id)
            .OrderBy(s => s.StartedAt)
            .Select(s => new { s.Id, s.StepId, s.StepName, s.StepType, s.TargetMachine,
                s.Status, s.StartedAt, s.CompletedAt, s.Output, s.ErrorOutput, s.AttemptCount,
                s.PausedAt, s.VariablesSnapshot, s.TraceOutput, s.OutputParametersJson,
                s.CustomActivityKey, s.CustomActivityVersion, s.CustomActivityHash })
            .ToListAsync(ct);

        // Map stepId -> outputVariable alias from the workflow definition. The alias isn't
        // persisted on the row, so we resolve it once per request from the parent workflow's
        // definition JSON. TryParse swallows malformed JSON and falls back to no aliases —
        // the response still lists every step, just without alias info.
        IReadOnlyDictionary<string, string> outputNameByStepId =
            NodePilot.Core.WorkflowDefinitions.WorkflowDefinitionDocument.TryParse(exec.Workflow?.DefinitionJson, out var definition)
                && definition is not null
                    ? definition.OutputNameByStepId
                    : new Dictionary<string, string>(StringComparer.Ordinal);

        var steps = raw.Select(s => new StepExecutionResponse(
            s.Id, s.StepId, s.StepName, s.StepType, s.TargetMachine,
            s.Status.ToString(), s.StartedAt, s.CompletedAt,
            Scrub(s.Output), Scrub(s.ErrorOutput), s.AttemptCount,
            // H-10: VariablesSnapshot carries raw template-resolved values (webhook bodies,
            // trigger-injected params, upstream step outputs) — Viewer roles must get the
            // same redaction treatment as Output/ErrorOutput or they can bulk-scrape
            // executions for secrets that the OutputRedactor would otherwise mask.
            s.PausedAt, Scrub(s.VariablesSnapshot),
            // Transcript already passed through OutputRedactor at persist time — Scrub
            // here is the second-line defense for legacy rows / any redactor regression.
            Scrub(s.TraceOutput),
            // OutputParametersJson is also redacted at persist (sanitized.OutputParameters)
            // — Scrub is the same belt-and-braces here.
            Scrub(s.OutputParametersJson),
            outputNameByStepId.TryGetValue(s.StepId, out var alias) ? alias : null,
            s.CustomActivityKey, s.CustomActivityVersion, s.CustomActivityHash)).ToList();

        return Ok(steps);
    }

    [HttpPost("/api/workflows/{workflowId:guid}/execute")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<ExecutionResponse>> Execute(
        Guid workflowId,
        [FromBody] ExecuteWorkflowRequest? request,
        CancellationToken ct,
        [FromQuery] bool force = false)
    {
        var workflow = await _db.Workflows.FindAsync([workflowId], ct);
        if (workflow is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(_authz, workflow, NodePilot.Core.Interfaces.ResourceOp.Run, ct) is { } d) return d;

        // IsEnabled doubles as an incident-response kill switch. External triggers already
        // respect it; without this mirror-check an Operator could keep firing a workflow that
        // an Admin just disabled for containment, defeating the only in-UI pause button.
        if (!workflow.IsEnabled)
            return BadRequest(new { message = $"Workflow '{workflow.Name}' is disabled" });

        // Maintenance-window gate. Early 423 (administratively held — same semantics as the
        // edit-lock) so the operator gets immediate, actionable feedback with the retry time
        // instead of a phantom Cancelled row. An Admin may force-run through an active window
        // with ?force=true (loudly audited); an Operator passing force=true is refused.
        var bypassMaintenance = false;
        var verdict = _maintenance.Evaluate(workflow.Id, workflow.FolderId, DateTime.UtcNow);
        if (verdict.Blocked)
        {
            if (force && !User.IsAdmin())
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Only Admins may force-run a workflow during a maintenance window." });

            if (force) // Admin force-run
            {
                bypassMaintenance = true;
                NodePilot.Api.Telemetry.ApiMetrics.MaintenanceWindowOverrides.Add(1);
                await _audit.LogAsync(AuditActions.MaintenanceWindowOverridden, "Workflow", workflow.Id,
                    AuditDetails.Json(("workflowName", workflow.Name), ("windowId", verdict.WindowId),
                        ("windowName", verdict.WindowName), ("mode", verdict.Mode?.ToString())), ct);
            }
            else
            {
                NodePilot.Api.Telemetry.ApiMetrics.MaintenanceWindowBlocks.Add(1,
                    new("source", "manual"), new("scope", "execute"));
                await _audit.LogAsync(AuditActions.ExecutionBlockedMaintenanceWindow, "Workflow", workflow.Id,
                    AuditDetails.Json(("source", "manual"), ("windowId", verdict.WindowId),
                        ("windowName", verdict.WindowName), ("mode", verdict.Mode?.ToString()),
                        ("activeUntil", verdict.ActiveUntilUtc)), ct);
                return StatusCode(StatusCodes.Status423Locked, new
                {
                    message = $"Workflow '{workflow.Name}' is blocked by maintenance window '{verdict.WindowName}'.",
                    windowId = verdict.WindowId,
                    windowName = verdict.WindowName,
                    activeUntil = verdict.ActiveUntilUtc,
                });
            }
        }

        // Queue the run into the shared dispatch worker so the engine's scoped services
        // (DbContext, CredentialStore, etc.) outlive the HTTP request cleanly.
        // The HTTP call returns 202 Accepted immediately; progress comes via SignalR.
        var parameters = request?.Parameters is null
            ? null
            : new Dictionary<string, string>(request.Parameters);

        // Reject reserved-prefix keys at ingestion. The engine filters them on
        // persistence, but the runtime VariableResolver still maps every key into
        // `manual.*` — without an ingestion-time block, a caller could set
        // `__callDepth=-1000` and bypass the recursion guard. Defense in depth.
        if (parameters is not null
            && NodePilot.Engine.Activities.WorkflowRecursion.FindReservedKey(parameters.Keys) is { } reservedKey)
        {
            return BadRequest(new { message = $"Input parameter '{reservedKey}' is reserved (keys starting with '__' are used by the engine)." });
        }
        var timeoutSeconds = request?.TimeoutSeconds;
        var debugEnabled = request?.Debug ?? false;
        // C-2: snapshot the caller id before dispatch; HttpContext is not available in the
        // worker scope. Used by Resume to enforce an owner check on debug sessions.
        var startedByUserId = this.GetCurrentUserId();
        var trigger = debugEnabled ? "debug" : "manual";
        var pending = await _executionDispatch.DispatchAsync(
            new WorkflowDispatchIntent(
                workflow.Id,
                trigger,
                parameters,
                timeoutSeconds,
                debugEnabled,
                startedByUserId,
                RequireWorkflowEnabled: true,
                Priority: ExecutionDispatchPriority.Interactive,
                BypassMaintenanceWindow: bypassMaintenance),
            ct);

        // Symmetric to EXECUTION_CANCELLED/RETRIED/RESUMED: a manual run start is its own
        // audit event, not just an Executions-table row, so the audit timeline answers
        // "who started this workflow when" without joining tables.
        await _audit.LogAsync(AuditActions.ExecutionStarted, "Execution", pending.Id,
            AuditDetails.Json(
                ("workflowId", workflow.Id),
                ("workflowName", workflow.Name),
                ("trigger", trigger),
                ("debugEnabled", debugEnabled),
                ("parameterCount", parameters?.Count ?? 0)),
            ct);

        if (HttpContext is not null)
            Response.Headers.Location = $"/api/executions/{pending.Id}";
        return Accepted(ToResponse(pending));

    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var execution = await _db.WorkflowExecutions
            .Include(e => e.Workflow)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (execution is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(_authz, execution.Workflow, NodePilot.Core.Interfaces.ResourceOp.Run, ct) is { } d) return d;

        // In-memory token cancel (works only for executions started by THIS process). "user"
        // attributes this manual single-cancel so alerting can target it (cancelledBy == "user").
        var signalled = await _engine.CancelAsync(id, "user", ct);

        // Zombie fallback: no in-memory token means this row came from a previous
        // API process (restart/crash). Force-cancel directly in the DB so the UI
        // stops showing a stale "Running" state forever.
        if (!signalled
            && (execution.Status == ExecutionStatus.Running || execution.Status == ExecutionStatus.Pending))
        {
            var wasPending = execution.Status == ExecutionStatus.Pending;
            execution.Status = ExecutionStatus.Cancelled;
            execution.CancelledBy = "user";
            execution.CompletedAt = DateTime.UtcNow;
            execution.ErrorMessage ??= wasPending
                ? "Queued execution was cancelled before dispatch."
                : "Force-cancelled: no active in-memory execution (orphaned from a previous API process).";

            var runningSteps = await _db.StepExecutions
                .Where(s => s.WorkflowExecutionId == id && s.Status == ExecutionStatus.Running)
                .ToListAsync(ct);
            foreach (var s in runningSteps)
            {
                s.Status = ExecutionStatus.Cancelled;
                s.CompletedAt = DateTime.UtcNow;
                s.ErrorOutput ??= "Step force-cancelled (parent execution was orphaned).";
            }
            await _db.SaveChangesAsync(ct);
        }

        await _audit.LogAsync(AuditActions.ExecutionCancelled, "Execution", id,
            AuditDetails.Json(("workflowId", execution.WorkflowId), ("signalledInMemory", signalled)), ct);

        return NoContent();
    }

    /// <summary>
    /// Cancels EVERY running execution of a workflow — the one-click "stop that thing
    /// from firing" for incident response. Pairs naturally with POST /api/workflows/{id}/disable:
    /// disable stops NEW fires, cancel-all stops CURRENT fires. Returns the count of rows
    /// whose Cancel was signalled. Idempotent: a second call returns 0.
    /// </summary>
    [HttpPost("/api/workflows/{workflowId:guid}/cancel-all")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<object>> CancelAllForWorkflow(Guid workflowId, CancellationToken ct)
    {
        // Ensure the workflow exists before spending effort — prevents "cancel all on a
        // typo'd GUID silently succeeds with 0" which hides operator mistakes.
        var workflow = await _db.Workflows.FindAsync([workflowId], ct);
        if (workflow is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(_authz, workflow, NodePilot.Core.Interfaces.ResourceOp.Run, ct) is { } d) return d;

        var running = await _db.WorkflowExecutions
            .Where(e => e.WorkflowId == workflowId
                        && (e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Pending))
            .Select(e => e.Id)
            .ToListAsync(ct);

        int signalledCount = 0;
        foreach (var execId in running)
        {
            if (await _engine.CancelAsync(execId, "cancelAll", ct)) signalledCount++;
            else
            {
                // Zombie row left over from a previous process — reconcile in DB so it
                // doesn't stay Running forever. Single UPDATE statement, no load round-trip.
                await _db.WorkflowExecutions
                    .Where(e => e.Id == execId
                                && (e.Status == ExecutionStatus.Running || e.Status == ExecutionStatus.Pending))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.Status, ExecutionStatus.Cancelled)
                        .SetProperty(e => e.CancelledBy, "cancelAll")
                        .SetProperty(e => e.CompletedAt, DateTime.UtcNow)
                        .SetProperty(e => e.ErrorMessage, e => e.ErrorMessage ?? "Force-cancelled via /cancel-all (orphaned from a previous API process).")
                    , ct);
            }
        }

        await _audit.LogAsync(AuditActions.WorkflowCancelAll, "Workflow", workflowId,
            AuditDetails.Json(("runningFound", running.Count), ("signalledInMemory", signalledCount)), ct);

        return Ok(new { total = running.Count, signalled = signalledCount });
    }

    /// <summary>
    /// Reruns a terminal execution with the same input parameters that the original was
    /// started with (captured on WorkflowExecution.InputParametersJson). Creates a NEW
    /// execution row — the original is never mutated. Useful for "it failed because of a
    /// transient network blip, try again with identical inputs" flows.
    /// </summary>
    [HttpPost("{id:guid}/retry")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<ExecutionResponse>> Retry(
        Guid id,
        CancellationToken ct)
    {
        var original = await _db.WorkflowExecutions.FindAsync([id], ct);
        if (original is null) return NotFound();

        // Only allow retry on terminal states. A Running/Pending retry would race the
        // original and produce confusing duplicate audit + log output.
        if (original.Status != Core.Enums.ExecutionStatus.Succeeded
            && original.Status != Core.Enums.ExecutionStatus.Failed
            && original.Status != Core.Enums.ExecutionStatus.Cancelled)
        {
            return BadRequest(new { message = $"Cannot retry an execution in state '{original.Status}' — wait until it reaches a terminal status." });
        }

        var workflow = await _db.Workflows.FindAsync([original.WorkflowId], ct);
        if (workflow is null) return NotFound(new { message = "Parent workflow no longer exists." });
        if (await this.RequireWorkflowAccessAsync(_authz, workflow, NodePilot.Core.Interfaces.ResourceOp.Run, ct) is { } d) return d;
        if (!workflow.IsEnabled) return BadRequest(new { message = $"Workflow '{workflow.Name}' is disabled." });

        // Deserialize snapshot. Missing / malformed JSON → fresh empty params (still audits
        // as a retry so the lineage stays visible).
        Dictionary<string, string>? parameters = null;
        if (!string.IsNullOrWhiteSpace(original.InputParametersJson))
        {
            try { parameters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(original.InputParametersJson); }
            catch { /* swallow — original's snapshot is corrupt; run without params */ }
        }

        // C-2: tag retry with the user who clicked the retry button (not the original author).
        var retriedByUserId = this.GetCurrentUserId();
        var trigger = $"retry:{id}";
        var pending = await _executionDispatch.DispatchAsync(
            new WorkflowDispatchIntent(
                original.WorkflowId,
                trigger,
                parameters,
                StartedByUserId: retriedByUserId,
                RequireWorkflowEnabled: true,
                MissingWorkflowMessage: "Queued retry was not dispatched because the workflow no longer exists.",
                PreOwnershipFailurePrefix: "Queued retry failed before the engine could take ownership",
                EnqueueFailureMessage: "Queued retry was not dispatched because the request was cancelled before enqueue completed.",
                // Retry is a recovery operation on an already-known run — not gated by maintenance
                // windows. Blocking incident recovery during a blackout would be counterproductive.
                RequireMaintenanceWindowCheck: false),
            ct);

        await _audit.LogAsync(AuditActions.ExecutionRetried, "Execution", pending.Id,
            AuditDetails.Json(("originalExecutionId", id), ("workflowId", original.WorkflowId)), ct);
        if (HttpContext is not null)
            Response.Headers.Location = $"/api/executions/{pending.Id}";
        return Accepted(ToResponse(pending));
    }
}

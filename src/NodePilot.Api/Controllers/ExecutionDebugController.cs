using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Core.Audit;
using NodePilot.Api.Security;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Step-debugger surface: resume a paused execution, query which steps are currently paused.
/// Read/lifecycle endpoints (GetById, Cancel, Retry, ...) live in <see cref="ExecutionsController"/>;
/// this controller only owns the debug semantics so the larger controller stays focused on
/// the execution-history flow.
/// </summary>
[ApiController]
[Authorize]
public class ExecutionDebugController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly IWorkflowEngine _engine;
    private readonly IAuditWriter _audit;
    private readonly NodePilot.Core.Interfaces.IResourceAuthorizationService _authz;

    public ExecutionDebugController(
        NodePilotDbContext db,
        IWorkflowEngine engine,
        IAuditWriter audit,
        NodePilot.Core.Interfaces.IResourceAuthorizationService authz)
    {
        _db = db;
        _engine = engine;
        _audit = audit;
        _authz = authz;
    }

    /// <summary>Debug resume: releases a step that is paused at a breakpoint. The mode
    /// decides what happens next: "continue" runs until the next breakpoint or the end,
    /// "stepOver" always pauses again at the next step, and "stop" cancels the execution.
    /// The caller can optionally supply variable overrides — these are merged into the
    /// variables dictionary BEFORE the executor runs the step, enabling "what-if" testing.</summary>
    [HttpPost("/api/executions/{id:guid}/resume")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Resume(Guid id, [FromBody] ResumeDebugRequest req, CancellationToken ct)
    {
        if (req is null) return BadRequest("Body required");
        var execution = await _db.WorkflowExecutions
            .Include(e => e.Workflow)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (execution is null) return NotFound();
        // RBAC: caller needs Run on the workflow's folder. The follow-up
        // StartedByUserId-ownership check below is unchanged — both gates apply.
        if (!await _authz.CanAccessWorkflowAsync(User, execution.Workflow.FolderId, NodePilot.Core.Interfaces.ResourceOp.Read, ct))
            return NotFound();
        if (!await _authz.CanAccessWorkflowAsync(User, execution.Workflow.FolderId, NodePilot.Core.Interfaces.ResourceOp.Run, ct))
            return new ObjectResult(new { message = "Insufficient folder permissions" })
            { StatusCode = StatusCodes.Status403Forbidden };

        // C-2: Debug-Session-Ownership. Only the user who started the debug run (or an
        // Admin) may step/continue/stop it. Without this, any Operator could interfere
        // with another Operator's debugging session, inspect their variable snapshots
        // via SignalR, or inject Overrides that tamper with the run mid-flight.
        // StartedByUserId is null for scheduler/trigger-driven runs — those aren't
        // resumable anyway (no debug flag), so that branch is moot.
        var currentUserId = this.GetCurrentUserId();
        if (!User.IsAdmin()
            && execution.StartedByUserId is not null
            && execution.StartedByUserId != currentUserId)
        {
            return Forbid();
        }

        // stepId is required because parallel branches each have their own pending
        // continuation (TaskCompletionSource) waiting to be released.
        if (string.IsNullOrWhiteSpace(req.StepId))
            return BadRequest("stepId required");

        // L-2: bound the Overrides dict. Without caps a caller could inject a dictionary
        // large enough to OOM the engine's variable-resolution pass or produce multi-MB
        // audit-log entries (the details JSON below serializes the count + keys).
        // Limits chosen well above any legitimate debug-step use case.
        if (req.Overrides is { Count: > 256 })
            return BadRequest(new { error = "Too many overrides (max 256)" });
        if (req.Overrides is { Count: > 0 })
        {
            foreach (var kv in req.Overrides)
            {
                if (kv.Value?.Length > 65536)
                    return BadRequest(new { error = $"Override value for '{kv.Key}' exceeds 64 KiB" });
            }
        }

        var cmd = req.Mode?.ToLowerInvariant() switch
        {
            "continue" => DebugResumeCommand.Continue,
            "stepover" or "step-over" => DebugResumeCommand.StepOver,
            "stop" => DebugResumeCommand.Stop,
            _ => (DebugResumeCommand?)null,
        };
        if (cmd is null)
            return BadRequest("mode must be one of: continue, stepOver, stop");

        var signalled = _engine.Resume(id, req.StepId, cmd.Value, req.Overrides);
        if (!signalled)
            // No step is waiting: either the execution wasn't paused, or the step was already
            // resumed. 409 rather than 404, to describe the race where the user clicked
            // "Continue" twice.
            return Conflict(new { message = "No paused step with this id — execution may have already resumed or ended." });

        await _audit.LogAsync(
            cmd switch {
                DebugResumeCommand.Continue => AuditActions.ExecutionResumed,
                DebugResumeCommand.StepOver => AuditActions.ExecutionStepOver,
                DebugResumeCommand.Stop => AuditActions.ExecutionDebugStop,
                _ => AuditActions.ExecutionResumed,
            },
            "Execution", id,
            AuditDetails.Json(("workflowId", execution.WorkflowId), ("stepId", req.StepId), ("overrides", req.Overrides?.Count ?? 0)),
            ct);

        return NoContent();
    }

    /// <summary>GET /api/executions/{id}/paused-steps — used for page-reload scenarios:
    /// if the user missed the SignalR events because of a browser refresh, the frontend
    /// polls this endpoint to rebuild the debug UI's state.</summary>
    [HttpGet("/api/executions/{id:guid}/paused-steps")]
    public async Task<ActionResult<IEnumerable<string>>> GetPausedSteps(Guid id, CancellationToken ct)
    {
        // RBAC: paused-step ids are read-only metadata but they're tied to the workflow
        // — gate by Read on the folder. Returns 404 to mask existence for non-readers.
        var execution = await _db.WorkflowExecutions.AsNoTracking()
            .Include(e => e.Workflow)
            .FirstOrDefaultAsync(e => e.Id == id, ct);
        if (execution is null) return NotFound();
        if (!await _authz.CanAccessWorkflowAsync(User, execution.Workflow.FolderId, NodePilot.Core.Interfaces.ResourceOp.Read, ct))
            return NotFound();
        return Ok(_engine.GetPausedSteps(id));
    }

}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using System.Text.Json;

namespace NodePilot.Api.Controllers;

[ApiController, Route("api/agents"), Authorize]
public sealed class AgentsController(NodePilotDbContext db, IResourceAuthorizationService authz) : ControllerBase
{
    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<AgentRunResponse>>> GetRuns([FromQuery] Guid executionId, CancellationToken ct)
    {
        var execution = await db.WorkflowExecutions.AsNoTracking().Include(x => x.Workflow).SingleOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(authz, execution.Workflow, ResourceOp.Read, ct) is { } denied) return denied;
        var rows = await db.AgentRuns.AsNoTracking().Where(x => x.WorkflowExecutionId == executionId).OrderBy(x => x.StartedAt)
            .Select(x => new {
                Run = new AgentRunResponse(x.Id, x.WorkflowExecutionId, x.StepId, x.Status, x.StartedAt, x.CompletedAt,
                    x.Result, x.Error, x.ModelCalls, x.ToolCalls, x.Delegations, x.InputTokens, x.OutputTokens, "unassessed", null),
                Assessment = db.AgentRunEvents.Where(e => e.AgentRunId == x.Id && e.Kind == "run_conclusion")
                    .OrderByDescending(e => e.Sequence).Select(e => e.Content).FirstOrDefault()
            }).ToListAsync(ct);
        return Ok(rows.Select(x => WithAssessment(x.Run, x.Assessment)).ToArray());
    }

    private static AgentRunResponse WithAssessment(AgentRunResponse run, string? json)
    {
        if (run.Status != "Succeeded" || json is null) return run;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("outcome", out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is "completed" or "partial" or "blocked"
                && doc.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                return run with { Outcome = value.GetString()!, OutcomeReason = reason.GetString() };
        }
        catch (JsonException) { /* Older or truncated journals have no final assessment. */ }
        return run;
    }

    [HttpGet("runs/{id:guid}/events")]
    public async Task<ActionResult<IReadOnlyList<AgentRunEventResponse>>> GetEvents(Guid id,
        [FromQuery] long after = 0, [FromQuery] int pageSize = 200, CancellationToken ct = default)
    {
        var run = await db.AgentRuns.AsNoTracking().Include(x => x.WorkflowExecution).ThenInclude(x => x.Workflow).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run is null) return NotFound();
        if (await this.RequireWorkflowAccessAsync(authz, run.WorkflowExecution.Workflow, ResourceOp.Read, ct) is { } denied) return denied;
        return Ok(await db.AgentRunEvents.AsNoTracking().Where(x => x.AgentRunId == id && x.Sequence > Math.Max(0, after))
            .OrderBy(x => x.Sequence).Take(Math.Clamp(pageSize, 1, 500)).Select(x => new AgentRunEventResponse(
                x.AgentRunId, x.Sequence, x.Timestamp, x.MemberId, x.Kind, x.ToolName, x.Content)).ToListAsync(ct));
    }
}

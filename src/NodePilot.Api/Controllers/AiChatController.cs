using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Api.Ai;
using NodePilot.Ai;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Controllers;

/// <summary>
/// AI workflow assistant: a multi-turn chat about the currently-open workflow — it explains
/// the workflow and, on request, proposes full definition rewrites. Deliberately <b>separate</b>
/// from <see cref="AiController"/>: this endpoint is open to <b>every</b> authenticated role
/// (Viewers may ask questions too), while change proposals are only generated server-side for
/// Admin/Operator. Persistence never happens here — the frontend applies the proposal to the
/// canvas and saves it through the normal edit-lock/publish flow.
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize]
[EnableRateLimiting("ai-generate")]
public sealed class AiChatController : ControllerBase
{
    private const int MaxWorkflowJsonBytes = 5 * 1024 * 1024;
    private const int MaxJsonDepth = 64;
    private const int MaxHistoryTurns = 20;
    private const int MaxHistoryChars = 50_000;
    private const int MaxQuestionChars = 8_000;

    private static readonly HashSet<string> _allowedRoles = new(StringComparer.OrdinalIgnoreCase) { "user", "assistant" };

    private readonly IOptionsMonitor<LlmOptions> _options;
    private readonly WorkflowAssistantService _assistant;
    private readonly IAuditWriter _audit;
    private readonly NodePilotDbContext _db;
    private readonly IResourceAuthorizationService _authz;
    private readonly ILogger<AiChatController> _logger;

    private static readonly string[] _aiActivityActions = { "AI_WORKFLOW_EXPLAINED", "AI_PROPOSAL_APPLIED" };

    public AiChatController(
        IOptionsMonitor<LlmOptions> options,
        WorkflowAssistantService assistant,
        IAuditWriter audit,
        NodePilotDbContext db,
        IResourceAuthorizationService authz,
        ILogger<AiChatController> logger)
    {
        _options = options;
        _assistant = assistant;
        _audit = audit;
        _db = db;
        _authz = authz;
        _logger = logger;
    }

    /// <summary>
    /// Folder-RBAC gate for the workflow-scoped AI audit endpoints. Loads the workflow, returns
    /// 404 if it doesn't exist, and then delegates to the shared gate
    /// (<see cref="ResourceAuthorizationGateExtensions.RequireWorkflowAccessAsync"/>): also 404
    /// when the caller can't even read it (existence is masked — no 403/404 differential that
    /// would help someone probe for valid IDs), 403 when they can read but can't perform the
    /// requested operation.
    /// </summary>
    private async Task<ActionResult?> RequireWorkflowAccessAsync(Guid workflowId, ResourceOp op, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return NotFound();
        return await this.RequireWorkflowAccessAsync(_authz, workflow, op, ct);
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat(WorkflowChatRequest request, CancellationToken ct)
    {
        if (!_options.CurrentValue.Enabled)
            return ServiceUnavailable("LLM_DISABLED",
                "AI assistant is disabled. Set Llm:Enabled=true in configuration.");

        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { code = "PROMPT_EMPTY", message = "Question must not be empty." });
        if (request.Question.Length > MaxQuestionChars)
            return BadRequest(new { code = "QUESTION_TOO_LONG", message = $"Question exceeds {MaxQuestionChars} characters." });

        if (string.IsNullOrWhiteSpace(request.WorkflowJson))
            return BadRequest(new { code = "WORKFLOW_JSON_EMPTY", message = "WorkflowJson must not be empty." });
        if (Encoding.UTF8.GetByteCount(request.WorkflowJson) > MaxWorkflowJsonBytes)
            return BadRequest(new { code = "WORKFLOW_JSON_TOO_LARGE", message = "WorkflowJson exceeds the 5 MiB cap." });

        var history = NormalizeHistory(request.History);
        if (history.Count > MaxHistoryTurns)
            return BadRequest(new { code = "HISTORY_TOO_LONG", message = $"History exceeds {MaxHistoryTurns} turns." });
        if (history.Sum(h => h.Content.Length) > MaxHistoryChars)
            return BadRequest(new { code = "HISTORY_TOO_LONG", message = $"History exceeds {MaxHistoryChars} characters." });

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(request.WorkflowJson, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
        }
        catch (JsonException)
        {
            return BadRequest(new { code = "WORKFLOW_JSON_INVALID", message = "WorkflowJson is not valid JSON." });
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return BadRequest(new { code = "WORKFLOW_JSON_INVALID", message = "WorkflowJson must be a JSON object." });

            var allowModify = User.IsPrivileged(); // Admin or Operator

            // Folder-RBAC gate for the execution-log tools: the workflowId comes from the client —
            // without this check, any authenticated user could exfiltrate step outputs from other
            // people's workflows through the assistant. No access / unknown / unsaved workflow =>
            // the tools are silently disabled (the chat itself still proceeds; existence stays
            // masked, no 404/403 differential). Deliberately NOT using RequireWorkflowAccessAsync
            // here — the chat request itself must never fail because of this check.
            var allowExecutionTools = false;
            if (_options.CurrentValue.EnableToolCalling && request.WorkflowId is { } wfId && wfId != Guid.Empty)
            {
                var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wfId, ct);
                allowExecutionTools = workflow is not null
                    && await _authz.CanAccessWorkflowAsync(User, workflow.FolderId, ResourceOp.Read, ct);
            }

            var normalized = request with { History = history };

            await using var en = _assistant
                .StreamChatAsync(normalized, doc.RootElement, allowModify, allowExecutionTools, ct)
                .GetAsyncEnumerator(ct);

            // Peek at the first event: this way, an error before streaming starts still comes
            // back as a normal HTTP status (which the frontend's authedFetch handles), rather
            // than as an SSE error event.
            bool hasFirst;
            try
            {
                hasFirst = await en.MoveNextAsync();
            }
            catch (LlmException ex)
            {
                RecordError(ex);
                return MapLlmException(ex);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                RecordResult("cancelled");
                return new EmptyResult(); // client disconnected before the first token arrived
            }

            await using var sse = SseResponseWriter.Begin(Response);

            var proposed = false;
            var model = "unknown";
            var durationMs = 0;
            int? promptTokens = null, completionTokens = null;

            async Task Write(ChatStreamEvent e)
            {
                switch (e)
                {
                    case ChatStreamEvent.DeltaEvent d:
                        await sse.WriteAsync("delta", new { text = d.Text }, ct);
                        break;
                    case ChatStreamEvent.BuildingEvent:
                        await sse.WriteAsync("building", new { }, ct);
                        break;
                    case ChatStreamEvent.ProposalEvent p:
                        proposed = true;
                        await sse.WriteAsync("proposal", p.Dto, ct);
                        break;
                    case ChatStreamEvent.ToolCallEvent tc:
                        await sse.WriteAsync("tool_call", new { toolName = tc.ToolName, toolId = tc.ToolId }, ct);
                        break;
                    case ChatStreamEvent.ToolResultEvent tr:
                        await sse.WriteAsync("tool_result", new { toolId = tr.ToolId, toolName = tr.ToolName }, ct);
                        break;
                    case ChatStreamEvent.DoneEvent done:
                        model = done.Model;
                        durationMs = done.DurationMs;
                        promptTokens = done.PromptTokens;
                        completionTokens = done.CompletionTokens;
                        await sse.WriteAsync("done", new { model = done.Model, durationMs = done.DurationMs, promptTokens = done.PromptTokens, completionTokens = done.CompletionTokens }, ct);
                        break;
                }
            }

            try
            {
                if (hasFirst) await Write(en.Current);
                while (await en.MoveNextAsync()) await Write(en.Current);
            }
            catch (LlmException ex)
            {
                RecordError(ex);
                await sse.WriteAsync("error", new { code = LlmErrorCodes.For(ex), message = ex.Message }, CancellationToken.None);
                return new EmptyResult();
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                RecordResult("cancelled");
                await AuditAsync(model, durationMs, proposed, cancelled: true, history.Count + 1, request.WorkflowId);
                return new EmptyResult();
            }

            RecordSuccess(model, durationMs, promptTokens, completionTokens);
            await AuditAsync(model, durationMs, proposed, cancelled: false, history.Count + 1, request.WorkflowId, ct);
            return new EmptyResult();
        }
    }

    /// <summary>
    /// Records that an AI proposal was applied to the canvas -> audit event
    /// <c>AI_PROPOSAL_APPLIED</c>. Persistence still goes through the normal edit-lock/publish
    /// flow; this call is only the audit trail. Admin/Operator only (Viewers may not apply
    /// changes).
    /// </summary>
    [HttpPost("chat/applied")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> ChatApplied([FromBody] ChatAppliedRequest request, CancellationToken ct)
    {
        if (request.WorkflowId == Guid.Empty)
            return BadRequest(new { code = "WORKFLOW_ID_REQUIRED", message = "workflowId is required." });

        // Applying a proposal changes the workflow -> require Edit on the folder (404 if the
        // caller can't even read it).
        if (await RequireWorkflowAccessAsync(request.WorkflowId, ResourceOp.Edit, ct) is { } denied)
            return denied;

        await _audit.LogAsync(AuditActions.AiProposalApplied, "Workflow", request.WorkflowId,
            AuditDetails.Json(("nodeCount", request.NodeCount), ("edgeCount", request.EdgeCount)), ct);
        return NoContent();
    }

    /// <summary>
    /// Workflow-scoped AI activity (only this workflow's <c>AI_WORKFLOW_EXPLAINED</c> /
    /// <c>AI_PROPOSAL_APPLIED</c> entries). Deliberately separate from the Admin-only
    /// <c>/api/audit</c> endpoint, so Operators can see their own AI activity without needing
    /// global audit access.
    /// </summary>
    [HttpGet("chat/activity/{workflowId:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<IReadOnlyList<AiActivityEntryDto>>> ChatActivity(
        Guid workflowId, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        // Reading activity requires reading the workflow -> require Read (404 if unreadable/nonexistent).
        if (await RequireWorkflowAccessAsync(workflowId, ResourceOp.Read, ct) is { } denied)
            return denied;

        take = Math.Clamp(take, 1, 100);
        var rows = await _db.AuditLog.AsNoTracking()
            .Where(a => a.ResourceId == workflowId && _aiActivityActions.Contains(a.Action))
            .OrderByDescending(a => a.Timestamp)
            .Take(take)
            .Select(a => new AiActivityEntryDto(a.Timestamp, a.UserId, a.Username, a.Action, a.Details))
            .ToListAsync(ct);
        return Ok(rows);
    }

    private static void RecordResult(string result) =>
        ApiMetrics.LlmCalls.Add(1, new(TelemetryConstants.Attributes.LlmKind, "chat"), new("result", result));

    private void RecordError(LlmException ex)
    {
        RecordResult("error");
        ApiMetrics.LlmErrors.Add(1,
            new(TelemetryConstants.Attributes.LlmKind, "chat"),
            new(TelemetryConstants.Attributes.LlmErrorKind, ex.Kind.ToString()));
        _logger.LogWarning(ex, "LLM chat stream failed: {Kind}", ex.Kind);
    }

    private static void RecordSuccess(string model, int durationMs, int? promptTokens, int? completionTokens)
    {
        RecordResult("success");
        ApiMetrics.LlmCallDuration.Record(durationMs,
            new(TelemetryConstants.Attributes.LlmKind, "chat"),
            new(TelemetryConstants.Attributes.LlmModel, model));
        if (promptTokens.HasValue)
            ApiMetrics.LlmTokens.Add(promptTokens.Value,
                new(TelemetryConstants.Attributes.LlmKind, "chat"),
                new(TelemetryConstants.Attributes.LlmModel, model), new("token_type", "prompt"));
        if (completionTokens.HasValue)
            ApiMetrics.LlmTokens.Add(completionTokens.Value,
                new(TelemetryConstants.Attributes.LlmKind, "chat"),
                new(TelemetryConstants.Attributes.LlmModel, model), new("token_type", "completion"));
    }

    private Task AuditAsync(string model, int durationMs, bool proposed, bool cancelled, int turnCount,
        Guid? workflowId, CancellationToken ct = default) =>
        _audit.LogAsync(AuditActions.AiWorkflowExplained, "Workflow", workflowId,
            AuditDetails.Json(
                ("model", model),
                ("durationMs", durationMs),
                ("modifyProposed", proposed),
                ("cancelled", cancelled),
                ("turnCount", turnCount)),
            ct);

    /// <summary>
    /// Keeps only turns with role <c>user</c>/<c>assistant</c> and non-empty content, and
    /// normalizes the role to lowercase. This stops a client from smuggling in a <c>system</c>
    /// role or similar.
    /// </summary>
    private static List<AiChatTurnDto> NormalizeHistory(IReadOnlyList<AiChatTurnDto>? history)
    {
        if (history is null) return new List<AiChatTurnDto>();
        return history
            .Where(h => h is not null
                        && !string.IsNullOrWhiteSpace(h.Content)
                        && h.Role is not null
                        && _allowedRoles.Contains(h.Role))
            .Select(h => new AiChatTurnDto(h.Role.ToLowerInvariant(), h.Content))
            .ToList();
    }

    private ActionResult MapLlmException(LlmException ex)
    {
        _logger.LogWarning(ex, "LLM chat call failed: {Kind}", ex.Kind);
        return ex.Kind switch
        {
            LlmErrorKind.Unreachable => ServiceUnavailable("LLM_UNREACHABLE", ex.Message),
            LlmErrorKind.Timeout => ServiceUnavailable("LLM_TIMEOUT", ex.Message),
            LlmErrorKind.Unauthorized => ServiceUnavailable("LLM_UNAUTHORIZED",
                "LLM endpoint rejected the configured API key. Check Llm:ApiKey."),
            LlmErrorKind.RateLimited => ServiceUnavailable("LLM_RATE_LIMITED",
                "LLM endpoint rate-limited the request. Try again shortly."),
            LlmErrorKind.MalformedResponse => BadGateway("LLM_MALFORMED_RESPONSE", ex.Message, ex.BodyExcerpt),
            LlmErrorKind.UpstreamError => BadGateway("LLM_UPSTREAM_ERROR",
                $"LLM endpoint returned HTTP {ex.HttpStatus}.", ex.BodyExcerpt),
            _ => StatusCode(StatusCodes.Status500InternalServerError,
                new { code = "LLM_UNKNOWN", message = ex.Message }),
        };
    }

    private ObjectResult ServiceUnavailable(string code, string message)
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new { code, message });

    private ObjectResult BadGateway(string code, string message, string? bodyExcerpt = null)
        => StatusCode(StatusCodes.Status502BadGateway,
            bodyExcerpt is null ? (object)new { code, message } : new { code, message, bodyExcerpt });
}

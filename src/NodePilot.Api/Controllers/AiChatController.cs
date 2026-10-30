using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Api.Ai;
using NodePilot.Ai;
using NodePilot.Api.Configuration;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Interfaces;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// AI workflow assistant: a multi-turn chat about the currently-open workflow that explains it
/// and, on request, proposes full definition rewrites. Deliberately <b>separate</b> from
/// <see cref="AiController"/>: <b>every</b> authenticated role can use it, including Viewers who
/// may only ask questions, while proposals are generated only for Admin/Operator. Persistence
/// never happens here — the frontend applies proposals through the normal edit-lock/publish flow.
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
    private const int MaxDiagnostics = 100;
    private const int MaxDiagnosticMessageChars = 500;
    private const int MaxDiagnosticCodeChars = 64;

    private static readonly HashSet<string> _allowedRoles = new(StringComparer.OrdinalIgnoreCase) { "user", "assistant" };
    private static readonly HashSet<string> _allowedSeverities = new(StringComparer.Ordinal) { "error", "warning" };

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
    /// Folder-RBAC gate for the workflow-scoped AI audit endpoints. Loads the workflow, then
    /// delegates to the shared gate
    /// (<see cref="ResourceAuthorizationGateExtensions.RequireWorkflowAccessAsync"/>): 404 when
    /// the workflow doesn't exist or the caller can't read it (no 403/404 differential to probe
    /// for valid IDs), 403 when they can read but can't perform the requested operation.
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
        if (LlmAvailability.Unavailable(this, _options.CurrentValue) is { } gate) return gate;

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

            // Folder-RBAC gate for the execution-log tools: workflowId comes from the client, so
            // without this check any authenticated user could pull step outputs from another
            // user's workflow through the assistant. A missing, unknown, or unsaved workflow just
            // disables the tools silently, and existence stays masked (no 404/403 differential).
            // Deliberately skips RequireWorkflowAccessAsync so this check never fails the chat
            // itself.
            var allowExecutionTools = false;
            var toolCallingEnabled = _options.CurrentValue.TryResolveActiveProfile(out var activeProfile)
                                     && activeProfile.EnableToolCalling;
            if (toolCallingEnabled && request.WorkflowId is { } wfId && wfId != Guid.Empty)
            {
                var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wfId, ct);
                allowExecutionTools = workflow is not null
                    && await _authz.CanAccessWorkflowAsync(User, workflow.FolderId, ResourceOp.Read, ct);
            }

            var normalized = request with
            {
                History = history,
                Diagnostics = NormalizeDiagnostics(request.Diagnostics, doc.RootElement),
            };

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
                return this.MapLlmException(_logger, ex, "LLM chat call");
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
            var scriptsShared = false;
            var historyDropped = false;

            async Task Write(ChatStreamEvent e)
            {
                // The closing totals must be captured before the write, so a client that
                // disconnects mid-flush still lands in the audit trail with what we knew.
                if (e is ChatStreamEvent.DoneEvent done)
                {
                    model = done.Model;
                    durationMs = done.DurationMs;
                    promptTokens = done.PromptTokens;
                    completionTokens = done.CompletionTokens;
                    scriptsShared = done.ScriptsShared;
                    historyDropped = done.HistoryDropped;

                }

                if (await AiStreamSupport.TryWriteSharedEventAsync(sse, e, onToolCall: null, ct))
                    return;

                switch (e)
                {
                    case ChatStreamEvent.BuildingEvent:
                        await sse.WriteAsync("building", new { }, ct);
                        break;
                    case ChatStreamEvent.ProposalEvent p:
                        proposed = true;
                        await sse.WriteAsync("proposal", p.Dto, ct);
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
                await AuditAsync(model, durationMs, proposed, cancelled: true, history.Count + 1, request.WorkflowId,
                    scriptsShared, historyDropped);
                return new EmptyResult();
            }

            RecordSuccess(model, durationMs, promptTokens, completionTokens);
            await AuditAsync(model, durationMs, proposed, cancelled: false, history.Count + 1, request.WorkflowId,
                scriptsShared, historyDropped, ct);
            return new EmptyResult();
        }
    }

    /// <summary>
    /// Records that an AI proposal was applied to the canvas, as audit event
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

        // Applying a proposal changes the workflow, so it requires Edit on the folder (404 if the
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
        // Reading activity requires reading the workflow, so it requires Read (404 if
        // unreadable/nonexistent).
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

    private const string LlmKind = "chat";

    private static void RecordResult(string result) =>
        AiStreamSupport.RecordResult(LlmKind, result);

    private void RecordError(LlmException ex)
    {
        AiStreamSupport.RecordError(LlmKind, ex);
        _logger.LogWarning(ex, "LLM chat stream failed: {Kind}", ex.Kind);
    }

    private static void RecordSuccess(string model, int durationMs, int? promptTokens, int? completionTokens) =>
        AiStreamSupport.RecordSuccess(LlmKind, model, durationMs, promptTokens, completionTokens);

    private Task AuditAsync(string model, int durationMs, bool proposed, bool cancelled, int turnCount,
        Guid? workflowId, bool scriptsShared, bool historyDropped, CancellationToken ct = default) =>
        _audit.LogAsync(AuditActions.AiWorkflowExplained, "Workflow", workflowId,
            AuditDetails.Json(
                ("model", model),
                ("durationMs", durationMs),
                ("modifyProposed", proposed),
                ("cancelled", cancelled),
                ("turnCount", turnCount),
                ("scriptsShared", scriptsShared),
                ("historyDropped", historyDropped)),
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

    /// <summary>
    /// Validates the designer findings the client sent along. They reach the LLM as prompt text,
    /// so only well-formed entries about elements of the submitted workflow are kept: known
    /// severity, a plain code, one-line messages of bounded length without inline secrets. The
    /// list is capped rather than rejected, since it is advisory context for the question.
    /// </summary>
    private static List<ChatDiagnosticDto> NormalizeDiagnostics(
        IReadOnlyList<ChatDiagnosticDto>? diagnostics, JsonElement workflow)
    {
        var result = new List<ChatDiagnosticDto>();
        if (diagnostics is null || diagnostics.Count == 0) return result;

        var nodeIds = CollectIds(workflow, "nodes");
        var edgeIds = CollectIds(workflow, "edges");
        foreach (var d in diagnostics)
        {
            if (result.Count >= MaxDiagnostics) break;
            if (d is null || d.Severity is null || d.Code is null || d.Message is null) continue;
            if (!_allowedSeverities.Contains(d.Severity)) continue;
            if (!IsPlainCode(d.Code)) continue;
            if (d.NodeId is not null && !nodeIds.Contains(d.NodeId)) continue;
            if (d.EdgeId is not null && !edgeIds.Contains(d.EdgeId)) continue;

            var message = d.Message.ReplaceLineEndings(" ").Trim();
            if (message.Length == 0 || WorkflowSecretContent.LooksSecret(message)) continue;
            if (message.Length > MaxDiagnosticMessageChars) message = message[..MaxDiagnosticMessageChars];

            result.Add(new ChatDiagnosticDto(d.Severity, d.Code, message, d.NodeId, d.EdgeId));
        }
        return result;
    }

    private static bool IsPlainCode(string code) =>
        code.Length is > 0 and <= MaxDiagnosticCodeChars
        && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static HashSet<string> CollectIds(JsonElement workflow, string arrayName)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (workflow.TryGetProperty(arrayName, out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { } value)
                {
                    ids.Add(value);
                }
            }
        }
        return ids;
    }

}

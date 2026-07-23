using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Ai.Knowledge;
using NodePilot.Api.Ai;
using NodePilot.Api.Audit;
using NodePilot.Api.Security;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Controllers;

/// <summary>
/// The global "AI Chat" knowledge assistant — a read-only Q&amp;A over NodePilot's documentation,
/// live operational/workflow data, and (when enabled) source code. Deliberately separate from the
/// workflow-scoped <see cref="AiChatController"/>: this endpoint needs no open canvas, proposes no
/// changes, and its available knowledge sources are governed by the admin-toggled
/// <see cref="AiKnowledgeOptions"/>. Open to every authenticated role; per-source RBAC (folder
/// scoping, Admin/Operator-only source-code and workflow-content tools) is enforced downstream.
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize]
[EnableRateLimiting("ai-generate")]
public sealed class AiKnowledgeController : ControllerBase
{
    private const int MaxQuestionChars = 8_000;
    private const int MaxHistoryTurns = 20;
    private const int MaxHistoryChars = 50_000;

    private static readonly HashSet<string> _allowedRoles = new(StringComparer.OrdinalIgnoreCase) { "user", "assistant" };

    private readonly IOptionsMonitor<LlmOptions> _llmOptions;
    private readonly IOptionsMonitor<AiKnowledgeOptions> _knowledgeOptions;
    private readonly KnowledgeAssistantService _assistant;
    private readonly IResourceAuthorizationService _authz;
    private readonly IAuditWriter _audit;
    private readonly ILogger<AiKnowledgeController> _logger;

    public AiKnowledgeController(
        IOptionsMonitor<LlmOptions> llmOptions,
        IOptionsMonitor<AiKnowledgeOptions> knowledgeOptions,
        KnowledgeAssistantService assistant,
        IResourceAuthorizationService authz,
        IAuditWriter audit,
        ILogger<AiKnowledgeController> logger)
    {
        _llmOptions = llmOptions;
        _knowledgeOptions = knowledgeOptions;
        _assistant = assistant;
        _authz = authz;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Effective capabilities for the current user — which knowledge sources the chat can draw from
    /// right now (both master switches on, per-source toggles, and the source-code role gate). Drives
    /// the sidebar nav visibility and the page's source badges. All roles.
    /// </summary>
    [HttpGet("knowledge/capabilities")]
    public ActionResult<KnowledgeCapabilitiesDto> Capabilities()
    {
        var k = _knowledgeOptions.CurrentValue;
        var enabled = _llmOptions.CurrentValue.Enabled && k.Enabled;
        return Ok(new KnowledgeCapabilitiesDto(
            Enabled: enabled,
            Docs: enabled && k.DocsEnabled,
            Operational: enabled && k.OperationalEnabled,
            SourceCode: enabled && k.SourceCodeEnabled && User.IsPrivileged(),
            Db: enabled && k.DbEnabled && User.IsPrivileged()));
    }

    /// <summary>Streams one knowledge-chat turn as Server-Sent Events (delta/tool_call/tool_result/done/error).</summary>
    [HttpPost("knowledge/ask")]
    public async Task<IActionResult> Ask(KnowledgeAskRequest request, CancellationToken ct)
    {
        if (!_llmOptions.CurrentValue.Enabled)
            return ServiceUnavailable("LLM_DISABLED", "AI ist deaktiviert. Setze Llm:Enabled=true in der Konfiguration.");
        var k = _knowledgeOptions.CurrentValue;
        if (!k.Enabled)
            return ServiceUnavailable("KNOWLEDGE_DISABLED", "Der KI-Chat ist deaktiviert. Aktiviere ihn in den Admin-Einstellungen (AI-Wissen).");

        if (request is null || string.IsNullOrWhiteSpace(request.Question))
            return BadRequest(new { code = "PROMPT_EMPTY", message = "Question must not be empty." });
        if (request.Question.Length > MaxQuestionChars)
            return BadRequest(new { code = "QUESTION_TOO_LONG", message = $"Question exceeds {MaxQuestionChars} characters." });

        var history = NormalizeHistory(request.History);
        if (history.Count > MaxHistoryTurns)
            return BadRequest(new { code = "HISTORY_TOO_LONG", message = $"History exceeds {MaxHistoryTurns} turns." });
        if (history.Sum(h => h.Content.Length) > MaxHistoryChars)
            return BadRequest(new { code = "HISTORY_TOO_LONG", message = $"History exceeds {MaxHistoryChars} characters." });

        var isPrivileged = User.IsPrivileged();
        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        var normalized = new KnowledgeAskRequest(request.Question, history, request.TimeZone, request.UtcOffsetMinutes);

        await using var en = _assistant
            .StreamAskAsync(normalized, accessible, isPrivileged, ct)
            .GetAsyncEnumerator(ct);

        // Peek the first event: an error before streaming starts comes back as a normal HTTP status.
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
            return new EmptyResult();
        }

        await using var sse = SseResponseWriter.Begin(Response);

        var model = "unknown";
        var durationMs = 0;
        var toolCalls = 0;
        int? promptTokens = null, completionTokens = null;

        async Task Write(ChatStreamEvent e)
        {
            switch (e)
            {
                case ChatStreamEvent.DeltaEvent d:
                    await sse.WriteAsync("delta", new { text = d.Text }, ct);
                    break;
                case ChatStreamEvent.ToolCallEvent tc:
                    toolCalls++;
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
                // BuildingEvent / ProposalEvent never occur on the knowledge stream.
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
            await AuditAsync(model, durationMs, toolCalls, history.Count + 1, k, isPrivileged, cancelled: true);
            return new EmptyResult();
        }

        RecordSuccess(model, durationMs, promptTokens, completionTokens);
        await AuditAsync(model, durationMs, toolCalls, history.Count + 1, k, isPrivileged, cancelled: false, ct);
        return new EmptyResult();
    }

    private Task AuditAsync(string model, int durationMs, int toolCalls, int turnCount,
        AiKnowledgeOptions k, bool isPrivileged, bool cancelled, CancellationToken ct = default) =>
        _audit.LogAsync(AuditActions.AiKnowledgeAsked, "AiKnowledge", null,
            AuditDetails.Json(
                ("model", model),
                ("durationMs", durationMs),
                ("toolCalls", toolCalls),
                ("turnCount", turnCount),
                ("cancelled", cancelled),
                ("docs", k.DocsEnabled),
                ("operational", k.OperationalEnabled),
                ("sourceCode", k.SourceCodeEnabled && isPrivileged),
                ("db", k.DbEnabled && isPrivileged)),
            ct);

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

    private static void RecordResult(string result) =>
        ApiMetrics.LlmCalls.Add(1, new(TelemetryConstants.Attributes.LlmKind, "knowledge"), new("result", result));

    private void RecordError(LlmException ex)
    {
        RecordResult("error");
        ApiMetrics.LlmErrors.Add(1,
            new(TelemetryConstants.Attributes.LlmKind, "knowledge"),
            new(TelemetryConstants.Attributes.LlmErrorKind, ex.Kind.ToString()));
        _logger.LogWarning(ex, "LLM knowledge stream failed: {Kind}", ex.Kind);
    }

    private static void RecordSuccess(string model, int durationMs, int? promptTokens, int? completionTokens)
    {
        RecordResult("success");
        ApiMetrics.LlmCallDuration.Record(durationMs,
            new(TelemetryConstants.Attributes.LlmKind, "knowledge"),
            new(TelemetryConstants.Attributes.LlmModel, model));
        if (promptTokens.HasValue)
            ApiMetrics.LlmTokens.Add(promptTokens.Value,
                new(TelemetryConstants.Attributes.LlmKind, "knowledge"),
                new(TelemetryConstants.Attributes.LlmModel, model), new("token_type", "prompt"));
        if (completionTokens.HasValue)
            ApiMetrics.LlmTokens.Add(completionTokens.Value,
                new(TelemetryConstants.Attributes.LlmKind, "knowledge"),
                new(TelemetryConstants.Attributes.LlmModel, model), new("token_type", "completion"));
    }

    private ActionResult MapLlmException(LlmException ex)
    {
        _logger.LogWarning(ex, "LLM knowledge call failed: {Kind}", ex.Kind);
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

/// <summary>Effective knowledge-chat capabilities for the current user (drives nav visibility + source badges).</summary>
public sealed record KnowledgeCapabilitiesDto(bool Enabled, bool Docs, bool Operational, bool SourceCode, bool Db);

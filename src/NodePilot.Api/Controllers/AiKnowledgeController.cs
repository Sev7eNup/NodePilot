using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodePilot.Ai;
using NodePilot.Api.Configuration;
using NodePilot.Ai.Knowledge;
using NodePilot.Api.Ai;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;

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
    /// the sidebar nav visibility and the page's source badges; the raw <c>llm</c> flag additionally
    /// gates the visibility of every AI entry point in the SPA. All roles.
    /// </summary>
    [HttpGet("knowledge/capabilities")]
    public ActionResult<KnowledgeCapabilitiesDto> Capabilities()
    {
        var k = _knowledgeOptions.CurrentValue;
        // "Usable" = kill-switch on AND an active profile resolves — without one every call would
        // 503 anyway, so reporting the sources as available would be a lie.
        var llm = _llmOptions.CurrentValue;
        var llmUsable = llm.IsUsable;
        var enabled = llmUsable && k.Enabled;
        var toolSourcesEnabled = enabled
                                 && llm.TryResolveActiveProfile(out var activeProfile)
                                 && activeProfile.EnableToolCalling;
        var scriptContextTargetHost = llmUsable && User.IsPrivileged()
                                      && llm.TryResolveActiveProfile(out var scriptProfile)
            ? DisplayHost(scriptProfile.BaseUrl)
            : null;
        return Ok(new KnowledgeCapabilitiesDto(
            Enabled: enabled,
            Llm: llmUsable,
            Docs: toolSourcesEnabled && k.DocsEnabled,
            Operational: toolSourcesEnabled && k.OperationalEnabled,
            SourceCode: toolSourcesEnabled && k.SourceCodeEnabled && User.IsPrivileged(),
            Db: toolSourcesEnabled && k.DbEnabled && User.IsAdmin(),
            ScriptContextTargetHost: scriptContextTargetHost));
    }

    private static string? DisplayHost(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        return uri.IsDefaultPort ? uri.IdnHost : $"{uri.IdnHost}:{uri.Port}";
    }

    /// <summary>Streams one knowledge-chat turn as Server-Sent Events (delta/tool_call/tool_result/done/error).</summary>
    [HttpPost("knowledge/ask")]
    public async Task<IActionResult> Ask(KnowledgeAskRequest request, CancellationToken ct)
    {
        if (LlmAvailability.Unavailable(this, _llmOptions.CurrentValue,
                "AI ist deaktiviert. Setze Llm:Enabled=true in der Konfiguration.") is { } gate) return gate;
        var k = _knowledgeOptions.CurrentValue;
        if (!k.Enabled)
            return this.LlmServiceUnavailable("KNOWLEDGE_DISABLED", "Der KI-Chat ist deaktiviert. Aktiviere ihn in den Admin-Einstellungen (AI-Wissen).");

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
        var isAdmin = User.IsAdmin();
        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        var normalized = new KnowledgeAskRequest(request.Question, history, request.TimeZone, request.UtcOffsetMinutes);

        await using var en = _assistant
            .StreamAskAsync(normalized, accessible, isPrivileged, isAdmin, ct)
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
            return this.MapLlmException(_logger, ex, "LLM knowledge call");
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
        var dbQueryFingerprints = new List<string>();
        int? promptTokens = null, completionTokens = null;

        // BuildingEvent / ProposalEvent never occur on the knowledge stream, so the shared
        // writer covers every event this endpoint can see.
        async Task Write(ChatStreamEvent e)
        {
            if (e is ChatStreamEvent.DoneEvent done)
            {
                model = done.Model;
                durationMs = done.DurationMs;
                promptTokens = done.PromptTokens;
                completionTokens = done.CompletionTokens;
            }

            await AiStreamSupport.TryWriteSharedEventAsync(sse, e, tc =>
            {
                toolCalls++;
                if (string.Equals(tc.ToolName, "execute_readonly_sql", StringComparison.Ordinal)
                    && TryFingerprintSqlToolCall(tc.ArgumentsJson) is { } fingerprint)
                    dbQueryFingerprints.Add(fingerprint);
            }, ct);
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
            await AuditAsync(model, durationMs, toolCalls, history.Count + 1, k, isPrivileged, isAdmin,
                dbQueryFingerprints, cancelled: true);
            return new EmptyResult();
        }

        RecordSuccess(model, durationMs, promptTokens, completionTokens);
        await AuditAsync(model, durationMs, toolCalls, history.Count + 1, k, isPrivileged, isAdmin,
            dbQueryFingerprints, cancelled: false, ct);
        return new EmptyResult();
    }

    private Task AuditAsync(string model, int durationMs, int toolCalls, int turnCount,
        AiKnowledgeOptions k, bool isPrivileged, bool isAdmin, IReadOnlyList<string> dbQueryFingerprints,
        bool cancelled, CancellationToken ct = default) =>
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
                ("db", k.DbEnabled && isAdmin),
                // Never audit SQL text: it may contain user-supplied literals. Stable fingerprints
                // still let operators correlate repeated/problematic queries without leaking data.
                ("dbQueryCount", dbQueryFingerprints.Count),
                ("dbQueryFingerprints", string.Join(",", dbQueryFingerprints))),
            ct);

    private static string? TryFingerprintSqlToolCall(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (!doc.RootElement.TryGetProperty("sql", out var sqlElement)
                || sqlElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(sqlElement.GetString()))
                return null;
            var sql = sqlElement.GetString()!;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))[..16];
            return $"{hash}:{sql.Length}";
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private const string LlmKind = "knowledge";

    private static void RecordResult(string result) =>
        AiStreamSupport.RecordResult(LlmKind, result);

    private void RecordError(LlmException ex)
    {
        AiStreamSupport.RecordError(LlmKind, ex);
        _logger.LogWarning(ex, "LLM knowledge stream failed: {Kind}", ex.Kind);
    }

    private static void RecordSuccess(string model, int durationMs, int? promptTokens, int? completionTokens) =>
        AiStreamSupport.RecordSuccess(LlmKind, model, durationMs, promptTokens, completionTokens);

}

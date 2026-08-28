using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using NodePilot.Core.Interfaces;

namespace NodePilot.Ai.Knowledge;

/// <summary>
/// Request for one turn of the global knowledge chat: a question plus prior user/assistant turns.
/// <paramref name="TimeZone"/> (caller's IANA zone) and <paramref name="UtcOffsetMinutes"/> (its
/// current UTC offset) come from the browser so the assistant can anchor "now" and show times in
/// the user's local zone. Both are optional; without them the time context is UTC-only.
/// </summary>
public sealed record KnowledgeAskRequest(
    string Question,
    IReadOnlyList<AiChatTurnDto>? History,
    string? TimeZone = null,
    int? UtcOffsetMinutes = null);

/// <summary>
/// Streams one turn of the global "AI Chat" knowledge assistant. Like
/// <see cref="WorkflowAssistantService"/> but canvas-free: no workflow JSON, no redact or merge,
/// no proposal, only read-only Q&amp;A over docs, operational data and source code through the
/// source-gated <see cref="IKnowledgeToolRegistry"/>. The bounded tool loop is the shared
/// <see cref="AgenticToolLoop"/>; the active LLM profile and <c>AiKnowledge:*</c> are read live
/// via <see cref="IOptionsMonitor{T}"/>. Emits <c>Delta</c> / <c>ToolCall</c> / <c>ToolResult</c>
/// and a closing <c>Done</c>.
///
/// <para>Takes <see cref="ILlmClientFactory"/> rather than a pre-built client: resolving the
/// active profile can fail and must surface as the controller's 503, not as a DI error.</para>
/// </summary>
public sealed class KnowledgeAssistantService(
    ILlmClientFactory llmFactory,
    PromptCatalog prompts,
    IKnowledgeToolRegistry tools,
    IOptionsMonitor<LlmOptions> llmOptions,
    IOptionsMonitor<AiKnowledgeOptions> knowledgeOptions,
    IOperationalKnowledgeReader operational,
    ISettingsKnowledgeReader settings,
    ISqlKnowledgeReader sql)
{
    private static readonly string[] AllowedRoles = { "user", "assistant" };

    /// <summary>
    /// Streams one chat turn. <paramref name="accessible"/> is the caller's pre-resolved folder
    /// access (the reader never sees a <c>ClaimsPrincipal</c>); <paramref name="isPrivileged"/> is
    /// Admin or Operator and gates the workflow-content and source-code tools, while
    /// <paramref name="isAdmin"/> alone gates the raw database tools.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamAskAsync(
        KnowledgeAskRequest request, AccessibleFolderSet accessible, bool isPrivileged, bool isAdmin,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var systemPrompt = prompts.KnowledgeSystemPrompt
            + "\n\n"
            + KnowledgeTimeContext.Build(DateTimeOffset.UtcNow, request.TimeZone, request.UtcOffsetMinutes);
        var conversation = new List<LlmMessage>(BuildConversation(request));

        var llm = llmFactory.Create(); // throws unless an active profile resolves
        // Re-read instead of reusing: a config reload between the two calls can leave no active
        // profile. A fresh LlmProfileOptions has tool calling off, which is the safe fallback.
        var profile = llmOptions.CurrentValue.TryResolveActiveProfile(out var active)
            ? active
            : new LlmProfileOptions();
        var kOpts = knowledgeOptions.CurrentValue;
        var maxDepth = Math.Max(1, profile.ToolCallMaxDepth);

        IReadOnlyList<LlmToolDefinition>? toolDefs = null;
        KnowledgeToolContext? toolContext = null;
        if (profile.EnableToolCalling)
        {
            // The operational reader only enters the context when operational data is enabled, so
            // its tools are otherwise neither offered nor executable. The settings reader is only
            // present for privileged callers, because read_settings is gated to them.
            var operationalReader = kOpts.OperationalEnabled ? operational : null;
            var settingsReader = isPrivileged ? settings : null;
            // Raw SQL is a global-Admin capability. Folder grants never elevate an Operator into
            // this source; the registry independently repeats the gate before every tool call.
            var sqlReader = (kOpts.DbEnabled && isAdmin) ? sql : null;
            toolContext = new KnowledgeToolContext(
                accessible, isPrivileged, isAdmin, kOpts.DocsEnabled, kOpts.OperationalEnabled,
                kOpts.SourceCodeEnabled, kOpts.DbEnabled, operationalReader, settingsReader, sqlReader);
            toolDefs = tools.GetTools(toolContext);
            if (toolDefs.Count == 0)
            {
                toolDefs = null;
                toolContext = null;
            }
        }

        // The bounded tool-loop mechanics live in AgenticToolLoop, shared with the workflow
        // assistant; this caller only maps each delta to a stream event.
        var loop = new AgenticToolLoop();
        await foreach (var evt in loop.RunAsync(
            llm, systemPrompt, conversation, toolDefs, maxDepth,
            delta => new[] { ChatStreamEvent.Delta(delta) },
            (call, token) => tools.ExecuteAsync(call.Name, call.ArgumentsJson, toolContext!, token),
            suppressToolCalls: null,
            ct))
        {
            yield return evt;
        }

        sw.Stop();
        yield return ChatStreamEvent.Done(loop.Model ?? "unknown", (int)sw.ElapsedMilliseconds,
            loop.PromptTokens, loop.CompletionTokens, loop.GenerationMs);
    }

    private static IReadOnlyList<LlmMessage> BuildConversation(KnowledgeAskRequest request)
    {
        var turns = new List<LlmMessage>();
        if (request.History is not null)
        {
            foreach (var h in request.History)
            {
                if (h is null || string.IsNullOrWhiteSpace(h.Content) || h.Role is null) continue;
                if (!AllowedRoles.Contains(h.Role, StringComparer.OrdinalIgnoreCase)) continue;
                turns.Add(new LlmMessage(h.Role.ToLowerInvariant(), h.Content));
            }
        }
        turns.Add(new LlmMessage("user", request.Question));
        return turns;
    }
}

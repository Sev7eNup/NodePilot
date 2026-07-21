using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Options;
using NodePilot.Core.Interfaces;

namespace NodePilot.Ai.Knowledge;

/// <summary>
/// Request for one turn of the global knowledge chat: a question plus prior user/assistant turns.
/// <paramref name="TimeZone"/> (caller's IANA zone) and <paramref name="UtcOffsetMinutes"/> (its
/// current UTC offset) are supplied by the browser so the assistant can anchor "now" and present
/// times in the user's local zone; both are optional (non-browser callers → UTC-only context).
/// </summary>
public sealed record KnowledgeAskRequest(
    string Question,
    IReadOnlyList<AiChatTurnDto>? History,
    string? TimeZone = null,
    int? UtcOffsetMinutes = null);

/// <summary>
/// Streams one turn of the global "AI Chat" knowledge assistant. Parallel to
/// <see cref="WorkflowAssistantService"/> but <b>canvas-free</b>: no workflow JSON, no
/// redact/merge, no proposal — a read-only Q&amp;A over docs / operational data / source code via the
/// source-gated <see cref="IKnowledgeToolRegistry"/>. Reuses the same bounded tool-loop mechanics
/// (final round offers no tools; reads <c>Llm:*</c> and <c>AiKnowledge:*</c> live via
/// <see cref="IOptionsMonitor{T}"/>). Emits <c>Delta</c> / <c>ToolCall</c> / <c>ToolResult</c> and a
/// closing <c>Done</c>.
/// </summary>
public sealed class KnowledgeAssistantService(
    ILlmClient llm,
    PromptCatalog prompts,
    IKnowledgeToolRegistry tools,
    IOptionsMonitor<LlmOptions> llmOptions,
    IOptionsMonitor<AiKnowledgeOptions> knowledgeOptions,
    IOperationalKnowledgeReader operational,
    ISettingsKnowledgeReader settings)
{
    private static readonly string[] AllowedRoles = { "user", "assistant" };

    /// <summary>
    /// Streams one chat turn. <paramref name="accessible"/> is the caller's pre-resolved folder
    /// access (the reader never sees a <c>ClaimsPrincipal</c>); <paramref name="isPrivileged"/> is
    /// Admin/Operator — it gates the workflow-content and source-code tools.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> StreamAskAsync(
        KnowledgeAskRequest request, AccessibleFolderSet accessible, bool isPrivileged,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var systemPrompt = prompts.KnowledgeSystemPrompt
            + "\n\n"
            + KnowledgeTimeContext.Build(DateTimeOffset.UtcNow, request.TimeZone, request.UtcOffsetMinutes);
        var conversation = new List<LlmMessage>(BuildConversation(request));

        var llmOpts = llmOptions.CurrentValue;
        var kOpts = knowledgeOptions.CurrentValue;
        var maxDepth = Math.Max(1, llmOpts.ToolCallMaxDepth);

        IReadOnlyList<LlmToolDefinition>? toolDefs = null;
        KnowledgeToolContext? toolContext = null;
        if (llmOpts.EnableToolCalling)
        {
            // The operational reader only goes into the context when operational data is enabled —
            // otherwise its tools are neither offered nor executable. The settings reader is present
            // only for privileged callers (Admin/Operator) — read_settings is gated to them.
            var operationalReader = kOpts.OperationalEnabled ? operational : null;
            var settingsReader = isPrivileged ? settings : null;
            toolContext = new KnowledgeToolContext(
                accessible, isPrivileged, kOpts.DocsEnabled, kOpts.OperationalEnabled, kOpts.SourceCodeEnabled, operationalReader, settingsReader);
            toolDefs = tools.GetTools(toolContext);
            if (toolDefs.Count == 0)
            {
                toolDefs = null;
                toolContext = null;
            }
        }

        string? model = null;
        int? promptTokens = null, completionTokens = null;

        for (var iteration = 0; ; iteration++)
        {
            // On the LAST allowed round, offer no tools — guarantees a text answer instead of yet
            // more tool_calls at the depth cap. Deliberately NOT tool_choice:"none" (some local
            // endpoints reject it); the tool results are already in the conversation history.
            var isFinalRound = iteration >= maxDepth - 1;
            var roundTools = isFinalRound ? null : toolDefs;
            var llmRequest = new LlmRequest(systemPrompt, UserPrompt: string.Empty, JsonMode: false,
                Conversation: conversation, Tools: roundTools, ToolChoice: roundTools is not null ? "auto" : null);

            var assistantText = new StringBuilder();
            IReadOnlyList<LlmToolCall>? toolCalls = null;

            await foreach (var evt in llm.StreamAsync(llmRequest, ct))
            {
                if (evt.Done)
                {
                    model = evt.Model;
                    if (evt.PromptTokens is int pt) promptTokens = (promptTokens ?? 0) + pt;
                    if (evt.CompletionTokens is int cpt) completionTokens = (completionTokens ?? 0) + cpt;
                    toolCalls = evt.ToolCalls;
                    break;
                }
                if (evt.ContentDelta is { Length: > 0 } delta)
                {
                    assistantText.Append(delta);
                    yield return ChatStreamEvent.Delta(delta);
                }
            }

            // Execute whenever the model emitted tool calls — their presence is authoritative, not the
            // finish_reason string. OpenAI sets finish_reason "tool_calls", but local endpoints (LM Studio,
            // llama.cpp) frequently report "stop"/null on a round that still carries tool_calls; gating on
            // the exact string would silently drop those calls and cap local models at a single tool call.
            var canCallTools = toolContext is not null && toolCalls is { Count: > 0 } && iteration < maxDepth - 1;
            if (canCallTools)
            {
                conversation.Add(new LlmMessage("assistant", assistantText.ToString(), ToolCalls: toolCalls));
                foreach (var call in toolCalls!)
                {
                    yield return ChatStreamEvent.ToolCall(call.Name, call.Id, call.ArgumentsJson);
                    var result = await tools.ExecuteAsync(call.Name, call.ArgumentsJson, toolContext!, ct);
                    yield return ChatStreamEvent.ToolResult(call.Id, call.Name, result);
                    conversation.Add(new LlmMessage("tool", result, ToolCallId: call.Id));
                }
                continue; // next LLM round, now with the tool results
            }
            break; // final answer (or the depth cap was reached)
        }

        sw.Stop();
        yield return ChatStreamEvent.Done(model ?? "unknown", (int)sw.ElapsedMilliseconds, promptTokens, completionTokens);
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

using System.Runtime.CompilerServices;
using System.Text;

namespace NodePilot.Ai;

/// <summary>
/// The ONE bounded tool-calling loop behind both chat assistants
/// (<see cref="WorkflowAssistantService"/> and <c>Knowledge.KnowledgeAssistantService</c>) —
/// previously each carried a near-verbatim private copy, so every quirk fix had to be made
/// twice (coherence audit 2026-08).
///
/// <para>Loop contract, kept exactly as both copies implemented it:</para>
/// <list type="bullet">
/// <item>On the LAST allowed round, offer no tools at all: this guarantees the model returns
/// a text answer instead of emitting yet more tool_calls at the depth cap (which the loop
/// would then discard → an empty final answer). Deliberately NOT <c>tool_choice:"none"</c> —
/// some local endpoints (llama.cpp/vLLM) reject that literal with HTTP 400; omitting tools
/// entirely avoids the problem. The tool results are already in the conversation history.</item>
/// <item>Execute on the PRESENCE of tool_calls, not the finish_reason string: OpenAI sets
/// finish_reason "tool_calls", but local endpoints (LM Studio, llama.cpp) frequently report
/// "stop"/null on a round that still carries tool_calls — an exact-string gate silently
/// dropped those calls and capped local models at a single tool call.</item>
/// <item>Token counts and the generation window ADD UP across rounds (never overwrite) —
/// otherwise the usage footer would only count the last LLM round. They stay null when the
/// server never reports usage.</item>
/// </list>
/// </summary>
internal sealed class AgenticToolLoop
{
    /// <summary>Usage totals, readable after the stream completes (for the Done event).</summary>
    public string? Model { get; private set; }
    public int? PromptTokens { get; private set; }
    public int? CompletionTokens { get; private set; }
    public int? GenerationMs { get; private set; }

    /// <summary>
    /// Runs the loop. <paramref name="onDelta"/> is the caller's per-delta translation into
    /// outward stream events (the plain chat emits one Delta; the workflow chat buffers
    /// prose/definition and may emit Delta/Building) — the assistant-turn accumulation for
    /// the conversation history happens here regardless. <paramref name="suppressToolCalls"/>
    /// is an extra per-round veto evaluated after streaming (the workflow chat drops
    /// tool_calls once the definition started — the definition wins, no further round trip).
    /// <paramref name="executeTool"/> is only invoked when <paramref name="tools"/> was
    /// non-null, so callers may capture a context that is null in the no-tools case.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> RunAsync(
        ILlmClient llm,
        string systemPrompt,
        List<LlmMessage> conversation,
        IReadOnlyList<LlmToolDefinition>? tools,
        int maxDepth,
        Func<string, IEnumerable<ChatStreamEvent>> onDelta,
        Func<LlmToolCall, CancellationToken, Task<string>> executeTool,
        Func<bool>? suppressToolCalls = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var iteration = 0; ; iteration++)
        {
            var isFinalRound = iteration >= maxDepth - 1;
            var roundTools = isFinalRound ? null : tools;
            var llmRequest = new LlmRequest(systemPrompt, UserPrompt: string.Empty, JsonMode: false,
                Conversation: conversation, Tools: roundTools, ToolChoice: roundTools is not null ? "auto" : null);

            var assistantText = new StringBuilder(); // prose from THIS round (for the conversation turn)
            IReadOnlyList<LlmToolCall>? toolCalls = null;

            await foreach (var evt in llm.StreamAsync(llmRequest, ct))
            {
                if (evt.Done)
                {
                    Model = evt.Model;
                    if (evt.PromptTokens is int pt) PromptTokens = (PromptTokens ?? 0) + pt;
                    if (evt.CompletionTokens is int cpt) CompletionTokens = (CompletionTokens ?? 0) + cpt;
                    if (evt.GenerationMs is int gm) GenerationMs = (GenerationMs ?? 0) + gm;
                    toolCalls = evt.ToolCalls;
                    break;
                }
                if (evt.ContentDelta is not { Length: > 0 } delta) continue;
                assistantText.Append(delta);
                foreach (var outward in onDelta(delta))
                    yield return outward;
            }

            var canCallTools = tools is not null
                && toolCalls is { Count: > 0 }
                && iteration < maxDepth - 1
                && !(suppressToolCalls?.Invoke() ?? false);
            if (!canCallTools)
                yield break; // final answer (or the depth cap was reached)

            // Play the tool results back as tool-role turns, then stream again.
            conversation.Add(new LlmMessage("assistant", assistantText.ToString(), ToolCalls: toolCalls));
            foreach (var call in toolCalls!)
            {
                yield return ChatStreamEvent.ToolCall(call.Name, call.Id, call.ArgumentsJson);
                var result = await executeTool(call, ct);
                yield return ChatStreamEvent.ToolResult(call.Id, call.Name, result);
                conversation.Add(new LlmMessage("tool", result, ToolCallId: call.Id));
            }
        }
    }
}

using System.Runtime.CompilerServices;
using System.Text;

namespace NodePilot.Ai;

/// <summary>
/// The bounded tool-calling loop shared by both chat assistants
/// (<see cref="WorkflowAssistantService"/> and <c>Knowledge.KnowledgeAssistantService</c>).
///
/// <para>Loop contract:</para>
/// <list type="bullet">
/// <item>On the last allowed round, offer no tools at all, so the model returns a text answer
/// instead of emitting tool_calls that the loop would have to discard. Tools are omitted rather
/// than sending <c>tool_choice:"none"</c>, which some local endpoints (llama.cpp/vLLM) reject
/// with HTTP 400. The tool results are already in the conversation history.</item>
/// <item>Execute on the presence of tool_calls, not on the finish_reason string: OpenAI sets
/// finish_reason "tool_calls", but local endpoints (LM Studio, llama.cpp) often report
/// "stop"/null on a round that still carries tool_calls.</item>
/// <item>Token counts and the generation window add up across rounds instead of overwriting, so
/// the usage footer covers every LLM round. They stay null when the server reports no usage.</item>
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
    /// Runs the loop. <paramref name="onDelta"/> turns each delta into outward stream events; the
    /// assistant turn is accumulated for the conversation history either way.
    /// <paramref name="suppressToolCalls"/> is a per-round veto evaluated after streaming, used by
    /// the workflow chat once the definition started. <paramref name="executeTool"/> is invoked
    /// only when <paramref name="tools"/> is non-null, so a null tool context is safe for callers.
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

            var assistantText = new StringBuilder(); // prose from this round, for the conversation turn
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

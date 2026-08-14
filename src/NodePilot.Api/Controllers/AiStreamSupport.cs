using NodePilot.Ai;
using NodePilot.Api.Ai;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Shared plumbing for the AI endpoints (<see cref="AiController"/>, <see cref="AiChatController"/>,
/// <see cref="AiKnowledgeController"/>): the LLM telemetry trio and the SSE events their streams
/// have in common. Everything that differs stays with the caller — the metric <c>kind</c> tag is a
/// parameter, the per-endpoint log line stays at the call site, and the chat-only
/// building/proposal events are handled there too.
/// </summary>
internal static class AiStreamSupport
{
    /// <summary>Counts one LLM call outcome. Metric name, tag names and tag order are dashboard contract.</summary>
    public static void RecordResult(string kind, string result) =>
        ApiMetrics.LlmCalls.Add(1, new(TelemetryConstants.Attributes.LlmKind, kind), new("result", result));

    /// <summary>Counts a failed call: the <c>error</c> result first, then the error-kind breakdown.</summary>
    public static void RecordError(string kind, LlmException ex)
    {
        RecordResult(kind, "error");
        ApiMetrics.LlmErrors.Add(1,
            new(TelemetryConstants.Attributes.LlmKind, kind),
            new(TelemetryConstants.Attributes.LlmErrorKind, ex.Kind.ToString()));
    }

    /// <summary>Counts a successful call plus its latency and (when reported) its token usage.</summary>
    public static void RecordSuccess(string kind, string model, int durationMs, int? promptTokens, int? completionTokens)
    {
        RecordResult(kind, "success");
        ApiMetrics.LlmCallDuration.Record(durationMs,
            new(TelemetryConstants.Attributes.LlmKind, kind),
            new(TelemetryConstants.Attributes.LlmModel, model));
        if (promptTokens.HasValue)
            ApiMetrics.LlmTokens.Add(promptTokens.Value,
                new(TelemetryConstants.Attributes.LlmKind, kind),
                new(TelemetryConstants.Attributes.LlmModel, model), new("token_type", "prompt"));
        if (completionTokens.HasValue)
            ApiMetrics.LlmTokens.Add(completionTokens.Value,
                new(TelemetryConstants.Attributes.LlmKind, kind),
                new(TelemetryConstants.Attributes.LlmModel, model), new("token_type", "completion"));
    }

    /// <summary>
    /// Writes the chat-stream events that the workflow assistant and the knowledge assistant emit
    /// identically (<c>delta</c>/<c>tool_call</c>/<c>tool_result</c>/<c>done</c>) and reports
    /// <c>false</c> for everything else, so a caller with extra events (building/proposal) can
    /// handle those itself. <paramref name="onToolCall"/> runs before the <c>tool_call</c> event is
    /// written — the knowledge stream counts calls and fingerprints SQL there.
    /// </summary>
    public static async Task<bool> TryWriteSharedEventAsync(
        SseResponseWriter sse,
        ChatStreamEvent e,
        Action<ChatStreamEvent.ToolCallEvent>? onToolCall,
        CancellationToken ct)
    {
        switch (e)
        {
            case ChatStreamEvent.DeltaEvent d:
                await sse.WriteAsync("delta", new { text = d.Text }, ct);
                return true;
            case ChatStreamEvent.ToolCallEvent tc:
                onToolCall?.Invoke(tc);
                await sse.WriteAsync("tool_call", new { toolName = tc.ToolName, toolId = tc.ToolId }, ct);
                return true;
            case ChatStreamEvent.ToolResultEvent tr:
                await sse.WriteAsync("tool_result", new { toolId = tr.ToolId, toolName = tr.ToolName }, ct);
                return true;
            case ChatStreamEvent.DoneEvent done:
                await sse.WriteAsync("done", new { model = done.Model, durationMs = done.DurationMs, generationMs = done.GenerationMs, promptTokens = done.PromptTokens, completionTokens = done.CompletionTokens }, ct);
                return true;
            default:
                return false;
        }
    }
}

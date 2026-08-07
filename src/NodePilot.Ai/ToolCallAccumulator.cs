using System.Text;

namespace NodePilot.Ai;

/// <summary>
/// Accumulates one streamed tool call (id/name arrive once, arguments arrive as string
/// fragments) across SSE chunks, keyed by wire index. Dialect-agnostic — shared by both
/// LLM clients, which previously carried byte-identical private copies (coherence audit
/// 2026-08).
/// </summary>
internal sealed class ToolCallAccumulator
{
    public string Id = "";
    public string Name = "";
    public StringBuilder Arguments { get; } = new();

    /// <summary>
    /// Orders by wire index and drops nameless slots (argument fragments whose header
    /// chunk never arrived). Returns null when nothing usable accumulated, so callers can
    /// pass the result straight into <c>LlmStreamEvent.ToolCalls</c>.
    /// </summary>
    public static IReadOnlyList<LlmToolCall>? Materialize(Dictionary<int, ToolCallAccumulator> acc)
    {
        if (acc.Count == 0) return null;
        var calls = acc.OrderBy(kv => kv.Key)
            .Select(kv => new LlmToolCall(kv.Value.Id, kv.Value.Name, kv.Value.Arguments.ToString()))
            .Where(t => t.Name.Length > 0).ToList();
        return calls.Count > 0 ? calls : null;
    }
}

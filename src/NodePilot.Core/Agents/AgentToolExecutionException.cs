using System.Text.Json;

namespace NodePilot.Core.Agents;

public sealed class AgentToolExecutionException(string code, string message, JsonElement? details = null)
    : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
    public JsonElement? Details { get; } = details?.Clone();
}

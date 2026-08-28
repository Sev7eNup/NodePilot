using System.Text.Json;

namespace NodePilot.Ai;

/// <summary>
/// Guarded reads over an LLM response element, shared by the two wire dialects
/// (<see cref="OpenAiCompatibleLlmClient"/>, <see cref="OpenAiResponsesLlmClient"/>). A missing
/// property, a <c>null</c>, or a value of the wrong kind reads as <c>null</c> rather than
/// throwing, because upstream payloads may not match the documented shape.
/// </summary>
internal static class LlmJson
{
    public static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    public static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : null;
}

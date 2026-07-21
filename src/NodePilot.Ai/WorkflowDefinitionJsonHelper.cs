using System.Text.Json;

namespace NodePilot.Ai;

/// <summary>
/// Tolerant extraction of a JSON object from a raw LLM response. Shared between
/// <see cref="WorkflowGenerationService"/> (workflow generation) and
/// <see cref="WorkflowAssistantService"/> (chat assistant) — both need to fish the actual JSON
/// object out of a response that may be decorated with code fences and prose.
/// </summary>
internal static class WorkflowDefinitionJsonHelper
{
    /// <summary>
    /// Strips leading/trailing prose and markdown fences and looks for the first balanced
    /// <c>{...}</c> block that also parses as valid JSON. Tolerant of LLMs that mention example
    /// snippets like <c>{key: value}</c> in a preamble before the actual response JSON follows.
    /// Returns <c>null</c> when none is found.
    /// </summary>
    internal static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var pos = 0;
        while (pos < raw.Length)
        {
            var start = raw.IndexOf('{', pos);
            if (start < 0) return null;

            var end = FindBalancedClose(raw, start);
            if (end < 0) return null;

            var candidate = raw[start..(end + 1)];
            try
            {
                using var _ = JsonDocument.Parse(candidate);
                return candidate;
            }
            catch (JsonException)
            {
                // Not real JSON (e.g. "{ key: value }" with unquoted keys) — skip it and keep
                // looking from the next '{'.
                pos = start + 1;
            }
        }
        return null;
    }

    private static int FindBalancedClose(string raw, int start)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < raw.Length; i++)
        {
            var ch = raw[i];
            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"') { inString = false; }
                continue;
            }
            if (ch == '"') { inString = true; continue; }
            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }
}

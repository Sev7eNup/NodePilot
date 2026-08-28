using System.Text.Json;

namespace NodePilot.Ai;

/// <summary>
/// Tolerant extraction of a JSON object from a raw LLM response. Shared by
/// <see cref="WorkflowGenerationService"/> and <see cref="WorkflowAssistantService"/>, which both
/// have to pull the JSON object out of a response decorated with code fences and prose.
/// </summary>
internal static class WorkflowDefinitionJsonHelper
{
    /// <summary>
    /// Skips surrounding prose and markdown fences and returns the first balanced <c>{...}</c>
    /// block that parses as valid JSON, so a preamble mentioning a snippet like
    /// <c>{key: value}</c> is ignored. Returns <c>null</c> when no such block exists.
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
                // Not real JSON, for example "{ key: value }" with unquoted keys. Skip it and keep
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

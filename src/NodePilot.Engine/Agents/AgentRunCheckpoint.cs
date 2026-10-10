using System.Text.Json;
using NodePilot.Core.Agents;

namespace NodePilot.Engine.Agents;

internal sealed class AgentRunCheckpoint
{
    private readonly Dictionary<string, string> _sections = new(StringComparer.Ordinal);
    private string? _draft;
    public string? Update(AgentProgress entry)
    {
        string key;
        string content;
        if (entry.Kind == "report_draft") { _draft = entry.Content; return _draft; }
        if (entry.Kind is "member_completed" or "member_needs_input" or "member_failed")
        {
            try
            {
                using var document = JsonDocument.Parse(entry.Content);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("content", out var finding) || finding.ValueKind != JsonValueKind.String) return null;
                content = entry.Kind + ": " + (finding.GetString() ?? "");
                key = "Member " + entry.MemberId;
            }
            catch (JsonException) { return null; }
        }
        else if (entry.Kind == "investigation_updated")
        {
            try
            {
                using var document = JsonDocument.Parse(entry.Content);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("check", out var check) || check.ValueKind != JsonValueKind.Object
                    || !check.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return null;
                key = "Check " + id.GetString();
                content = entry.Content;
            }
            catch (JsonException) { return null; }
        }
        else if (entry.Kind is "tool_completed" or "tool_failed" or "progress_stopped")
        {
            key = entry.Kind + " / " + entry.MemberId;
            content = entry.Content;
        }
        else return null;
        _sections.Remove(key);
        _sections[key] = content.Length > 3000 ? content[..3000] + "\n[Checkpoint excerpt; see event journal for complete evidence.]" : content;
        return "INTERMEDIATE FINDINGS — not a final assessment. Claims and proposed actions remain unverified unless supported by cited evidence.\n"
            + (_draft is null ? "" : "Latest draft (may be superseded by subsequent findings):\n" + _draft[..Math.Min(_draft.Length, 8000)] + "\n")
            + (_draft?.Length > 8000 ? "[Draft excerpt; see report_draft event for the retained draft.]\n" : "")
            + (_sections.Count > 14 ? $"{_sections.Count - 14} older sections omitted; complete history remains in the event journal.\n" : "")
            + string.Join("\n\n", _sections.TakeLast(14).Select(s => s.Key + "\n" + s.Value));
    }
}

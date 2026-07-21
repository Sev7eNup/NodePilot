using System.Text.Json;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Read-only, secret-redacted view of the effective admin configuration for the global "AI Chat"
/// knowledge assistant. Mirrors what <c>GET /api/admin/settings</c> returns to the admin UI — every
/// section's values with secrets already masked to <c>"********"</c> — so the assistant can answer
/// questions about <b>configured</b> values (runspace pre-allocation, retention windows, log format,
/// WinRM timeouts, auth modes …) without the model guessing. The raw config file itself is never
/// exposed (it is deliberately blocked from the source-code source), so this redacted snapshot is the
/// only sanctioned path to configuration facts. Restricted to Admin/Operator at the tool layer.
/// </summary>
public interface ISettingsKnowledgeReader
{
    /// <summary>All admin settings sections with their secret-redacted current values.</summary>
    IReadOnlyList<SettingsSectionKnowledge> GetRedactedSnapshot();
}

/// <summary>One admin settings section: its config path, display name, hot-reload flag, and the
/// secret-redacted current values as a JSON object.</summary>
public sealed record SettingsSectionKnowledge(
    string Section,
    string DisplayName,
    bool HotReloadable,
    JsonElement Values);

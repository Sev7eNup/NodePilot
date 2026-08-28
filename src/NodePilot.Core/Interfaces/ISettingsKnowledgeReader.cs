using System.Text.Json;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Read-only, secret-redacted view of the effective admin configuration for the global AI Chat
/// knowledge assistant. Mirrors what <c>GET /api/admin/settings</c> returns to the admin UI, with
/// secrets masked to <c>"********"</c>, so the assistant can answer questions about configured
/// values (retention windows, log format, WinRM timeouts, auth modes) instead of guessing. The raw
/// config file is never exposed, so this snapshot is the only path to configuration facts.
/// Restricted to Admin and Operator at the tool layer.
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

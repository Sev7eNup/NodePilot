namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// Returned by <c>GET /api/admin/settings/status</c>. Drives the orange
/// "service restart required" banner in the Frontend SystemSettingsPage.
/// </summary>
/// <param name="OverridesPath">Absolute filesystem path to the active runtime-overrides file.</param>
/// <param name="RestartRequired">True when at least one section has been saved that is not yet active.</param>
/// <param name="RestartRequiredSince">UTC instant of the oldest unactivated save. Null when nothing is pending.</param>
/// <param name="RestartRequiredFor">List of section paths (e.g. <c>"Smtp"</c>, <c>"Authentication:Ldap"</c>) waiting on a restart.</param>
/// <param name="LastSavedAt">UTC instant of the most recent save through the Settings API.</param>
/// <param name="LastSavedBy">Username of the most recent saver. Null on a fresh install.</param>
public sealed record SettingsStatusResponse(
    string OverridesPath,
    bool RestartRequired,
    DateTimeOffset? RestartRequiredSince,
    IReadOnlyList<string> RestartRequiredFor,
    DateTimeOffset? LastSavedAt,
    string? LastSavedBy);

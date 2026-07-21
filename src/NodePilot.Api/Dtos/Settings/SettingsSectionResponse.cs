namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// Wrapper returned by <c>GET /api/admin/settings/{section}</c>. Carries the typed
/// section payload alongside the ETag (for optimistic concurrency on the next PUT),
/// the per-field <c>effectiveSource</c> map (so the UI can surface env-overridden
/// fields as read-only), and the per-section hot-reload flag (so the UI can decide
/// whether to show the orange restart banner after a save).
/// </summary>
/// <typeparam name="TPayload">Section DTO (e.g. <see cref="SmtpSettingsDto"/>).</typeparam>
public sealed class SettingsSectionResponse<TPayload>
{
    public required string SectionPath { get; init; }
    public required TPayload Payload { get; init; }
    public required string Etag { get; init; }
    public required bool IsHotReloadable { get; init; }

    /// <summary>
    /// Per-leaf-key configuration source — e.g. <c>{"Host": "runtime", "Password": "env"}</c>.
    /// The Frontend renders any field with <c>"env"</c> or <c>"cli"</c> as read-only with
    /// a tooltip explaining why a UI save won't take effect.
    /// </summary>
    public required IReadOnlyDictionary<string, string> EffectiveSource { get; init; }
}

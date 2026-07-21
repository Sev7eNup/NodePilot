using System.ComponentModel.DataAnnotations;

namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// SMTP section DTO for the Admin Settings API. Mirrors
/// <see cref="NodePilot.Engine.Options.SmtpOptions"/> with two API-specific tweaks:
/// <list type="bullet">
///   <item><c>Password</c> is sent back as <c>"********"</c> on read; on write the
///   client may either supply a new plaintext (encrypted server-side and persisted
///   to the runtime override file) or the unchanged-secret sentinel
///   to keep the existing encrypted value untouched. The sentinel constant lives in
///   <c>NodePilot.Api.Configuration.SettingsSchema.UnchangedSecretSentinel</c>.</item>
///   <item>DataAnnotations cover the obvious bounds (port range, non-empty host) so
///   the controller can fail-fast with 400 before invoking the boot-validator pipeline.</item>
/// </list>
/// </summary>
public sealed class SmtpSettingsDto
{
    [Required(AllowEmptyStrings = false)]
    [StringLength(255)]
    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = 25;

    [StringLength(255)]
    public string? Username { get; set; }

    /// <summary>
    /// Read response: always <c>"********"</c> (masked) when a value is present,
    /// <c>null</c> when no password is configured. Write request: new plaintext to
    /// rotate, the literal <c>"__unchanged__"</c> sentinel to keep the current value,
    /// or <c>null</c>/empty-string to clear the password entirely.
    /// </summary>
    public string? Password { get; set; }

    [Required(AllowEmptyStrings = false)]
    [EmailAddress]
    [StringLength(320)]
    public string From { get; set; } = "";

    /// <summary>
    /// H-2 (security audit 2026-05-15): enables explicit TLS / STARTTLS on the SMTP
    /// connection. Default <c>true</c>. The Admin Settings UI surfaces this as a
    /// checkbox under the SMTP section so an operator can roundtrip the value
    /// without editing appsettings by hand.
    /// </summary>
    public bool EnableSsl { get; set; } = true;
}

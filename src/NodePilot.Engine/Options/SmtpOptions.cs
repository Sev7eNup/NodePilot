namespace NodePilot.Engine.Options;

/// <summary>
/// SMTP server settings consumed by <see cref="NodePilot.Engine.Activities.EmailActivity"/>.
/// Bound from the <c>Smtp:*</c> configuration section via
/// <see cref="NodePilot.Engine.ServiceCollectionExtensions"/>.AddNodePilotActivities.
/// </summary>
/// <remarks>
/// <c>Password</c> in plaintext appsettings is flagged at startup by the security-hardening
/// warnings (see <c>Hosting/SecurityHardeningWarnings.cs</c>) — prefer an environment variable
/// (<c>Smtp__Password</c>) or a secrets manager.
/// </remarks>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 25;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "nodepilot@localhost";

    /// <summary>
    /// Enables explicit TLS / STARTTLS on the SMTP connection. Default <c>true</c> so a fresh
    /// deployment with Auth-SMTP does not send credentials in plaintext. Set to <c>false</c> only
    /// for lab/legacy setups without TLS (localhost relay, internal MTA). Turning it off while
    /// <see cref="Username"/> is set makes <c>SecurityHardeningWarnings</c> emit a boot warning,
    /// since the LOGIN/PLAIN auth mechanism would then travel unencrypted.
    /// </summary>
    public bool EnableSsl { get; set; } = true;
}

namespace NodePilot.Api.Configuration;

/// <summary>
/// Deployment posture, selected by the <c>Deployment:Mode</c> configuration key.
/// <para>
/// <see cref="DeploymentMode.Server"/> (default) is the hardened server install: Windows
/// service on a real host, verified database TLS, Kestrel bound to every interface behind a
/// TLS terminator.
/// </para>
/// <para>
/// <see cref="DeploymentMode.Desktop"/> is the single-machine desktop package: a bundled
/// PostgreSQL on 127.0.0.1 without PKI, Kestrel bound to loopback only. It is a separate
/// shipping target and relaxes only the checks that apply to a machine talking to itself.
/// </para>
/// </summary>
public enum DeploymentMode
{
    Server,
    Desktop,
}

/// <summary>
/// Single source of truth for reading <c>Deployment:Mode</c>. <see cref="IsDesktop"/> is used by
/// the boot validators, Kestrel binding, and the startup gate. It fails safe: any value other
/// than an explicit "Desktop", a typo included, resolves to the stricter
/// <see cref="DeploymentMode.Server"/> posture. A misspelled value is reported separately by
/// <c>DeploymentModeBootValidator</c> so the operator sees a clear error instead of a silent
/// downgrade.
/// </summary>
public static class DeploymentModeReader
{
    public const string Key = "Deployment:Mode";
    public const string Server = "Server";
    public const string Desktop = "Desktop";

    /// <summary>
    /// True only when <c>Deployment:Mode</c> is exactly "Desktop" (case-insensitive, trimmed).
    /// Empty, absent, "Server", or any unrecognized value yields false, the hardened Server
    /// posture. Never throws.
    /// </summary>
    public static bool IsDesktop(IConfiguration configuration)
        => string.Equals(configuration[Key]?.Trim(), Desktop, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the configured value is empty (meaning Server) or exactly "Server"/"Desktop".
    /// Used by the boot validator to reject typos with a precise message.
    /// </summary>
    public static bool IsRecognized(IConfiguration configuration)
    {
        var raw = configuration[Key];
        if (string.IsNullOrWhiteSpace(raw)) return true;
        raw = raw.Trim();
        return string.Equals(raw, Server, StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, Desktop, StringComparison.OrdinalIgnoreCase);
    }
}

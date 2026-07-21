namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Configuration for the Negotiate/Kerberos auth path. Lives under
/// <c>Authentication:Windows:*</c>. Default <see cref="Enabled"/>=false. Only takes effect
/// once the operator has registered the matching SPN on the service account and browsers
/// on the domain intranet treat the URL as part of the Local Intranet zone.
/// </summary>
public sealed class WindowsAuthOptions
{
    public const string SectionName = "Authentication:Windows";

    /// <summary>Master switch for Windows Integrated Auth (Negotiate). Default false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Legacy compatibility flag. Enterprise validation requires <c>false</c>, and the
    /// Windows endpoint rejects an explicitly identified NTLM principal unconditionally.
    /// </summary>
    public bool AllowNtlmFallback { get; set; } = false;

    /// <summary>
    /// Deployment attestation that incoming NTLM is denied by host/domain policy. The
    /// application cannot infer the negotiated package reliably from WindowsIdentity.
    /// </summary>
    public bool NtlmDisabledByPolicy { get; set; } = false;
}

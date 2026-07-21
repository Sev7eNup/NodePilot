namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Turns whatever a user typed into the login form into the UPN-form
/// (<c>local@domain</c>) that an AD simple-bind expects. Operators paste
/// usernames in three shapes:
/// <list type="bullet">
/// <item><c>"alice"</c> — bare local part, needs <c>@&lt;UpnSuffix&gt;</c> appended.</item>
/// <item><c>"DOMAIN\alice"</c> — legacy NetBIOS style, needs the DOMAIN prefix dropped
/// and the suffix appended.</item>
/// <item><c>"alice@firma.de"</c> — already UPN, passes through unchanged.</item>
/// </list>
/// All forms are trimmed and lowercased so two case-different login attempts hit the same
/// JIT user row.
/// </summary>
public static class UsernameNormalizer
{
    /// <summary>
    /// Returns the UPN form of <paramref name="raw"/> using <paramref name="upnSuffix"/> as
    /// the domain part when the input doesn't already carry one. Throws
    /// <see cref="ArgumentException"/> when the input is empty or when the input has no
    /// <c>@</c> and the suffix is also missing — that combination cannot be turned into a
    /// valid UPN, so failing fast is better than building a malformed bind DN.
    /// </summary>
    public static string ToUpn(string raw, string? upnSuffix)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Username must not be empty.", nameof(raw));

        var trimmed = raw.Trim();

        // DOMAIN\user — legacy NetBIOS form. Strip the prefix and treat the rest as a bare
        // local part. We don't try to map the NetBIOS domain to a UPN suffix; operators that
        // need a DOMAIN-aware mapping must use the explicit UPN suffix.
        var slashIdx = trimmed.IndexOf('\\');
        if (slashIdx >= 0)
            trimmed = trimmed[(slashIdx + 1)..];

        if (trimmed.Length == 0)
            throw new ArgumentException("Username must not be empty after stripping the DOMAIN prefix.", nameof(raw));

        if (trimmed.Contains('@'))
            return trimmed.ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(upnSuffix))
            throw new ArgumentException(
                "Authentication:Ldap:UpnSuffix is not configured — bare usernames cannot be converted to UPN form.",
                nameof(upnSuffix));

        var suffix = upnSuffix.Trim().TrimStart('@');
        return $"{trimmed.ToLowerInvariant()}@{suffix.ToLowerInvariant()}";
    }
}

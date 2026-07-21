namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Raised when LDAP cannot reach a verdict on the user's credentials — DC unreachable,
/// TLS handshake failed, malformed search response, etc. Distinct from a clean "wrong
/// password" verdict (which is signalled by <see cref="LdapAuthResult"/> being null).
/// <para>
/// The LDAP-first login path (<see cref="LdapAuthenticator"/>) trips the circuit breaker
/// on this and falls back to the local-password path. The login endpoint then surfaces a
/// generic <c>Invalid credentials</c> 401 to the user so an outsider can't probe whether
/// LDAP is up — operators see the detail in the audit + Serilog stream instead.
/// </para>
/// </summary>
public sealed class LdapInfrastructureException : Exception
{
    public LdapInfrastructureException(string message) : base(message) { }
    public LdapInfrastructureException(string message, Exception inner) : base(message, inner) { }
}

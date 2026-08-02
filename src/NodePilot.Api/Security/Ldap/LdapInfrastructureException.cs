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
public class LdapInfrastructureException : Exception
{
    public LdapInfrastructureException(string message) : base(message) { }
    public LdapInfrastructureException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The directory answered correctly, but the authenticated principal has no usable user
/// object: the bind succeeded, yet no entry under <c>BaseDn</c> carries a matching
/// <c>userPrincipalName</c>.
/// <para>
/// This is a <em>data</em> condition on one account, not an outage — most often an AD
/// account whose <c>userPrincipalName</c> attribute is unset or carries a different suffix.
/// Active Directory still accepts a simple bind as <c>samAccountName@dns-domain</c> in that
/// case (implicit UPN), so the password verifies while the search finds nothing (lab
/// 2026-08-01).
/// </para>
/// <para>
/// It derives from <see cref="LdapInfrastructureException"/> so the endpoint loop keeps
/// failing over — a replication-stale DC may legitimately not have a freshly created
/// account yet. Once every endpoint agrees, the login path must NOT treat it as an outage:
/// no circuit-breaker failure (one broken account would otherwise block LDAP logins for
/// everyone) and a clean, audited 401 instead of a silent 503.
/// </para>
/// </summary>
public sealed class LdapUserObjectNotFoundException : LdapInfrastructureException
{
    public LdapUserObjectNotFoundException(string message) : base(message) { }
}

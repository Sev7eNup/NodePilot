namespace NodePilot.Core.Enums;

/// <summary>
/// How a user authenticated. Stored on <c>User.Provider</c> so the login + token-validity
/// pipeline can decide which path to take per user.
/// <para>
/// <see cref="Local"/> users carry a non-null <c>PasswordHash</c> and are validated by BCrypt.
/// <see cref="Ldap"/>, <see cref="Windows"/> and <see cref="Oidc"/> users have a null hash and a non-null
/// <c>ExternalId</c> compatibility projection; canonical identities live in
/// <c>ExternalIdentity</c>. The local-login path must reject external users outright,
/// even if an attacker were to set a hash via direct DB write — defense in depth.
/// </para>
/// </summary>
public enum AuthProvider
{
    Local = 0,
    Ldap = 1,
    Windows = 2,
    Oidc = 3,
}

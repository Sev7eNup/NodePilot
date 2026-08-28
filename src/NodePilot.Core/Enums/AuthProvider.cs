namespace NodePilot.Core.Enums;

/// <summary>
/// How a user authenticated. Stored on <c>User.Provider</c> so the login and token-validation
/// pipeline can pick the right path per user.
/// <para>
/// <see cref="Local"/> users carry a non-null <c>PasswordHash</c> and are validated by BCrypt.
/// <see cref="Ldap"/>, <see cref="Windows"/> and <see cref="Oidc"/> users have a null hash and
/// a non-null <c>ExternalId</c> projection; the canonical identity lives in
/// <c>ExternalIdentity</c>. As defence in depth, the local-login path rejects external users
/// outright even if a hash were set by a direct database write.
/// </para>
/// </summary>
public enum AuthProvider
{
    Local = 0,
    Ldap = 1,
    Windows = 2,
    Oidc = 3,
}

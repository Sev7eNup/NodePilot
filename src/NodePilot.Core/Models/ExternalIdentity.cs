namespace NodePilot.Core.Models;

/// <summary>
/// A stable identity issued by an external authority. Authentication transports such as
/// LDAP simple-bind and Windows Negotiate are deliberately not part of the key: both expose
/// the same Active Directory account SID and therefore resolve to the same NodePilot user.
/// </summary>
public sealed class ExternalIdentity
{
    /// <summary>Canonical authority used for Active Directory identities.</summary>
    public const string ActiveDirectoryAuthority = "urn:nodepilot:identity:active-directory";

    /// <summary>
    /// Transitional authority for pre-migration LDAP objectGUID values. A successful LDAP
    /// login replaces this key with the canonical AD SID; it is never used to merge users.
    /// </summary>
    public const string LegacyLdapAuthority = "urn:nodepilot:identity:legacy-ldap-object-guid";

    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>
    /// Namespace that issued <see cref="Subject"/>. For OIDC this will be the issuer URI;
    /// Active Directory uses <see cref="ActiveDirectoryAuthority"/>.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Immutable subject within <see cref="Authority"/>. Active Directory subjects are
    /// canonical user SIDs for both LDAP and Windows Negotiate.
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}

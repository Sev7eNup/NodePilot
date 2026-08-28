namespace NodePilot.Core.Models;

/// <summary>
/// Snapshot of an external user's directory groups. Storing memberships in the database keeps
/// unbounded group claims out of the authentication cookie and gives background authorization
/// paths the same source of truth as HTTP requests.
/// </summary>
public sealed class DirectoryMembership
{
    /// <summary>
    /// Surrogate clustered key. The natural key stays unique but cannot be the SQL Server
    /// clustered primary key because its maximum UTF-16 width exceeds 900 bytes.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    /// <summary>
    /// Authority that owns <see cref="GroupKey"/>. AD uses the forest-wide authority constant,
    /// OIDC and SCIM use the exact issuer URL. This keeps opaque group identifiers from
    /// different providers in separate namespaces.
    /// </summary>
    public string Authority { get; set; } = ExternalIdentity.ActiveDirectoryAuthority;
    /// <summary>Provider-stable group identifier (AD SID, OIDC or SCIM group id).</summary>
    public string GroupKey { get; set; } = string.Empty;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}

namespace NodePilot.Core.Models;

/// <summary>
/// Persistent metadata for a group provisioned by a SCIM authority. Memberships reference
/// the authority's stable group key through <see cref="DirectoryMembership.GroupKey"/>.
/// </summary>
public sealed class ScimGroup
{
    public Guid Id { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool IsTombstoned { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

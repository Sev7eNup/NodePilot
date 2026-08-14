using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Reconciles the persisted <see cref="DirectoryMembership"/> rows of one (user, authority)
/// pair against a desired group set — the single implementation behind the AD login mapper,
/// the directory-sync background pass and the OIDC/SCIM identity mapper.
/// <para>
/// The key comparer is a caller decision and part of the authority's semantics: AD SIDs are
/// compared case-insensitively, OIDC/SCIM group ids are opaque and compared ordinally. Removals
/// use the desired set's own comparer, so the caller controls both halves.
/// </para>
/// </summary>
internal static class DirectoryMembershipReconciler
{
    /// <summary>
    /// Applies <paramref name="desired"/> onto an already-loaded membership list. Rows of a
    /// different authority are left untouched, so a caller may pass every membership of the user.
    /// </summary>
    public static void Apply(
        NodePilotDbContext db,
        Guid userId,
        string authority,
        IReadOnlyCollection<DirectoryMembership> existing,
        IReadOnlySet<string> desired,
        DateTime timestamp,
        StringComparer keyComparer)
    {
        var scoped = existing
            .Where(membership => string.Equals(membership.Authority, authority, StringComparison.Ordinal))
            .ToList();

        foreach (var membership in scoped)
        {
            if (!desired.Contains(membership.GroupKey))
                db.DirectoryMemberships.Remove(membership);
            else
                membership.LastSeenAt = timestamp;
        }

        var existingKeys = scoped.Select(membership => membership.GroupKey).ToHashSet(keyComparer);
        foreach (var group in desired.Where(group => !existingKeys.Contains(group)))
        {
            db.DirectoryMemberships.Add(new DirectoryMembership
            {
                UserId = userId,
                Authority = authority,
                GroupKey = group,
                LastSeenAt = timestamp,
            });
        }
    }

    /// <summary>Loads the (user, authority) memberships and applies <see cref="Apply"/> to them.</summary>
    public static async Task ApplyAsync(
        NodePilotDbContext db,
        Guid userId,
        string authority,
        IReadOnlySet<string> desired,
        DateTime timestamp,
        StringComparer keyComparer,
        CancellationToken ct)
    {
        var existing = await db.DirectoryMemberships
            .Where(membership => membership.UserId == userId && membership.Authority == authority)
            .ToListAsync(ct);
        Apply(db, userId, authority, existing, desired, timestamp, keyComparer);
    }
}

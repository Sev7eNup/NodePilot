using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// Authority-scoped directory group used by every HTTP and background authorization path.
/// Opaque group identifiers are not globally unique, so comparing GroupKey without its
/// issuer would allow a grant from one tenant/provider to bleed into another.
/// </summary>
internal sealed record DirectoryGroupPrincipal(string Authority, string GroupKey)
{
    public bool Matches(SharedFolderPermission permission)
    {
        var grantAuthority = string.IsNullOrWhiteSpace(permission.PrincipalAuthority)
            ? ExternalIdentity.ActiveDirectoryAuthority
            : permission.PrincipalAuthority;
        if (!string.Equals(Authority, grantAuthority, StringComparison.Ordinal)) return false;
        return string.Equals(
            GroupKey,
            permission.PrincipalKey,
            Authority == ExternalIdentity.ActiveDirectoryAuthority
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    public static async Task<IReadOnlyCollection<DirectoryGroupPrincipal>> LoadAsync(
        NodePilotDbContext db,
        User user,
        CancellationToken ct)
    {
        if (user.Provider == AuthProvider.Local) return [];

        HashSet<string> allowedAuthorities;
        if (user.Provider is AuthProvider.Ldap or AuthProvider.Windows)
        {
            allowedAuthorities = new(StringComparer.Ordinal)
            {
                ExternalIdentity.ActiveDirectoryAuthority,
            };
        }
        else
        {
            allowedAuthorities = (await db.ExternalIdentities.AsNoTracking()
                    .Where(identity => identity.UserId == user.Id)
                    .Select(identity => identity.Authority)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
        }

        if (allowedAuthorities.Count == 0) return [];
        var memberships = await db.DirectoryMemberships.AsNoTracking()
            .Where(membership => membership.UserId == user.Id)
            .Select(membership => new { membership.Authority, membership.GroupKey })
            .ToListAsync(ct);
        return memberships
            .Where(membership => allowedAuthorities.Contains(membership.Authority)
                              && !string.IsNullOrWhiteSpace(membership.GroupKey))
            .Select(membership => new DirectoryGroupPrincipal(
                membership.Authority, membership.GroupKey))
            .Distinct()
            .ToList();
    }
}

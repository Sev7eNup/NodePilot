using System.Linq.Expressions;
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

    /// <summary>
    /// SQL-translatable grant predicate shared by every folder-permission lookup: the user's own
    /// grants plus any group grant whose key and authority both appear in <paramref name="groups"/>.
    /// Coarse on purpose — the authority-exact, per-authority string comparison lives in
    /// <see cref="Matches"/> and has to run in memory over the candidates (see
    /// <see cref="ExactMatches"/>). A grant written before authority scoping carries an empty
    /// PrincipalAuthority and means Active Directory, so the empty string joins the authority list
    /// whenever the user holds an AD group.
    /// </summary>
    public static Expression<Func<SharedFolderPermission, bool>> GrantPredicate(
        Guid userId,
        IReadOnlyCollection<DirectoryGroupPrincipal> groups)
    {
        var userKey = userId.ToString("D");
        var groupKeys = groups.Select(group => group.GroupKey).Distinct().ToList();
        var groupAuthorities = groups.Select(group => group.Authority).Distinct().ToList();
        if (groupAuthorities.Contains(ExternalIdentity.ActiveDirectoryAuthority, StringComparer.Ordinal))
            groupAuthorities.Add(string.Empty);
        return permission =>
            (permission.PrincipalType == FolderPrincipalType.User && permission.PrincipalKey == userKey)
            || (permission.PrincipalType == FolderPrincipalType.Group
                && groupKeys.Contains(permission.PrincipalKey)
                && groupAuthorities.Contains(permission.PrincipalAuthority));
    }

    /// <summary>
    /// Narrows the candidates returned by <see cref="GrantPredicate"/> to the grants that really
    /// apply: user grants pass through, group grants must match one principal exactly.
    /// </summary>
    public static List<SharedFolderPermission> ExactMatches(
        IEnumerable<SharedFolderPermission> candidates,
        IReadOnlyCollection<DirectoryGroupPrincipal> groups) =>
        candidates
            .Where(permission => permission.PrincipalType == FolderPrincipalType.User
                              || groups.Any(group => group.Matches(permission)))
            .ToList();

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

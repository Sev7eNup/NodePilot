using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

public sealed record ExternalAuthorizationEvaluation(
    bool IsCurrent,
    UserRole EffectiveRole,
    DateTime? ValidUntil,
    string Reason);

/// <summary>
/// Provider-aware source of truth for external authorization freshness. AD logins/syncs
/// produce a complete user snapshot, while SCIM Group writes are partial and therefore
/// must be evaluated from authority-scoped membership timestamps rather than a coarse
/// timestamp on the user row.
/// </summary>
public sealed class ExternalAuthorizationEvaluator(
    NodePilotDbContext db,
    IOptions<AuthenticationPolicyOptions> policy,
    IOptions<EnterpriseOidcOptions> oidcOptions)
{
    public async Task<ExternalAuthorizationEvaluation> EvaluateAsync(
        User user,
        DateTime at,
        CancellationToken ct)
        => (await EvaluateManyAsync([user], at, ct))[user.Id];

    public async Task<IReadOnlyDictionary<Guid, ExternalAuthorizationEvaluation>> EvaluateManyAsync(
        IReadOnlyCollection<User> users,
        DateTime at,
        CancellationToken ct)
    {
        var maxStaleness = TimeSpan.FromMinutes(Math.Clamp(
            policy.Value.MaxAuthorizationStalenessMinutes, 1, 15));
        var result = new Dictionary<Guid, ExternalAuthorizationEvaluation>(users.Count);
        foreach (var user in users.Where(user => user.Provider == AuthProvider.Local))
            result[user.Id] = new ExternalAuthorizationEvaluation(true, user.Role, null, "local");
        foreach (var user in users.Where(user => user.Provider is AuthProvider.Ldap or AuthProvider.Windows))
        {
            var adValidUntil = user.LastDirectorySyncAt?.Add(maxStaleness);
            result[user.Id] = adValidUntil is not null && adValidUntil > at
                ? new ExternalAuthorizationEvaluation(true, user.Role, adValidUntil, "ad_snapshot_current")
                : new ExternalAuthorizationEvaluation(false, user.Role, adValidUntil, "ad_snapshot_stale");
        }
        foreach (var user in users.Where(user => user.Provider is not (
                     AuthProvider.Local or AuthProvider.Ldap or AuthProvider.Windows or AuthProvider.Oidc)))
            result[user.Id] = new ExternalAuthorizationEvaluation(
                false, user.Role, null, "unknown_external_provider");

        var oidcUsers = users.Where(user => user.Provider == AuthProvider.Oidc).ToList();
        if (oidcUsers.Count == 0) return result;

        var oidc = oidcOptions.Value;
        var authority = oidc.Authority;
        if (!OidcIdentityMapper.IsValidIssuer(authority))
        {
            foreach (var user in oidcUsers)
                result[user.Id] = new ExternalAuthorizationEvaluation(
                    false, user.Role, null, "oidc_authority_invalid");
            return result;
        }

        var allowedGroups = oidc.AllowedGroupIds
            .Where(OidcIdentityMapper.IsValidGroupId)
            .ToHashSet(StringComparer.Ordinal);
        if (allowedGroups.Count == 0)
        {
            foreach (var user in oidcUsers)
                result[user.Id] = new ExternalAuthorizationEvaluation(
                    false, user.Role, null, "oidc_allowed_groups_missing");
            return result;
        }

        var cutoff = at - maxStaleness;
        var relevantGroups = allowedGroups
            .Concat(oidc.GlobalRoleMappings
                .Select(mapping => mapping.GroupId)
                .Where(OidcIdentityMapper.IsValidGroupId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var identities = new List<ExternalIdentity>();
        var coarseMemberships = new List<DirectoryMembership>();
        // Keep SQL Server parameter counts bounded for enterprise-sized directories.
        foreach (var batch in oidcUsers.Select(user => user.Id).Chunk(500))
        {
            identities.AddRange(await db.ExternalIdentities.AsNoTracking()
                .Where(identity => batch.Contains(identity.UserId)
                                && identity.Authority == authority)
                .ToListAsync(ct));
            coarseMemberships.AddRange(await db.DirectoryMemberships.AsNoTracking()
                .Where(membership => batch.Contains(membership.UserId)
                                  && membership.Authority == authority
                                  && relevantGroups.Contains(membership.GroupKey)
                                  && membership.LastSeenAt > cutoff)
                .ToListAsync(ct));
        }
        var exactIdentityCounts = identities
            .Where(identity => string.Equals(identity.Authority, authority, StringComparison.Ordinal))
            .GroupBy(identity => identity.UserId)
            .ToDictionary(group => group.Key, group => group.Count());
        var membershipsByUser = coarseMemberships
            .Where(membership => string.Equals(membership.Authority, authority, StringComparison.Ordinal)
                              && OidcIdentityMapper.IsValidGroupId(membership.GroupKey))
            .GroupBy(membership => membership.UserId)
            .ToDictionary(group => group.Key, group => group.Take(OidcIdentityMapper.MaximumGroups).ToList());

        foreach (var user in oidcUsers)
        {
            if (exactIdentityCounts.GetValueOrDefault(user.Id) != 1)
            {
                result[user.Id] = new ExternalAuthorizationEvaluation(
                    false, user.Role, null, "oidc_identity_missing_or_ambiguous");
                continue;
            }

            var memberships = membershipsByUser.GetValueOrDefault(user.Id) ?? [];
            var groups = memberships.Select(membership => membership.GroupKey)
                .ToHashSet(StringComparer.Ordinal);
            if (!groups.Overlaps(allowedGroups))
            {
                result[user.Id] = new ExternalAuthorizationEvaluation(
                    false, user.Role, null, "oidc_allowed_membership_stale_or_missing");
                continue;
            }

            var effectiveRole = OidcIdentityMapper.ResolveRole(groups, oidc.GlobalRoleMappings);
            var validUntil = memberships.Min(membership => membership.LastSeenAt).Add(maxStaleness);
            result[user.Id] = effectiveRole != user.Role
                ? new ExternalAuthorizationEvaluation(
                    false, effectiveRole, validUntil, "oidc_effective_role_changed")
                : new ExternalAuthorizationEvaluation(
                    true, effectiveRole, validUntil, "oidc_memberships_current");
        }

        return result;
    }
}

using NodePilot.Core.Enums;

namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Resolves a user's effective global <see cref="UserRole"/> from their AD-group
/// memberships using the <c>Authentication:Ldap:GlobalRoleMappings</c> table.
/// <para>
/// Highest-role-wins: a user who is in <c>Domain Admins</c> AND in <c>NodePilot-Operators</c>
/// gets <see cref="UserRole.Admin"/>. A user with no matching group gets
/// <see cref="UserRole.Viewer"/> by default (matches the JIT-Provisioning default).
/// </para>
/// </summary>
public static class GlobalRoleResolver
{
    /// <summary>
    /// Picks the highest <see cref="UserRole"/> any of <paramref name="userGroupSids"/>
    /// maps to via <paramref name="mappings"/>. SID comparison is case-insensitive
    /// (Windows stores SIDs in uppercase but operators sometimes paste lowercase).
    /// </summary>
    public static UserRole Resolve(IEnumerable<string> userGroupSids, IEnumerable<GlobalRoleMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(userGroupSids);
        ArgumentNullException.ThrowIfNull(mappings);

        var sidSet = new HashSet<string>(userGroupSids, StringComparer.OrdinalIgnoreCase);
        UserRole best = UserRole.Viewer;

        foreach (var m in mappings)
        {
            if (string.IsNullOrWhiteSpace(m.GroupSid)) continue;
            if (!sidSet.Contains(m.GroupSid)) continue;
            // UserRole enum order is Viewer=0 < Operator=1 < Admin=2; numeric Max picks
            // the strongest matching role.
            if ((int)m.Role > (int)best) best = m.Role;
        }

        return best;
    }
}

using Microsoft.EntityFrameworkCore;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>
/// DB-dependent startup invariant for enterprise authentication. Configuration validation
/// cannot see whether an upgraded installation still has an independently recoverable local
/// administrator, so this check runs after migrations and before the HTTP pipeline starts.
/// </summary>
public static class EnterpriseRecoveryInvariant
{
    public static async Task EnsureAsync(
        NodePilotDbContext db,
        IConfiguration configuration,
        CancellationToken ct = default)
    {
        if (!ExternalAuthenticationEnabled(configuration)) return;
        if (!await db.Users.AsNoTracking().AnyAsync(ct)) return;
        if (await BreakGlassAccountPolicy.ExistsAsync(db, ct)) return;

        throw new InvalidOperationException(
            "Enterprise authentication is enabled, but the database has no active local break-glass Admin. " +
            "Restore or provision a local Admin with IsBreakGlass=true and a valid password before starting SSO.");
    }

    internal static bool ExternalAuthenticationEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>("Authentication:Ldap:Enabled")
        || configuration.GetValue<bool>("Authentication:Windows:Enabled")
        || configuration.GetValue<bool>("Authentication:Oidc:Enabled")
        || configuration.GetValue<bool>("Authentication:Scim:Enabled");
}

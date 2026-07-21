using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace NodePilot.Api.Security.Ldap;

public sealed class LdapHealthCheck(
    ILdapConnectionAdapter adapter,
    IOptionsMonitor<LdapOptions> options,
    ActiveDirectoryAuthenticationConfiguration? activeConfiguration = null) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!(activeConfiguration?.DirectorySyncEnabled
              ?? options.CurrentValue.Enabled))
            return HealthCheckResult.Healthy("LDAP disabled");
        var health = await adapter.CheckHealthAsync(cancellationToken);
        var data = new Dictionary<string, object>
        {
            ["endpoint"] = health.Endpoint ?? "unknown",
            ["errorCode"] = health.ErrorCode ?? "none",
            ["healthyEndpoints"] = health.HealthyEndpoints ?? [],
            ["failedEndpoints"] = health.FailedEndpoints ?? [],
        };
        if (health.Degraded)
            return HealthCheckResult.Degraded(
                "LDAPS failover capacity is degraded", data: data);
        return health.Healthy
            ? HealthCheckResult.Healthy("LDAPS service bind succeeded", data)
            : HealthCheckResult.Unhealthy("LDAPS service bind failed", data: data);
    }
}

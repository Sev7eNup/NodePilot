using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NodePilot.Api.Security.Ldap;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security.Ldap;

public sealed class LdapHealthCheckTests
{
    [Fact]
    public async Task OneOfTwoEndpointsUnavailable_ReportsDegradedFailoverCapacity()
    {
        var adapter = new HealthAdapter(new LdapEndpointHealth(
            Healthy: true,
            Endpoint: "dc1.example.test:636",
            ErrorCode: "partial_endpoint_failure",
            Degraded: true,
            HealthyEndpoints: ["dc1.example.test:636"],
            FailedEndpoints: ["dc2.example.test:636"]));
        var options = new StaticOptionsMonitor<LdapOptions>(new LdapOptions { Enabled = true });
        var check = new LdapHealthCheck(adapter, options);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["healthyEndpoints"].Should().BeEquivalentTo(new[] { "dc1.example.test:636" });
        result.Data["failedEndpoints"].Should().BeEquivalentTo(new[] { "dc2.example.test:636" });
    }

    private sealed class HealthAdapter(LdapEndpointHealth health) : ILdapConnectionAdapter
    {
        public Task<LdapAuthResult?> AuthenticateAsync(string upn, string password, CancellationToken ct) =>
            Task.FromResult<LdapAuthResult?>(null);

        public Task<LdapEndpointHealth> CheckHealthAsync(CancellationToken ct) =>
            Task.FromResult(health);
    }
}

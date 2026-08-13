using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests.Architecture;

public sealed class MonitoringDeploymentSecurityTests
{
    [Fact]
    public void MonitoringCompose_DefaultExposureAndSupplyChain_AreFailClosed()
    {
        var root = ProductionSources.RepoRoot();
        var compose = File.ReadAllText(Path.Combine(root, "grafana", "docker-compose.yml"));
        var environment = File.ReadAllText(Path.Combine(root, "grafana", ".env.example"));

        compose.Should().Contain("127.0.0.1:9090:9090");
        compose.Should().Contain("127.0.0.1:3000:3000");
        compose.Should().NotContain(":latest");
        Regex.Matches(compose, @"(?m)^\s*image:\s*[^\s]+:[^\s@]+@sha256:[a-f0-9]{64}\s*$")
            .Count.Should().Be(2, "Grafana and Prometheus must be pinned by readable version and immutable manifest digest");
        compose.Should().Contain("GF_USERS_ALLOW_SIGN_UP: \"false\"");
        compose.Should().Contain("GF_AUTH_ANONYMOUS_ENABLED: \"false\"");
        compose.Should().Contain("NODEPILOT_GRAFANA_ADMIN_PASSWORD:?NODEPILOT_GRAFANA_ADMIN_PASSWORD must be set");
        compose.Should().NotContain("${GF_SECURITY_ADMIN_PASSWORD:",
            "legacy .env files may still carry the publicly documented default and must fail closed");

        environment.Should().NotMatchRegex("(?i)(change[ -]?me|changeme|password\\s*=\\s*[^<\\s]+)");
        environment.Should().MatchRegex("(?m)^NODEPILOT_GRAFANA_ADMIN_PASSWORD=\\s*$",
            "copying the example must still fail Compose's required-value expansion until an operator supplies a secret");
    }

    [Fact]
    public void MonitoringDocumentation_RemoteAccessRequiresAuthenticatedTlsProxy()
    {
        var root = ProductionSources.RepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "grafana", "README.md"));
        var rootReadme = File.ReadAllText(Path.Combine(root, "README.md"));

        readme.Should().Contain("127.0.0.1");
        readme.Should().Contain("TLS reverse proxy");
        readme.Should().Contain("authentication");
        readme.Should().Contain("Do not publish ports 3000 or 9090 directly");
        readme.Should().Contain("docker compose pull");
        rootReadme.Should().Contain("NODEPILOT_GRAFANA_ADMIN_PASSWORD");
        rootReadme.Should().Contain("Compose fails closed while the password is missing");
    }

    [Fact]
    public void MonitoringDocumentation_ExistingGrafanaVolumeRequiresExplicitPasswordRotation()
    {
        var root = ProductionSources.RepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "grafana", "README.md"));

        readme.Should().Contain("persistent", Exactly.Once(),
            "operators must understand that changing Compose input does not update Grafana's stored credential");
        readme.Should().Contain("first start");
        readme.Should().Contain("reset-admin-password");
        readme.Should().Contain("--password-from-stdin",
            "the replacement password must not be exposed in the process command line");
        readme.Should().Contain("RandomNumberGenerator]::Create()");
        readme.Should().NotContain("RandomNumberGenerator]::GetBytes(",
            "that static overload is unavailable in Windows PowerShell 5.1");
    }
}

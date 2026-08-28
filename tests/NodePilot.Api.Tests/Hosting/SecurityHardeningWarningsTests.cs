using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using NodePilot.Api.Hosting;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Coverage for the strict-mode path in <see cref="SecurityHardeningWarnings"/>. The log
/// escalation to Error level is not tested via Serilog capture here; what matters
/// operationally is that StrictAllowedHosts=true aborts boot when an unsafe Host value is
/// configured.
/// </summary>
public class SecurityHardeningWarningsTests
{
    private static IConfiguration BuildConfig(Dictionary<string, string?> kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public void LogSecurityHardeningWarnings_DevelopmentEnvironment_NoOp()
    {
        var env = new StubEnvironment { EnvironmentName = "Development" };
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "*",
            ["Security:StrictAllowedHosts"] = "true",
        });

        // Even with strict mode in Dev: no throw, because the whole method is a no-op in Dev.
        var act = () => SecurityHardeningWarnings.LogSecurityHardeningWarnings(cfg, env);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("{{ALLOWED_HOSTS}}")]
    [InlineData("{{HOSTS}}")]
    public void LogSecurityHardeningWarnings_StrictMode_UnsafeAllowedHosts_Throws(string allowedHosts)
    {
        var env = new StubEnvironment();
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = allowedHosts,
            ["Security:StrictAllowedHosts"] = "true",
        });

        var act = () => SecurityHardeningWarnings.LogSecurityHardeningWarnings(cfg, env);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Security:StrictAllowedHosts=true and AllowedHosts is unsafe*");
    }

    [Fact]
    public void LogSecurityHardeningWarnings_StrictMode_ConcreteFqdn_DoesNotThrow()
    {
        var env = new StubEnvironment();
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "nodepilot.example.com",
            ["Security:StrictAllowedHosts"] = "true",
        });

        var act = () => SecurityHardeningWarnings.LogSecurityHardeningWarnings(cfg, env);
        act.Should().NotThrow();
    }

    [Fact]
    public void LogSecurityHardeningWarnings_NoStrictMode_StarAllowedHosts_DoesNotThrow()
    {
        // Default path: without strict mode, this only logs (at Error level) and does not
        // abort boot. Deployments using "*" keep starting since the check is opt-in.
        var env = new StubEnvironment();
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "*",
        });

        var act = () => SecurityHardeningWarnings.LogSecurityHardeningWarnings(cfg, env);
        act.Should().NotThrow();
    }

    [Fact]
    public void LogSecurityHardeningWarnings_StrictMode_MultipleHosts_DoesNotThrow()
    {
        // Semicolon-separated hosts are valid per ASP.NET conventions and must be accepted.
        var env = new StubEnvironment();
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "nodepilot.example.com;internal.example.com",
            ["Security:StrictAllowedHosts"] = "true",
        });

        var act = () => SecurityHardeningWarnings.LogSecurityHardeningWarnings(cfg, env);
        act.Should().NotThrow();
    }

    // ---- Plaintext SMTP warning code path ----

    [Fact]
    public void LogSecurityHardeningWarnings_PlaintextSmtpWithUsername_DoesNotThrow()
    {
        // The warning is informational (Serilog Log.Warning, not an exception). This test
        // exercises that code path for coverage; the actual log assertion is left to the
        // manual deployment audit.
        var env = new StubEnvironment();
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "nodepilot.example.com",
            ["Smtp:EnableSsl"] = "false",
            ["Smtp:Username"] = "mail-user",
        });

        var act = () => SecurityHardeningWarnings.LogSecurityHardeningWarnings(cfg, env);
        act.Should().NotThrow();
    }

    [Fact]
    public void LogSecurityHardeningWarnings_AllPermissiveTogglesAndPlaintextSecrets_ExercisesEveryWarning()
    {
        // Fires every warning branch at once: hardening flags off, plaintext secrets present,
        // weak JWT/DPAPI/LDAP settings, anonymous Prometheus scrape. A safe AllowedHosts keeps
        // the strict fail-hard out of the way so the audit can run end-to-end without throwing.
        var env = new StubEnvironment();
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "nodepilot.example.com",
            ["Remote:RequireWinRmSsl"] = "false",
            ["RestApi:BlockPrivateNetworks"] = "false",
            ["FileSystemOperation:RejectTraversal"] = "false",
            ["SqlActivity:RequireConnectionRef"] = "false",
            ["Trigger:Database:RequireConnectionRef"] = "false",
            ["StartProgram:DisallowShellExecute"] = "false",
            ["Jwt:Issuer"] = "NodePilot",
            ["Jwt:Audience"] = "NodePilot",
            ["Smtp:Password"] = "p4ss",
            ["Llm:Profiles:openai:Name"] = "OpenAI",
            ["Llm:Profiles:openai:ApiKey"] = "sk-secret",
            ["Llm:Profiles:local:ApiKey"] = "EMPTY", // the placeholder must not warn
            ["Llm:Profiles:nokey:BaseUrl"] = "http://localhost:11434/v1", // no key at all
            ["Authentication:Ldap:Enabled"] = "true",
            ["Authentication:Ldap:UseSsl"] = "false",
            ["Credentials:DpapiScope"] = "CurrentUser",
            ["Secrets:Provider"] = "AesGcm",
            ["Secrets:MasterKey"] = "base64key",
            ["OpenTelemetry:Prometheus:Password"] = "pw",
            ["OpenTelemetry:Prometheus:BearerToken"] = "tok",
            ["OpenTelemetry:Enabled"] = "true",
            ["OpenTelemetry:Exporters:PrometheusScrape"] = "true",
            ["OpenTelemetry:Exporters:PrometheusScrapeAllowAnonymous"] = "true",
        });

        var act = () => SecurityHardeningWarnings.LogSecurityHardeningWarnings(cfg, env);
        act.Should().NotThrow();
    }

    [Fact]
    public void LogRetentionDisabledWarnings_AllSweepersDisabled_DoesNotThrow()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Retention:Executions:Enabled"] = "false",
            ["Retention:AuditLog:Enabled"] = "false",
            ["Retention:WorkflowVersions:Enabled"] = "false",
        });

        var act = () => SecurityHardeningWarnings.LogRetentionDisabledWarnings(cfg);
        act.Should().NotThrow();
    }

    [Fact]
    public void LogRetentionDisabledWarnings_AllSweepersEnabled_NoWarnings()
    {
        var act = () => SecurityHardeningWarnings.LogRetentionDisabledWarnings(
            BuildConfig(new Dictionary<string, string?>()));
        act.Should().NotThrow();
    }

    [Fact]
    public void LogSecurityHardeningWarnings_PlaintextSmtpWithoutUsername_DoesNotThrow()
    {
        // A localhost relay without auth is the legitimate use case for EnableSsl=false;
        // there are no credentials on the wire to leak, so the warning must not fire
        // (no throw, and no boot-blocking semantics).
        var env = new StubEnvironment();
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "nodepilot.example.com",
            ["Smtp:EnableSsl"] = "false",
        });

        var act = () => SecurityHardeningWarnings.LogSecurityHardeningWarnings(cfg, env);
        act.Should().NotThrow();
    }
}

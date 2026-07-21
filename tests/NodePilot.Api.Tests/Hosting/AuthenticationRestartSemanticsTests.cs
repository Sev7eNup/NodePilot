using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NodePilot.Api.Hosting;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Api.Security.Scim;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class AuthenticationRestartSemanticsTests
{
    [Fact]
    public void PendingSettingsWrite_DoesNotChangeActiveEnterpriseOptionsBeforeRestart()
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "NodePilot-Test-Secret-Key-Minimum-32-Characters!",
            ["Authentication:MaxAuthorizationStalenessMinutes"] = "15",
            ["Authentication:Oidc:Authority"] = "https://old-idp.example.test/tenant",
            ["Authentication:Scim:Authority"] = "https://old-idp.example.test/tenant",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNodePilotAuthentication(configuration, new TestEnvironment());

        // Simulates Admin Settings persisting the next startup configuration before any
        // request-scoped mapper/evaluator has resolved IOptions<T> for the first time.
        configuration["Authentication:MaxAuthorizationStalenessMinutes"] = "5";
        configuration["Authentication:Oidc:Authority"] = "https://new-idp.example.test/tenant";
        configuration["Authentication:Scim:Authority"] = "https://new-idp.example.test/tenant";

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<AuthenticationPolicyOptions>>().Value
            .MaxAuthorizationStalenessMinutes.Should().Be(15);
        provider.GetRequiredService<IOptions<EnterpriseOidcOptions>>().Value.Authority
            .Should().Be("https://old-idp.example.test/tenant");
        provider.GetRequiredService<IOptions<ScimOptions>>().Value.Authority
            .Should().Be("https://old-idp.example.test/tenant");
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NodePilot.Api.Tests";
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

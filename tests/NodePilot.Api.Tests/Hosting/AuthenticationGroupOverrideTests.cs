using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodePilot.Api.Hosting;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Security.Oidc;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Enums;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class AuthenticationGroupOverrideTests
{
    [Theory]
    [InlineData("Ldap", "AllowedGroupSids", false)]
    [InlineData("Ldap", "AllowedGroupSids", true)]
    [InlineData("Oidc", "AllowedGroupIds", false)]
    [InlineData("Oidc", "AllowedGroupIds", true)]
    public void Startup_ReplacesAdmissionListFromLowerProvider(string section, string property, bool empty)
    {
        var configuration = Layered(section,
            $$"""{"{{property}}":["keep","removed"]}""",
            empty ? $$"""{"{{property}}":[]}""" : $$"""{"{{property}}":["keep"]}""");
        using var services = Build(configuration);
        var actual = section == "Ldap"
            ? services.GetRequiredService<ActiveDirectoryAuthenticationConfiguration>().Ldap.AllowedGroupSids.ToArray()
            : services.GetRequiredService<IOptions<EnterpriseOidcOptions>>().Value.AllowedGroupIds;
        actual.Should().Equal(empty ? [] : new[] { "keep" });
    }

    [Theory]
    [InlineData("Ldap", "GroupSid", false)]
    [InlineData("Ldap", "GroupSid", true)]
    [InlineData("Oidc", "GroupId", false)]
    [InlineData("Oidc", "GroupId", true)]
    public void Startup_DoesNotInheritRemovedRoleRowsOrMissingRoleFields(string section, string groupKey, bool empty)
    {
        var configuration = Layered(section,
            $$"""{"GlobalRoleMappings":[{"{{groupKey}}":"old","Role":"Admin"},{"{{groupKey}}":"removed","Role":"Admin"}]}""",
            empty ? "{\"GlobalRoleMappings\":[]}" : $$"""{"GlobalRoleMappings":[{"{{groupKey}}":"new"}]}""");
        using var services = Build(configuration);
        var roles = section == "Ldap"
            ? services.GetRequiredService<ActiveDirectoryAuthenticationConfiguration>().Ldap.GlobalRoleMappings.Select(m => m.Role)
            : services.GetRequiredService<IOptions<EnterpriseOidcOptions>>().Value.GlobalRoleMappings.Select(m => m.Role);
        roles.Should().Equal(empty ? [] : new[] { UserRole.Viewer });
    }

    private static IConfigurationRoot Layered(string section, string lower, string higher) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "NodePilot-Test-Secret-Key-Minimum-32-Characters!",
            })
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("{\"Authentication\":{\"" + section + "\":" + lower + "}}")))
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("{\"Authentication\":{\"" + section + "\":" + higher + "}}")))
            .Build();

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNodePilotAuthentication(configuration, new StubEnvironment(environmentName: "Development"));
        return services.BuildServiceProvider();
    }
}

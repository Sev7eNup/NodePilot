using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Ldap;
using NodePilot.Data;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Api.Tests.Controllers;

public sealed class AuthControllerMethodsTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly NodePilotDbContext _db;

    public AuthControllerMethodsTests()
    {
        var (conn, db) = NodePilot.TestCommons.TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private sealed class TestJwtKeyProvider : IJwtKeyProvider
    {
        public string Key => "NodePilot-Test-Secret-Key-Minimum-32-Characters!";
    }

    private static IConfiguration NewConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "NodePilot-Test-Secret-Key-Minimum-32-Characters!",
        }).Build();

    private AuthController NewController(LdapOptions? ldap = null, WindowsAuthOptions? windows = null)
    {
        var cfg = NewConfig();
        var key = new TestJwtKeyProvider();
        var issuer = new AuthSessionIssuer(cfg, key, NoopAuditWriter.Instance);
        IOptionsMonitor<LdapOptions>? ldapMonitor = ldap is null ? null : new StaticOptionsMonitor<LdapOptions>(ldap);
        IOptionsMonitor<WindowsAuthOptions>? winMonitor = windows is null ? null : new StaticOptionsMonitor<WindowsAuthOptions>(windows);
        return new AuthController(_db, cfg, NoopAuditWriter.Instance, key, issuer,
            ldapAuthenticator: null,
            externalUserMapper: null,
            ldapOptions: ldapMonitor,
            windowsOptions: winMonitor);
    }

    [Fact]
    public void Methods_DefaultDeployment_LocalOnly()
    {
        var controller = NewController();

        var result = controller.Methods();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<AuthMethodsResponse>().Subject;
        body.Local.Should().BeTrue();
        body.Ldap.Should().BeFalse();
        body.Windows.Should().BeFalse();
        body.WindowsEndpoint.Should().BeNull();
    }

    [Fact]
    public void Methods_LdapEnabled_FlagsLdap()
    {
        var controller = NewController(ldap: new LdapOptions { Enabled = true });

        var body = ((OkObjectResult)controller.Methods().Result!).Value as AuthMethodsResponse;

        body!.Ldap.Should().BeTrue();
        body.Windows.Should().BeFalse();
    }

    [Fact]
    public void Methods_WindowsEnabled_PublishesEndpoint()
    {
        var controller = NewController(windows: new WindowsAuthOptions { Enabled = true });

        var body = ((OkObjectResult)controller.Methods().Result!).Value as AuthMethodsResponse;

        body!.Windows.Should().BeTrue();
        body.WindowsEndpoint.Should().Be("/api/auth/windows");
    }

    [Fact]
    public void Methods_BothEnabled_LightUpAll()
    {
        var controller = NewController(
            ldap: new LdapOptions { Enabled = true },
            windows: new WindowsAuthOptions { Enabled = true });

        var body = ((OkObjectResult)controller.Methods().Result!).Value as AuthMethodsResponse;

        body!.Local.Should().BeTrue();
        body.Ldap.Should().BeTrue();
        body.Windows.Should().BeTrue();
        body.WindowsEndpoint.Should().Be("/api/auth/windows");
    }
}

using FluentAssertions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Verifies the env-aware Secure-flag policy on the auth cookie pair (<c>np_auth</c> +
/// <c>np_csrf</c>). The legacy behaviour ("Secure follows <c>Request.IsHttps</c>") still
/// kicks in when the issuer was constructed without an <see cref="IHostEnvironment"/>
/// (test fixtures and dev-time direct construction). When the host environment is
/// non-Development, the cookies must be Secure regardless of <c>Request.IsHttps</c> —
/// a Reverse-Proxy that strips <c>X-Forwarded-Proto</c> would otherwise hand out cookies
/// that a passive on-path attacker could replay over plain HTTP.
/// </summary>
public class AuthSessionIssuerSecureCookieTests
{
    private sealed class TestJwtKeyProvider : IJwtKeyProvider
    {
        public string Key => "NodePilot-Test-Secret-Key-Minimum-32-Characters!";
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NodePilot.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static IConfiguration NewConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "NodePilot-Test-Secret-Key-Minimum-32-Characters!",
            ["Jwt:Issuer"] = "NodePilot",
            ["Jwt:Audience"] = "NodePilot",
        }).Build();

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "ops-user",
        Role = UserRole.Operator,
        PasswordHash = "irrelevant-for-issuer",
    };

    private static DefaultHttpContext NewHttpContext(bool isHttps)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = isHttps ? "https" : "http";
        ctx.Request.Host = new HostString("nodepilot.example.com");
        return ctx;
    }

    private static (string? Auth, string? Csrf) ExtractSetCookieHeader(DefaultHttpContext ctx)
    {
        var values = ctx.Response.Headers["Set-Cookie"].ToArray();
        string? auth = values.FirstOrDefault(v => v is not null && v.StartsWith("np_auth=", StringComparison.Ordinal));
        string? csrf = values.FirstOrDefault(v => v is not null && v.StartsWith("np_csrf=", StringComparison.Ordinal));
        return (auth, csrf);
    }

    [Fact]
    public async Task NoEnvironment_HttpRequest_OmitsSecure()
    {
        // Legacy behaviour for the 10 test fixtures that construct the issuer with the 3-arg
        // ctor: the missing env falls back to "Secure follows Request.IsHttps" — same shape
        // those fixtures were written against.
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance);
        var ctx = NewHttpContext(isHttps: false);

        await issuer.IssueAsync(NewUser(), AuthSource.Local, ctx, CancellationToken.None);

        var (auth, csrf) = ExtractSetCookieHeader(ctx);
        auth.Should().NotBeNull().And.NotContain("secure", "isHttps=false and no env hint defaults to non-Secure");
        csrf.Should().NotBeNull().And.NotContain("secure");
    }

    [Fact]
    public async Task DevelopmentEnvironment_HttpRequest_OmitsSecure()
    {
        // `dotnet run --urls http://localhost:5000` must still produce a working auth cookie
        // in dev — otherwise the SPA cannot store the session at all on plain-HTTP localhost.
        var env = new FakeEnvironment { EnvironmentName = Environments.Development };
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, env);
        var ctx = NewHttpContext(isHttps: false);

        await issuer.IssueAsync(NewUser(), AuthSource.Local, ctx, CancellationToken.None);

        var (auth, csrf) = ExtractSetCookieHeader(ctx);
        auth.Should().NotContain("secure");
        csrf.Should().NotContain("secure");
    }

    [Fact]
    public async Task ProductionEnvironment_HttpRequest_SetsSecure()
    {
        // Defense-in-Depth: production must never hand out cookies without the Secure-flag,
        // even when Request.IsHttps comes back false (proxy without ForwardedHeaders, etc.).
        var env = new FakeEnvironment { EnvironmentName = Environments.Production };
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, env);
        var ctx = NewHttpContext(isHttps: false);

        await issuer.IssueAsync(NewUser(), AuthSource.Local, ctx, CancellationToken.None);

        var (auth, csrf) = ExtractSetCookieHeader(ctx);
        auth.Should().NotBeNull();
        auth!.Should().Contain("secure", "non-Development env enforces Secure regardless of request scheme");
        csrf.Should().NotBeNull();
        csrf!.Should().Contain("secure");
    }

    [Fact]
    public async Task StagingEnvironment_HttpsRequest_SetsSecure()
    {
        // Staging is also non-Development → same hardening as Production.
        var env = new FakeEnvironment { EnvironmentName = Environments.Staging };
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, env);
        var ctx = NewHttpContext(isHttps: true);

        await issuer.IssueAsync(NewUser(), AuthSource.Local, ctx, CancellationToken.None);

        var (auth, csrf) = ExtractSetCookieHeader(ctx);
        auth!.Should().Contain("secure");
        csrf!.Should().Contain("secure");
    }

    [Fact]
    public async Task EnterpriseUser_WithFiveHundredGroups_StillGetsCompactCookie()
    {
        var user = NewUser();
        user.Provider = AuthProvider.Ldap;
        user.KnownGroupSidsJson = JsonSerializer.Serialize(
            Enumerable.Range(1, 500).Select(i => $"S-1-5-21-111111111-222222222-333333333-{1000 + i}"));
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance);
        var ctx = NewHttpContext(isHttps: true);

        var session = await issuer.IssueAsync(user, AuthSource.Ldap, ctx, CancellationToken.None);

        var (auth, _) = ExtractSetCookieHeader(ctx);
        auth.Should().NotBeNull();
        auth!.Length.Should().BeLessThan(3800,
            "directory memberships are server-side and must not inflate the browser cookie");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(session.Token);
        jwt.Claims.Should().NotContain(c => c.Type == ClaimTypes.GroupSid);
        jwt.Claims.Should().ContainSingle(c => c.Type == AuthSessionIssuer.SessionIdClaim);
    }

    [Fact]
    public async Task BreakGlassLogin_EmitsDedicatedEmergencyAccessAuditSignal()
    {
        var user = NewUser();
        user.Role = UserRole.Admin;
        user.IsBreakGlass = true;
        var audit = new CapturingAuditWriter();
        var issuer = new AuthSessionIssuer(NewConfig(), new TestJwtKeyProvider(), audit);

        await issuer.IssueAsync(user, AuthSource.Local, NewHttpContext(isHttps: true), default);

        var call = audit.Calls.Should().ContainSingle().Subject;
        call.Action.Should().Be("BREAK_GLASS_LOGIN_SUCCESS");
        using var details = JsonDocument.Parse(call.Details!);
        details.RootElement.GetProperty("breakGlass").GetBoolean().Should().BeTrue();
        details.RootElement.GetProperty("source").GetString().Should().Be("Local");
    }

    [Fact]
    public async Task RefreshToken_IsSingleUseWithinServerSideSessionFamily()
    {
        using var db = TestDbFactory.Create();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var issuer = new AuthSessionIssuer(
            NewConfig(), new TestJwtKeyProvider(), NoopAuditWriter.Instance, db: db);
        var loginContext = NewHttpContext(isHttps: true);
        var original = await issuer.IssueAsync(user, AuthSource.Local, loginContext, default);
        var originalJwt = new JwtSecurityTokenHandler().ReadJwtToken(original.Token);
        var originalPrincipal = new ClaimsPrincipal(new ClaimsIdentity(originalJwt.Claims, "jwt"));

        var firstRefreshContext = NewHttpContext(isHttps: true);
        firstRefreshContext.User = originalPrincipal;
        var refreshed = await issuer.RefreshAsync(user, firstRefreshContext, default);

        var replayContext = NewHttpContext(isHttps: true);
        replayContext.User = originalPrincipal;
        var replay = async () => await issuer.RefreshAsync(user, replayContext, default);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>();

        var refreshedJwt = new JwtSecurityTokenHandler().ReadJwtToken(refreshed.Token);
        var persisted = db.AuthSessions.Single();
        persisted.CurrentJti.Should().Be(refreshedJwt.Id);
        persisted.RefreshGeneration.Should().Be(1);
    }
}

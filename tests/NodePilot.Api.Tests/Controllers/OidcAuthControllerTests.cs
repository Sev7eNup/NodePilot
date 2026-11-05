using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Hosting;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class OidcAuthControllerTests
{
    private const string Issuer = "https://issuer.example";
    private const string AllowedGroup = "nodepilot-users";
    private const string ReturnUrlItem = "nodepilot:return_url";

    [Fact]
    public async Task Callback_InvalidExternalTicket_AuditsLoginFailure()
    {
        await using var db = TestDbFactory.Create();
        var fixture = new Fixture(db);
        fixture.Authentication
            .Setup(service => service.AuthenticateAsync(
                It.IsAny<HttpContext>(),
                AuthenticationSetup.OidcExternalSchemeName))
            .ReturnsAsync(AuthenticateResult.Fail("invalid external ticket"));

        var result = await fixture.Controller().Callback(CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/login?oidcError=authentication_failed");
        fixture.Audit.Calls.Should().ContainSingle(call =>
            call.Action == AuditActions.LoginFailed
            && call.Details!.Contains("\"reason\":\"oidc_external_ticket_invalid\""));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LoginAndCallback_OidcNotActive_Return404(bool activeAtBoot, bool enabledInOptions)
    {
        await using var db = TestDbFactory.Create();
        var fixture = new Fixture(db, activeAtBoot, enabledInOptions);
        var controller = fixture.Controller();

        controller.Login("/workflows").Should().BeOfType<NotFoundResult>();
        (await controller.Callback(CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        fixture.Audit.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/workflows/42?tab=runs", "/workflows/42?tab=runs")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/path", "/")]
    [InlineData(null, "/")]
    public async Task Login_ChallengesOidcAndKeepsOnlyALocalReturnUrl(string? requested, string expected)
    {
        await using var db = TestDbFactory.Create();
        var controller = new Fixture(db).Controller(callbackUrl: "https://nodepilot.example/api/auth/oidc/callback");

        var result = controller.Login(requested);

        var challenge = result.Should().BeOfType<ChallengeResult>().Subject;
        challenge.AuthenticationSchemes.Should().Equal(AuthenticationSetup.OidcChallengeSchemeName);
        challenge.Properties!.RedirectUri.Should().Be("https://nodepilot.example/api/auth/oidc/callback");
        challenge.Properties.Items[ReturnUrlItem].Should().Be(expected);
    }

    [Fact]
    public async Task Login_WithoutRoutableCallback_FallsBackToTheFixedCallbackPath()
    {
        await using var db = TestDbFactory.Create();

        var result = new Fixture(db).Controller(callbackUrl: null).Login(null);

        result.Should().BeOfType<ChallengeResult>()
            .Which.Properties!.RedirectUri.Should().Be("/api/auth/oidc/callback");
    }

    [Theory]
    [InlineData("/executions?status=failed", "/executions?status=failed")]
    [InlineData("https://evil.example/", "/")]
    [InlineData(null, "/")]
    public async Task Callback_MappedPrincipal_IssuesSessionAndRedirectsToLocalReturnUrl(string? stored, string expected)
    {
        await using var db = TestDbFactory.Create();
        var fixture = new Fixture(db);
        var properties = new AuthenticationProperties();
        if (stored is not null) properties.Items[ReturnUrlItem] = stored;
        fixture.ExternalTicket(Principal("subject-1", "alice@example.test", AllowedGroup), properties);
        User? issuedFor = null;
        fixture.SessionIssuer
            .Setup(issuer => issuer.IssueAsync(It.IsAny<User>(), AuthSource.Oidc, It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()))
            .Callback<User, AuthSource, HttpContext, CancellationToken>((user, _, _, _) => issuedFor = user);

        var result = await fixture.Controller().Callback(CancellationToken.None);

        result.Should().BeOfType<LocalRedirectResult>().Which.Url.Should().Be(expected);
        issuedFor.Should().NotBeNull();
        issuedFor!.Provider.Should().Be(AuthProvider.Oidc);
        issuedFor.Username.Should().Be("alice@example.test");
        fixture.VerifyExternalSignOut(Times.Once());
        fixture.Audit.Calls.Should().NotContain(call => call.Action == AuditActions.LoginFailed);
    }

    [Fact]
    public async Task Callback_PrincipalWithoutAssignedGroup_IsRefusedWithoutSession()
    {
        await using var db = TestDbFactory.Create();
        var fixture = new Fixture(db);
        fixture.ExternalTicket(Principal("subject-2", "bob@example.test", "unrelated-group"), new AuthenticationProperties());

        var result = await fixture.Controller().Callback(CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/login?oidcError=access_not_assigned");
        fixture.SessionIssuer.Verify(issuer => issuer.IssueAsync(
            It.IsAny<User>(), It.IsAny<AuthSource>(), It.IsAny<HttpContext>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.VerifyExternalSignOut(Times.Once());
        fixture.Audit.Calls.Should().ContainSingle(call => call.Action == AuditActions.LoginFailed
            && call.Details!.Contains("\"source\":\"Oidc\""));
        db.Users.Should().NotContain(user => user.Username == "bob@example.test");
    }

    private static ClaimsPrincipal Principal(string subject, string username, params string[] groups)
    {
        var claims = new List<Claim>
        {
            new("iss", Issuer),
            new("sub", subject),
            new("iat", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("preferred_username", username),
        };
        claims.AddRange(groups.Select(group => new Claim("groups", group)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc"));
    }

    private sealed class Fixture
    {
        private readonly NodePilotDbContext _db;
        private readonly IOptions<EnterpriseOidcOptions> _options;
        private readonly ActiveAuthenticationConfiguration _active;

        public Fixture(NodePilotDbContext db, bool activeAtBoot = true, bool enabledInOptions = true)
        {
            _db = db;
            // JIT provisioning refuses to run before a local break-glass administrator exists.
            db.Users.Add(new User
            {
                Id = Guid.NewGuid(), Username = "recovery-admin", Provider = AuthProvider.Local,
                PasswordHash = "hash", Role = UserRole.Admin, IsActive = true, IsBreakGlass = true,
            });
            db.SaveChanges();
            _options = Options.Create(new EnterpriseOidcOptions
            {
                Enabled = enabledInOptions,
                Authority = Issuer,
                ClientId = "nodepilot",
                AllowedGroupIds = [AllowedGroup],
            });
            _active = new ActiveAuthenticationConfiguration(
                LocalLoginMode.Enabled, false, false, activeAtBoot, "Single Sign-On");
        }

        public Mock<IAuthenticationService> Authentication { get; } = new();
        public Mock<IAuthSessionIssuer> SessionIssuer { get; } = new();
        public CapturingAuditWriter Audit { get; } = new();

        public void ExternalTicket(ClaimsPrincipal principal, AuthenticationProperties properties)
            => Authentication
                .Setup(service => service.AuthenticateAsync(
                    It.IsAny<HttpContext>(), AuthenticationSetup.OidcExternalSchemeName))
                .ReturnsAsync(AuthenticateResult.Success(new AuthenticationTicket(
                    principal, properties, AuthenticationSetup.OidcExternalSchemeName)));

        public void VerifyExternalSignOut(Times times)
            => Authentication.Verify(service => service.SignOutAsync(
                It.IsAny<HttpContext>(), AuthenticationSetup.OidcExternalSchemeName,
                It.IsAny<AuthenticationProperties?>()), times);

        public OidcAuthController Controller(string? callbackUrl = "https://nodepilot.example/api/auth/oidc/callback")
        {
            var httpContext = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection()
                    .AddSingleton(Authentication.Object)
                    .BuildServiceProvider(),
            };
            var actionContext = new ActionContext(httpContext, new RouteData(), new ControllerActionDescriptor());
            return new OidcAuthController(
                _options,
                _active,
                new OidcIdentityMapper(_db, _options, NullLogger<OidcIdentityMapper>.Instance),
                SessionIssuer.Object,
                Audit)
            {
                ControllerContext = new ControllerContext(actionContext),
                Url = new FixedCallbackUrlHelper(actionContext, callbackUrl),
            };
        }
    }

    /// <summary>Real local-URL checks from <see cref="UrlHelperBase"/>; action URLs are fixed
    /// because a unit test has no route table.</summary>
    private sealed class FixedCallbackUrlHelper(ActionContext context, string? callbackUrl) : UrlHelperBase(context)
    {
        public override string? Action(UrlActionContext actionContext) => callbackUrl;

        public override string? RouteUrl(UrlRouteContext routeContext) => null;
    }
}

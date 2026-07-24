using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Hosting;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Audit;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class OidcAuthControllerTests
{
    [Fact]
    public async Task Callback_InvalidExternalTicket_AuditsLoginFailure()
    {
        await using var db = TestDbFactory.Create();
        var oidc = Options.Create(new EnterpriseOidcOptions
        {
            Enabled = true,
            Authority = "https://issuer.example",
            ClientId = "nodepilot",
        });
        var authentication = new Mock<IAuthenticationService>();
        authentication
            .Setup(service => service.AuthenticateAsync(
                It.IsAny<HttpContext>(),
                AuthenticationSetup.OidcExternalSchemeName))
            .ReturnsAsync(AuthenticateResult.Fail("invalid external ticket"));
        var services = new ServiceCollection()
            .AddSingleton(authentication.Object)
            .BuildServiceProvider();
        var audit = new CapturingAuditWriter();
        var controller = new OidcAuthController(
            oidc,
            new ActiveAuthenticationConfiguration(
                LocalLoginMode.Enabled, false, false, true, "Single Sign-On"),
            new OidcIdentityMapper(db, oidc, NullLogger<OidcIdentityMapper>.Instance),
            new Mock<IAuthSessionIssuer>().Object,
            audit)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services },
            },
        };

        var result = await controller.Callback(CancellationToken.None);

        result.Should().BeOfType<RedirectResult>()
            .Which.Url.Should().Be("/login?oidcError=authentication_failed");
        audit.Calls.Should().ContainSingle(call =>
            call.Action == AuditActions.LoginFailed
            && call.Details!.Contains("\"reason\":\"oidc_external_ticket_invalid\""));
    }
}

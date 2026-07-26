using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Scheme registration in <see cref="AuthenticationSetup.AddNodePilotAuthentication"/>. The
/// enterprise schemes are opt-in and must stay absent unless explicitly enabled — a stray
/// Negotiate or OIDC handler would change the challenge behaviour of every endpoint that does
/// not pin its scheme. Where a scheme is enabled, the security-relevant option values (PKCE,
/// HTTPS metadata, cookie policy, scope set) are asserted rather than just its presence.
/// </summary>
public sealed class AuthenticationSetupSchemeTests
{
    private const string Authority = "https://idp.example.test/tenant";

    [Fact]
    public void WithoutEnterpriseOptions_OnlyTheDefaultSchemesAreRegistered()
    {
        var provider = Build([]);
        var schemes = Schemes(provider);

        schemes.Should().NotContain(AuthenticationSetup.WindowsAuthSchemeName);
        schemes.Should().NotContain(AuthenticationSetup.OidcChallengeSchemeName);
        schemes.Should().NotContain(AuthenticationSetup.OidcExternalSchemeName);
    }

    [Fact]
    public void WindowsAuthEnabled_RegistersTheNamedNegotiateScheme()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Authentication:Windows:Enabled"] = "true",
        });

        Schemes(provider).Should().Contain(AuthenticationSetup.WindowsAuthSchemeName,
            "the endpoint opts in via [Authorize(AuthenticationSchemes = ...)], the default stays JWT");
    }

    [Fact]
    public void OidcDisabled_LeavesTheOidcSchemesUnregistered()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Enabled"] = "false",
            ["Authentication:Oidc:Authority"] = Authority,
            ["Authentication:Oidc:ClientId"] = "nodepilot",
            ["Authentication:Oidc:ClientSecret"] = "secret",
        });

        Schemes(provider).Should().NotContain(AuthenticationSetup.OidcChallengeSchemeName);
    }

    [Fact]
    public void OidcEnabled_RegistersBothTheChallengeAndTheExternalCookieScheme()
    {
        var provider = Build(OidcEnabled());
        var schemes = Schemes(provider);

        schemes.Should().Contain(AuthenticationSetup.OidcChallengeSchemeName);
        schemes.Should().Contain(AuthenticationSetup.OidcExternalSchemeName);
    }

    [Fact]
    public void OidcEnabled_UsesAuthorizationCodeWithPkceOverHttpsMetadata()
    {
        var options = Oidc(Build(OidcEnabled()));

        options.ResponseType.Should().Be("code");
        options.UsePkce.Should().BeTrue();
        options.RequireHttpsMetadata.Should().BeTrue();
        options.Authority.Should().Be(Authority);
        options.ClientId.Should().Be("nodepilot");
    }

    [Fact]
    public void OidcEnabled_ValidatesIssuerAndAudienceAndDoesNotRemapInboundClaims()
    {
        var options = Oidc(Build(OidcEnabled()));

        options.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        options.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        options.MapInboundClaims.Should().BeFalse(
            "claim names must stay as the IdP issued them so OidcIdentityMapper sees the raw set");
    }

    [Fact]
    public void OidcEnabled_WithoutConfiguredScopes_FallsBackToTheOpenIdProfileEmailSet()
    {
        var options = Oidc(Build(OidcEnabled()));

        options.Scope.Should().BeEquivalentTo("openid", "profile", "email");
    }

    [Fact]
    public void OidcEnabled_ConfiguredScopes_ReplaceTheDefaultsAndAlwaysIncludeOpenId()
    {
        var config = OidcEnabled();
        config["Authentication:Oidc:Scopes:0"] = "profile";
        config["Authentication:Oidc:Scopes:1"] = "groups";

        var options = Oidc(Build(config));

        options.Scope.Should().Contain("groups");
        options.Scope.Should().Contain("openid", "the openid scope is mandatory and is appended");
        options.Scope.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void OidcEnabled_CustomNameClaimType_IsApplied()
    {
        var config = OidcEnabled();
        config["Authentication:Oidc:NameClaimType"] = "upn";

        Oidc(Build(config)).TokenValidationParameters.NameClaimType.Should().Be("upn");
    }

    [Fact]
    public void OidcEnabled_DefaultNameClaimType_IsPreferredUsername()
    {
        Oidc(Build(OidcEnabled())).TokenValidationParameters.NameClaimType
            .Should().Be("preferred_username");
    }

    [Fact]
    public void OidcEnabled_TemporaryCookieIsHttpOnlySecureAndShortLived()
    {
        var provider = Build(OidcEnabled());
        var cookie = provider
            .GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
            .Get(AuthenticationSetup.OidcExternalSchemeName);

        cookie.Cookie.HttpOnly.Should().BeTrue();
        cookie.Cookie.SecurePolicy.Should().Be(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always);
        cookie.ExpireTimeSpan.Should().Be(TimeSpan.FromMinutes(5),
            "the external cookie only bridges the redirect round-trip");
        cookie.SlidingExpiration.Should().BeFalse();
    }

    // ---------------------------------------------------------------- helpers

    private static Dictionary<string, string?> OidcEnabled() => new()
    {
        ["Authentication:Oidc:Enabled"] = "true",
        ["Authentication:Oidc:Authority"] = Authority,
        ["Authentication:Oidc:ClientId"] = "nodepilot",
        ["Authentication:Oidc:ClientSecret"] = "secret",
    };

    private static OpenIdConnectOptions Oidc(IServiceProvider provider) => provider
        .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
        .Get(AuthenticationSetup.OidcChallengeSchemeName);

    private static IReadOnlyList<string> Schemes(IServiceProvider provider) => provider
        .GetRequiredService<IOptions<AuthenticationOptions>>()
        .Value.Schemes.Select(scheme => scheme.Name).ToList();

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var root = Directory.CreateTempSubdirectory("np-auth-setup").FullName;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNodePilotAuthentication(configuration, new StubEnvironment(root));
        return services.BuildServiceProvider();
    }

    private sealed class StubEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "NodePilot.Api";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Development";
    }
}

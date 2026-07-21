using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using NodePilot.Api.Configuration.Validators;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

public sealed class AuthenticationBootValidatorTests
{
    [Fact]
    public void EnabledLdap_RequiresTlsEndpointAndAdmissionGroup()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Authentication:Ldap:Enabled"] = "true",
            ["Authentication:Ldap:UseSsl"] = "false",
            ["Authentication:Ldap:BaseDn"] = "DC=example,DC=test",
            ["Authentication:Ldap:UpnSuffix"] = "example.test",
        });
        var issues = Validate(config);

        issues.Should().Contain(i => i.ConfigKey == "Authentication:Ldap:UseSsl");
        issues.Should().Contain(i => i.ConfigKey == "Authentication:Ldap:Endpoints");
        issues.Should().Contain(i => i.ConfigKey == "Authentication:Ldap:AllowedGroupSids");
    }

    [Fact]
    public void KerberosOnlyWindowsSso_RequiresHostPolicyAttestation()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Authentication:Windows:Enabled"] = "true",
            ["Authentication:Windows:AllowNtlmFallback"] = "false",
            ["Authentication:Windows:NtlmDisabledByPolicy"] = "false",
        });

        Validate(config).Should().ContainSingle(i =>
            i.ConfigKey == "Authentication:Windows:NtlmDisabledByPolicy");
    }

    [Fact]
    public void WindowsOnlySso_StillRequiresLdapsDirectorySyncConfiguration()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Authentication:Windows:Enabled"] = "true",
            ["Authentication:Windows:NtlmDisabledByPolicy"] = "true",
            ["Authentication:Ldap:AllowedGroupSids:0"] = "S-1-5-21-111-222-333-1001",
        });

        var issues = Validate(config);

        issues.Should().Contain(i => i.ConfigKey == "Authentication:Ldap:Endpoints");
        issues.Should().Contain(i => i.ConfigKey == "Authentication:Ldap:BaseDn");
        issues.Should().Contain(i => i.ConfigKey == "Authentication:Ldap:ServiceBindDn");
    }

    [Fact]
    public void EnterpriseAuthenticationConfiguration_IsAccepted()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Authentication:LocalLoginMode"] = "BreakGlassOnly",
            ["Authentication:SessionAbsoluteLifetimeHours"] = "8",
            ["Authentication:MaxAuthorizationStalenessMinutes"] = "15",
            ["Authentication:Ldap:Enabled"] = "true",
            ["Authentication:Ldap:UseSsl"] = "true",
            ["Authentication:Ldap:Endpoints:0"] = "dc01.example.test",
            ["Authentication:Ldap:Endpoints:1"] = "dc02.example.test",
            ["Authentication:Ldap:BaseDn"] = "DC=example,DC=test",
            ["Authentication:Ldap:UpnSuffix"] = "example.test",
            ["Authentication:Ldap:AllowedGroupSids:0"] = "S-1-5-21-111-222-333-1001",
            ["Authentication:Ldap:DirectorySyncIntervalMinutes"] = "5",
            ["Authentication:Ldap:ServiceBindDn"] = "CN=nodepilot,OU=Service Accounts,DC=example,DC=test",
            ["Authentication:Ldap:ServicePassword"] = "test-only-secret",
            ["Authentication:Windows:Enabled"] = "true",
            ["Authentication:Windows:AllowNtlmFallback"] = "false",
            ["Authentication:Windows:NtlmDisabledByPolicy"] = "true",
        });

        Validate(config).Should().BeEmpty();
    }

    [Fact]
    public void OidcAndScim_RequireSecureConfiguration()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Enabled"] = "true",
            ["Authentication:Oidc:Authority"] = "http://idp.example.test",
            ["Authentication:Scim:Enabled"] = "true",
            ["Authentication:Scim:BearerToken"] = "short",
        });
        var issues = Validate(config);

        issues.Should().Contain(i => i.ConfigKey == "Authentication:Oidc:Authority");
        issues.Should().Contain(i => i.ConfigKey == "Authentication:Oidc:ClientId");
        issues.Should().Contain(i => i.ConfigKey == "Authentication:Oidc:AllowedGroupIds");
        issues.Should().Contain(i => i.ConfigKey == "Authentication:Scim:BearerToken");
    }

    [Fact]
    public void HaOidc_RequiresSharedCertificateProtectedDataProtectionKeyRing()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Authentication:Oidc:Enabled"] = "true",
            ["Authentication:Oidc:Authority"] = "https://idp.example.test/tenant",
            ["Authentication:Oidc:ClientId"] = "nodepilot",
            ["Authentication:Oidc:ClientSecret"] = "test-secret",
            ["Authentication:Oidc:AllowedGroupIds:0"] = "nodepilot-users",
        });

        var issues = Validate(config);

        issues.Should().Contain(i => i.ConfigKey == "DataProtection:KeyRingPath");
        issues.Should().Contain(i => i.ConfigKey == "DataProtection:CertificateThumbprint");
        issues.Should().Contain(i => i.ConfigKey == "DataProtection:SharedKeyRing");
    }

    [Fact]
    public void Scim_RequiresOidcAndTheExactSameIssuerIdentifier()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Enabled"] = "true",
            ["Authentication:Oidc:Authority"] = "https://idp.example.test/tenant",
            ["Authentication:Oidc:ClientId"] = "nodepilot",
            ["Authentication:Oidc:ClientSecret"] = "test-secret",
            ["Authentication:Oidc:AllowedGroupIds:0"] = "nodepilot-users",
            ["Authentication:Scim:Enabled"] = "true",
            ["Authentication:Scim:BearerToken"] = new string('s', 32),
            ["Authentication:Scim:Authority"] = "https://idp.example.test/tenant/",
        });

        Validate(config).Should().Contain(i =>
            i.ConfigKey == "Authentication:Scim:Authority"
            && i.Message.Contains("exactly match", StringComparison.Ordinal));
    }

    [Fact]
    public void ScimWithoutOidc_IsRejected()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Authentication:Scim:Enabled"] = "true",
            ["Authentication:Scim:BearerToken"] = new string('s', 32),
            ["Authentication:Scim:Authority"] = "https://idp.example.test/tenant",
        });

        Validate(config).Should().Contain(i => i.ConfigKey == "Authentication:Oidc:Enabled");
    }

    [Fact]
    public void ScimPreviousToken_MustBeStrongAndDifferentFromCurrentToken()
    {
        var token = new string('s', 32);
        var weakPrevious = Config(new Dictionary<string, string?>
        {
            ["Authentication:Scim:Enabled"] = "true",
            ["Authentication:Scim:BearerToken"] = token,
            ["Authentication:Scim:PreviousBearerToken"] = "weak",
        });
        Validate(weakPrevious).Should().Contain(i =>
            i.ConfigKey == "Authentication:Scim:PreviousBearerToken"
            && i.Message.Contains("32", StringComparison.Ordinal));

        var duplicatePrevious = Config(new Dictionary<string, string?>
        {
            ["Authentication:Scim:Enabled"] = "true",
            ["Authentication:Scim:BearerToken"] = token,
            ["Authentication:Scim:PreviousBearerToken"] = token,
        });
        Validate(duplicatePrevious).Should().Contain(i =>
            i.ConfigKey == "Authentication:Scim:PreviousBearerToken"
            && i.Message.Contains("differ", StringComparison.Ordinal));
    }

    [Fact]
    public void AuthorizationFreshnessAndSyncCadence_CannotExceedEnterpriseBounds()
    {
        var config = Config(new Dictionary<string, string?>
        {
            ["Authentication:MaxAuthorizationStalenessMinutes"] = "16",
            ["Authentication:Ldap:Enabled"] = "true",
            ["Authentication:Ldap:DirectorySyncIntervalMinutes"] = "6",
            ["Authentication:Ldap:AllowLocalUserAutoLink"] = "true",
        });

        var issues = Validate(config);

        issues.Should().Contain(i => i.ConfigKey == "Authentication:MaxAuthorizationStalenessMinutes");
        issues.Should().Contain(i => i.ConfigKey == "Authentication:Ldap:DirectorySyncIntervalMinutes");
        issues.Should().Contain(i => i.ConfigKey == "Authentication:Ldap:AllowLocalUserAutoLink");
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static List<BootValidationIssue> Validate(IConfiguration config)
    {
        var result = new List<BootValidationIssue>();
        new AuthenticationBootValidator().Validate(config, result);
        return result;
    }
}

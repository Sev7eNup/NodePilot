using NodePilot.Api.Security;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Security.Oidc;

namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Fails startup and Settings saves for authentication configurations that would expose
/// credentials, silently accept NTLM, or leave an enterprise deployment without an
/// explicit admission policy.
/// </summary>
public sealed class AuthenticationBootValidator : IBootValidator
{
    public string Name => "Authentication";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        ValidateLocalMode(configuration, issues);
        ValidateLdap(configuration, issues);
        ValidateWindows(configuration, issues);
        ValidateOidc(configuration, issues);
        ValidateScim(configuration, issues);
    }

    private void ValidateLocalMode(IConfiguration config, IList<BootValidationIssue> issues)
    {
        var raw = config["Authentication:LocalLoginMode"];
        if (!string.IsNullOrWhiteSpace(raw)
            && !Enum.TryParse<LocalLoginMode>(raw, ignoreCase: true, out _))
        {
            Error(issues, "Authentication:LocalLoginMode",
                "must be Disabled, BreakGlassOnly, or Enabled.");
        }

        ValidateRange(config, issues, "Authentication:SessionAbsoluteLifetimeHours", 1, 168, 8);
        ValidateRange(config, issues, "Authentication:MaxAuthorizationStalenessMinutes", 1, 15, 15);
    }

    private void ValidateLdap(IConfiguration config, IList<BootValidationIssue> issues)
    {
        var ldapEnabled = config.GetValue<bool>("Authentication:Ldap:Enabled");
        var windowsEnabled = config.GetValue<bool>("Authentication:Windows:Enabled");
        if (!ldapEnabled && !windowsEnabled) return;

        if (!config.GetValue("Authentication:Ldap:UseSsl", true))
            Error(issues, "Authentication:Ldap:UseSsl", "must be true when LDAP authentication is enabled; plaintext simple bind is not supported for enterprise deployments.");

        var endpoints = config.GetSection("Authentication:Ldap:Endpoints").Get<string[]>() ?? [];
        var legacyServer = config["Authentication:Ldap:Server"];
        if (endpoints.All(string.IsNullOrWhiteSpace) && string.IsNullOrWhiteSpace(legacyServer))
            Error(issues, "Authentication:Ldap:Endpoints", "at least one LDAP endpoint is required when LDAP is enabled.");
        else
        {
            try
            {
                LdapEndpoint.Resolve(
                    endpoints,
                    legacyServer,
                    config.GetValue("Authentication:Ldap:Port", 636));
            }
            catch (LdapInfrastructureException ex)
            {
                Error(issues, "Authentication:Ldap:Endpoints", ex.Message);
            }
        }

        Required(config, issues, "Authentication:Ldap:BaseDn");
        if (ldapEnabled)
            Required(config, issues, "Authentication:Ldap:UpnSuffix");

        var serviceDn = config["Authentication:Ldap:ServiceBindDn"];
        var servicePassword = config["Authentication:Ldap:ServicePassword"];
        if (string.IsNullOrWhiteSpace(serviceDn) != string.IsNullOrWhiteSpace(servicePassword))
            Error(issues, "Authentication:Ldap:ServiceBindDn", "service bind DN and password must either both be configured or both be empty.");
        else if (string.IsNullOrWhiteSpace(serviceDn))
            Error(issues, "Authentication:Ldap:ServiceBindDn", "service-bind credentials are required for background group sync and deprovisioning.");

        var allowedGroups = config.GetSection("Authentication:Ldap:AllowedGroupSids").Get<string[]>() ?? [];
        if (allowedGroups.Length == 0)
            Error(issues, "Authentication:Ldap:AllowedGroupSids", "at least one allowed AD group SID is required; unrestricted domain-wide JIT access is disabled.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in allowedGroups)
        {
            if (string.IsNullOrWhiteSpace(sid) || !TryParseSid(sid))
                Error(issues, "Authentication:Ldap:AllowedGroupSids", $"'{sid}' is not a valid Windows SID.");
            else if (!seen.Add(sid))
                Error(issues, "Authentication:Ldap:AllowedGroupSids", $"duplicate SID '{sid}'.");
        }

        ValidateRange(config, issues, "Authentication:Ldap:DirectorySyncIntervalMinutes", 1, 5, 5);
        ValidateRange(config, issues, "Authentication:Ldap:DirectorySyncMaxConcurrency", 1, 32, 16);

        if (config.GetValue<bool>("Authentication:Ldap:AllowLocalUserAutoLink"))
            Error(issues, "Authentication:Ldap:AllowLocalUserAutoLink",
                "must be false; existing users are never merged automatically.");
    }

    private void ValidateWindows(IConfiguration config, IList<BootValidationIssue> issues)
    {
        if (!config.GetValue<bool>("Authentication:Windows:Enabled")) return;

        if (!config.GetValue<bool>("Authentication:Ldap:Enabled")
            && !(config.GetSection("Authentication:Ldap:AllowedGroupSids").Get<string[]>() ?? []).Any())
        {
            Error(issues, "Authentication:Ldap:AllowedGroupSids",
                "at least one allowed AD group SID is required for Windows SSO.");
        }

        if (config.GetValue<bool>("Authentication:Windows:AllowNtlmFallback"))
            Error(issues, "Authentication:Windows:AllowNtlmFallback", "must be false; enterprise Windows SSO is Kerberos-only.");
        if (!config.GetValue<bool>("Authentication:Windows:NtlmDisabledByPolicy"))
            Error(issues, "Authentication:Windows:NtlmDisabledByPolicy",
                "must attest that incoming NTLM is denied by host/domain policy; WindowsIdentity.AuthenticationType cannot enforce this reliably in application code.");
    }

    private void ValidateOidc(IConfiguration config, IList<BootValidationIssue> issues)
    {
        if (!config.GetValue<bool>("Authentication:Oidc:Enabled")) return;
        var authority = config["Authentication:Oidc:Authority"];
        if (!OidcIdentityMapper.IsValidIssuer(authority))
            Error(issues, "Authentication:Oidc:Authority", "must be an absolute HTTPS issuer URL.");
        Required(config, issues, "Authentication:Oidc:ClientId");
        Required(config, issues, "Authentication:Oidc:ClientSecret");
        if (!(config.GetSection("Authentication:Oidc:AllowedGroupIds").Get<string[]>() ?? []).Any())
            Error(issues, "Authentication:Oidc:AllowedGroupIds", "at least one allowed OIDC group id is required.");
        if (config.GetValue<bool>("Cluster:Enabled"))
        {
            Required(config, issues, "DataProtection:KeyRingPath");
            Required(config, issues, "DataProtection:CertificateThumbprint");
            if (!config.GetValue<bool>("DataProtection:SharedKeyRing"))
                Error(issues, "DataProtection:SharedKeyRing",
                    "must attest that every HA node uses the same key-ring path for OIDC correlation, nonce and ticket protection.");
        }
    }

    private void ValidateScim(IConfiguration config, IList<BootValidationIssue> issues)
    {
        if (!config.GetValue<bool>("Authentication:Scim:Enabled")) return;
        var token = config["Authentication:Scim:BearerToken"];
        if (string.IsNullOrWhiteSpace(token) || token.Length is < 32 or > 4096)
            Error(issues, "Authentication:Scim:BearerToken", "must contain between 32 and 4096 characters when SCIM is enabled.");
        var previousToken = config["Authentication:Scim:PreviousBearerToken"];
        if (!string.IsNullOrEmpty(previousToken) && previousToken.Length is < 32 or > 4096)
            Error(issues, "Authentication:Scim:PreviousBearerToken", "must contain between 32 and 4096 characters when configured.");
        if (!string.IsNullOrEmpty(token)
            && !string.IsNullOrEmpty(previousToken)
            && string.Equals(token, previousToken, StringComparison.Ordinal))
            Error(issues, "Authentication:Scim:PreviousBearerToken", "must differ from Authentication:Scim:BearerToken.");

        var oidcEnabled = config.GetValue<bool>("Authentication:Oidc:Enabled");
        var oidcAuthority = config["Authentication:Oidc:Authority"];
        var configuredScimAuthority = config["Authentication:Scim:Authority"];
        var scimAuthority = string.IsNullOrWhiteSpace(configuredScimAuthority)
            ? oidcAuthority
            : configuredScimAuthority;
        if (!oidcEnabled)
            Error(issues, "Authentication:Oidc:Enabled",
                "must be true when SCIM is enabled; SCIM identities and memberships are bound to the OIDC issuer.");
        if (!OidcIdentityMapper.IsValidIssuer(scimAuthority))
            Error(issues, "Authentication:Scim:Authority", "must be an absolute HTTPS issuer URL.");
        else if (!string.Equals(scimAuthority, oidcAuthority, StringComparison.Ordinal))
            Error(issues, "Authentication:Scim:Authority",
                "must exactly match Authentication:Oidc:Authority, including path, case, and trailing slash.");
    }

    private void ValidateRange(IConfiguration config, IList<BootValidationIssue> issues, string key, int min, int max, int fallback)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (!int.TryParse(raw, out var value) || value < min || value > max)
            Error(issues, key, $"must be between {min} and {max} (default {fallback}).");
    }

    private void Required(IConfiguration config, IList<BootValidationIssue> issues, string key)
    {
        if (string.IsNullOrWhiteSpace(config[key])) Error(issues, key, "is required when this authentication method is enabled.");
    }

    private void Error(IList<BootValidationIssue> issues, string key, string message) =>
        issues.Add(new BootValidationIssue(Name, BootValidationSeverity.Error, key, message));

    private static bool TryParseSid(string value)
    {
        var parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !parts[0].Equals("S", StringComparison.OrdinalIgnoreCase)) return false;
        if (!byte.TryParse(parts[1], out var revision) || revision != 1) return false;
        if (!ulong.TryParse(parts[2], out _)) return false;
        return parts.Skip(3).All(p => uint.TryParse(p, out _));
    }
}

using System.ComponentModel.DataAnnotations;
using NodePilot.Core.Enums;
using NodePilot.Api.Security;
using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Security.Oidc;

namespace NodePilot.Api.Dtos.Settings;

/// <summary>
/// Authentication section DTO. Combines the LDAP and Windows-Negotiate sub-options
/// behind one logical "Authentication" tab so the operator sees both paths together
/// (they're complementary — Windows-SSO uses the same JIT-mapping table that LDAP
/// fills in). Mirrors <c>LdapOptions</c> + <c>WindowsAuthOptions</c> on the server.
///
/// <para>LDAP, OIDC and SCIM credentials use the same masked SecretField sentinel
/// handling as SMTP/LLM passwords.</para>
/// </summary>
public sealed class AuthenticationSettingsDto : IValidatableObject
{
    public LocalLoginMode LocalLoginMode { get; set; } = LocalLoginMode.BreakGlassOnly;

    [Range(1, 168)]
    public int SessionAbsoluteLifetimeHours { get; set; } = 8;

    [Range(1, 15)]
    public int MaxAuthorizationStalenessMinutes { get; set; } = 15;

    [Required]
    public LdapAuthenticationDto Ldap { get; set; } = new();
    [Required]
    public WindowsAuthenticationDto Windows { get; set; } = new();
    [Required]
    public OidcAuthenticationDto Oidc { get; set; } = new();
    [Required]
    public ScimAuthenticationDto Scim { get; set; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // DataAnnotations don't recurse into nested objects — delegate explicitly.
        foreach (var r in ValidateChild(Ldap, nameof(Ldap))) yield return r;
        foreach (var r in ValidateChild(Windows, nameof(Windows))) yield return r;
        foreach (var r in ValidateChild(Oidc, nameof(Oidc))) yield return r;
        foreach (var r in ValidateChild(Scim, nameof(Scim))) yield return r;

        // Cross-field guard: enabling LDAP requires the four connection essentials.
        // Catching this here surfaces a clean 400 instead of a runtime auth failure
        // on the first login attempt.
        if (Ldap.Enabled || Windows.Enabled)
        {
            if (Ldap.Endpoints.All(string.IsNullOrWhiteSpace) && string.IsNullOrWhiteSpace(Ldap.Server))
                yield return new ValidationResult("At least one LDAPS endpoint is required for LDAP or Windows SSO.", new[] { "Ldap.Endpoints" });
            else
            {
                string? endpointError = null;
                try
                {
                    LdapEndpoint.Resolve(Ldap.Endpoints, Ldap.Server, Ldap.Port);
                }
                catch (LdapInfrastructureException ex)
                {
                    endpointError = ex.Message;
                }
                if (endpointError is not null)
                    yield return new ValidationResult(endpointError, new[] { "Ldap.Endpoints" });
            }
            if (!Ldap.UseSsl)
                yield return new ValidationResult("LDAPS is required for LDAP or Windows SSO.", new[] { "Ldap.UseSsl" });
            if (string.IsNullOrWhiteSpace(Ldap.BaseDn))
                yield return new ValidationResult("Ldap.BaseDn is required for LDAP or Windows SSO.", new[] { "Ldap.BaseDn" });
            if (Ldap.AllowedGroupSids.Count == 0)
                yield return new ValidationResult("At least one allowed AD group SID is required.", new[] { "Ldap.AllowedGroupSids" });
            if (string.IsNullOrWhiteSpace(Ldap.ServiceBindDn) != string.IsNullOrWhiteSpace(Ldap.ServicePassword))
                yield return new ValidationResult("Service bind DN and password must both be configured or both be empty.", new[] { "Ldap.ServiceBindDn", "Ldap.ServicePassword" });
            else if (string.IsNullOrWhiteSpace(Ldap.ServiceBindDn))
                yield return new ValidationResult("Service bind credentials are required for directory sync and deprovisioning.", new[] { "Ldap.ServiceBindDn", "Ldap.ServicePassword" });

            var seenSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sid in Ldap.AllowedGroupSids)
            {
                if (!TryParseSid(sid))
                    yield return new ValidationResult($"'{sid}' is not a valid Windows SID.", new[] { "Ldap.AllowedGroupSids" });
                else if (!seenSids.Add(sid))
                    yield return new ValidationResult($"Duplicate SID '{sid}'.", new[] { "Ldap.AllowedGroupSids" });
            }
        }

        if (Ldap.Enabled && string.IsNullOrWhiteSpace(Ldap.UpnSuffix))
            yield return new ValidationResult("Ldap.UpnSuffix is required when Ldap.Enabled=true.", new[] { "Ldap.UpnSuffix" });

        if (Windows.Enabled)
        {
            if (Ldap.AllowedGroupSids.Count == 0)
                yield return new ValidationResult("At least one allowed AD group SID is required for Windows SSO.", new[] { "Ldap.AllowedGroupSids" });
            if (Windows.AllowNtlmFallback)
                yield return new ValidationResult("Enterprise Windows SSO is Kerberos-only.", new[] { "Windows.AllowNtlmFallback" });
            if (!Windows.NtlmDisabledByPolicy)
                yield return new ValidationResult("Confirm that NTLM is disabled by host/domain policy.", new[] { "Windows.NtlmDisabledByPolicy" });
        }

        if (Oidc.Enabled)
        {
            if (!OidcIdentityMapper.IsValidIssuer(Oidc.Authority))
                yield return new ValidationResult("OIDC authority must be an absolute HTTPS issuer URL.", new[] { "Oidc.Authority" });
            if (string.IsNullOrWhiteSpace(Oidc.ClientId))
                yield return new ValidationResult("OIDC client id is required.", new[] { "Oidc.ClientId" });
            if (string.IsNullOrWhiteSpace(Oidc.ClientSecret))
                yield return new ValidationResult("OIDC client secret is required.", new[] { "Oidc.ClientSecret" });
            if (Oidc.AllowedGroupIds.Count == 0)
                yield return new ValidationResult("At least one OIDC allowed group id is required.", new[] { "Oidc.AllowedGroupIds" });
        }

        if (Scim.Enabled
            && Scim.BearerToken != "__unchanged__"
            && (string.IsNullOrWhiteSpace(Scim.BearerToken) || Scim.BearerToken.Length is < 32 or > 4096))
            yield return new ValidationResult("SCIM bearer token must contain between 32 and 4096 characters.", new[] { "Scim.BearerToken" });
        if (!string.IsNullOrEmpty(Scim.PreviousBearerToken)
            && Scim.PreviousBearerToken != "__unchanged__"
            && Scim.PreviousBearerToken.Length is < 32 or > 4096)
            yield return new ValidationResult("Previous SCIM bearer token must contain between 32 and 4096 characters.", new[] { "Scim.PreviousBearerToken" });
        if (Scim.BearerToken is not null
            && Scim.PreviousBearerToken is not null
            && Scim.BearerToken != "__unchanged__"
            && Scim.PreviousBearerToken != "__unchanged__"
            && string.Equals(Scim.BearerToken, Scim.PreviousBearerToken, StringComparison.Ordinal))
            yield return new ValidationResult("Current and previous SCIM bearer tokens must differ.", new[] { "Scim.BearerToken", "Scim.PreviousBearerToken" });
        if (Scim.Enabled)
        {
            if (!Oidc.Enabled)
                yield return new ValidationResult("OIDC must be enabled when SCIM is enabled.", new[] { "Oidc.Enabled", "Scim.Enabled" });
            var scimAuthority = string.IsNullOrWhiteSpace(Scim.Authority)
                ? Oidc.Authority
                : Scim.Authority;
            if (!OidcIdentityMapper.IsValidIssuer(scimAuthority))
                yield return new ValidationResult("SCIM authority must be an absolute HTTPS issuer URL.", new[] { "Scim.Authority" });
            else if (!string.Equals(scimAuthority, Oidc.Authority, StringComparison.Ordinal))
                yield return new ValidationResult(
                    "SCIM authority must exactly match OIDC authority, including trailing slash and case.",
                    new[] { "Scim.Authority", "Oidc.Authority" });
        }
    }

    private static IEnumerable<ValidationResult> ValidateChild(object child, string prefix)
    {
        var ctx = new ValidationContext(child);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(child, ctx, results, validateAllProperties: true);
        foreach (var r in results)
            yield return new ValidationResult(r.ErrorMessage, r.MemberNames.Select(m => $"{prefix}.{m}"));
    }

    private static bool TryParseSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 4
               && parts[0].Equals("S", StringComparison.OrdinalIgnoreCase)
               && byte.TryParse(parts[1], out var revision)
               && revision == 1
               && ulong.TryParse(parts[2], out _)
               && parts.Skip(3).All(part => uint.TryParse(part, out _));
    }
}

public sealed class LdapAuthenticationDto
{
    public bool Enabled { get; set; }

    [StringLength(255)]
    public string? Server { get; set; }

    public List<string> Endpoints { get; set; } = new();

    [Range(1, 65535)]
    public int Port { get; set; } = 636;

    public bool UseSsl { get; set; } = true;

    [StringLength(500)]
    public string? BaseDn { get; set; }

    [StringLength(255)]
    public string? UpnSuffix { get; set; }

    [Range(1, 5)]
    public int BindTimeoutSeconds { get; set; } = 5;

    [StringLength(500)]
    public string? ServiceBindDn { get; set; }

    /// <summary>
    /// Read response: <c>"********"</c> when set, <c>null</c> otherwise.
    /// Write request: <c>"__unchanged__"</c> keeps it, plaintext rotates, null clears.
    /// </summary>
    public string? ServicePassword { get; set; }

    public List<string> AllowedGroupSids { get; set; } = new();

    [Range(1, 5)]
    public int DirectorySyncIntervalMinutes { get; set; } = 5;

    [Range(1, 32)]
    public int DirectorySyncMaxConcurrency { get; set; } = 16;

    /// <summary>AD-Group-SID → global UserRole mapping table.</summary>
    public List<GlobalRoleMappingDto> GlobalRoleMappings { get; set; } = new();

    /// <summary>Optional default folder grant for JIT-provisioned users. Null = no auto-grant.</summary>
    public SharedFolderRole? JitUserDefaultRootRole { get; set; }
}

public sealed class GlobalRoleMappingDto
{
    [Required(AllowEmptyStrings = false)]
    [StringLength(184)] // Max length of a Windows SID
    public string GroupSid { get; set; } = "";

    // Role is a value-type enum — [Required] on it doesn't add anything meaningful
    // (enums always have a value, just not necessarily a named one). The deserialiser
    // defaults to Viewer if the JSON omits or misnames the field.
    public UserRole Role { get; set; } = UserRole.Viewer;
}

public sealed class WindowsAuthenticationDto
{
    public bool Enabled { get; set; }
    public bool AllowNtlmFallback { get; set; }
    public bool NtlmDisabledByPolicy { get; set; }
}

public sealed class OidcAuthenticationDto
{
    public bool Enabled { get; set; }
    [StringLength(384)] public string? Authority { get; set; }
    [StringLength(256)] public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    [StringLength(100)] public string DisplayName { get; set; } = "Single Sign-On";
    [StringLength(100)] public string NameClaimType { get; set; } = "preferred_username";
    [StringLength(100)] public string GroupsClaimType { get; set; } = "groups";
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];
    public List<string> AllowedGroupIds { get; set; } = new();
    public List<OidcRoleMappingDto> GlobalRoleMappings { get; set; } = new();
}

public sealed class OidcRoleMappingDto
{
    [Required, StringLength(256)] public string GroupId { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Viewer;
}

public sealed class ScimAuthenticationDto
{
    public bool Enabled { get; set; }
    public string? BearerToken { get; set; }
    public string? PreviousBearerToken { get; set; }
    [StringLength(384)] public string? Authority { get; set; }
}

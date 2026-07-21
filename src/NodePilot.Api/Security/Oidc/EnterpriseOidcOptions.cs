using NodePilot.Core.Enums;

namespace NodePilot.Api.Security.Oidc;

public sealed class EnterpriseOidcOptions
{
    public const string SectionName = "Authentication:Oidc";

    public bool Enabled { get; set; }
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string DisplayName { get; set; } = "Single Sign-On";
    public string NameClaimType { get; set; } = "preferred_username";
    public string GroupsClaimType { get; set; } = "groups";
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
    public string[] AllowedGroupIds { get; set; } = [];
    public List<OidcRoleMapping> GlobalRoleMappings { get; set; } = [];
}

public sealed class OidcRoleMapping
{
    public string GroupId { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Viewer;
}

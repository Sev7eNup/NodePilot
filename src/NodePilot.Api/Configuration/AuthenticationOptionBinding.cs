using NodePilot.Api.Security.Ldap;
using NodePilot.Api.Security.Oidc;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Configuration;

internal static class AuthenticationOptionBinding
{
    internal static LdapOptions ReadLdap(IConfiguration configuration)
    {
        var options = configuration.GetSection(LdapOptions.SectionName).Get<LdapOptions>() ?? new();
        ReplaceLists(configuration, options);
        return options;
    }

    internal static EnterpriseOidcOptions ReadOidc(IConfiguration configuration)
    {
        var options = configuration.GetSection(EnterpriseOidcOptions.SectionName).Get<EnterpriseOidcOptions>() ?? new();
        ReplaceLists(configuration, options);
        return options;
    }

    internal static void ReplaceLists(IConfiguration configuration, LdapOptions options)
    {
        options.AllowedGroupSids = ProviderAtomicList.Read<string>(configuration, "Authentication:Ldap:AllowedGroupSids") ?? [];
        options.GlobalRoleMappings = ProviderAtomicList.Read<GlobalRoleMapping>(configuration, "Authentication:Ldap:GlobalRoleMappings") ?? [];
    }

    internal static void ReplaceLists(IConfiguration configuration, EnterpriseOidcOptions options)
    {
        options.AllowedGroupIds = (ProviderAtomicList.Read<string>(configuration, "Authentication:Oidc:AllowedGroupIds") ?? []).ToArray();
        options.GlobalRoleMappings = ProviderAtomicList.Read<OidcRoleMapping>(configuration, "Authentication:Oidc:GlobalRoleMappings") ?? [];
        options.Scopes = (ProviderAtomicList.Read<string>(configuration, "Authentication:Oidc:Scopes") ?? ["openid", "profile", "email"]).ToArray();
    }
}

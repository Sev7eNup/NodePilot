using NodePilot.Api.Configuration;

namespace NodePilot.Api.Security.Ldap;

/// <summary>Immutable startup snapshot for restart-required AD authentication settings.</summary>
public sealed record ActiveDirectoryAuthenticationConfiguration(
    LdapOptions Ldap,
    WindowsAuthOptions Windows)
{
    public bool DirectorySyncEnabled => Ldap.Enabled || Windows.Enabled;

    public static ActiveDirectoryAuthenticationConfiguration From(IConfiguration configuration) => new(
        AuthenticationOptionBinding.ReadLdap(configuration),
        configuration.GetSection(WindowsAuthOptions.SectionName).Get<WindowsAuthOptions>() ?? new WindowsAuthOptions());
}

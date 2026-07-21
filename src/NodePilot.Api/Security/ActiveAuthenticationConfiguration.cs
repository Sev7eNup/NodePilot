namespace NodePilot.Api.Security;

/// <summary>
/// Immutable snapshot of authentication methods that were registered at process start.
/// Runtime override files may reload, but scheme enablement only changes after restart;
/// discovery and endpoints use this snapshot so they never advertise half-active config.
/// </summary>
public sealed record ActiveAuthenticationConfiguration(
    LocalLoginMode LocalLoginMode,
    bool LdapEnabled,
    bool WindowsEnabled,
    bool OidcEnabled,
    string OidcDisplayName)
{
    public static ActiveAuthenticationConfiguration From(IConfiguration configuration)
    {
        var mode = Enum.TryParse<LocalLoginMode>(
            configuration["Authentication:LocalLoginMode"], true, out var parsed)
            ? parsed
            : LocalLoginMode.BreakGlassOnly;
        return new ActiveAuthenticationConfiguration(
            mode,
            configuration.GetValue<bool>("Authentication:Ldap:Enabled"),
            configuration.GetValue<bool>("Authentication:Windows:Enabled"),
            configuration.GetValue<bool>("Authentication:Oidc:Enabled"),
            configuration["Authentication:Oidc:DisplayName"] ?? "Single Sign-On");
    }
}

using NodePilot.Core.Enums;

namespace NodePilot.Api.Security.Ldap;

/// <summary>
/// Configuration for the LDAP-bind authentication path. All keys live under
/// <c>Authentication:Ldap:*</c>. Default <see cref="Enabled"/>=false — existing
/// deployments keep working unchanged; LDAP only becomes active once the operator
/// explicitly turns it on and supplies a reachable domain-controller configuration.
/// </summary>
public sealed class LdapOptions
{
    public const string SectionName = "Authentication:Ldap";

    /// <summary>Master switch. Default false. Turns the entire LDAP path on.</summary>
    public bool Enabled { get; set; } = false;

    // ---- Connection ----
    public string? Server { get; set; }
    /// <summary>
    /// Ordered LDAP endpoints used for failover. Entries may be host names or host:port;
    /// the legacy Server/Port pair is used only when this list is empty.
    /// </summary>
    public List<string> Endpoints { get; set; } = new();
    public int Port { get; set; } = 636;

    /// <summary>
    /// Must stay <c>true</c>: the adapter refuses a plaintext simple-bind and the boot
    /// validator rejects <c>false</c> for enabled deployments. The flag exists so the
    /// requirement is visible/attestable in configuration, not as an opt-out.
    /// </summary>
    public bool UseSsl { get; set; } = true;

    public string? BaseDn { get; set; }

    /// <summary>UPN suffix used for the simple-bind. <c>sam</c> becomes
    /// <c>sam@firma.de</c>. Required in every enabled setup.</summary>
    public string? UpnSuffix { get; set; }

    /// <summary>
    /// Bound for both bind- and search-operations. Sets <see cref="System.DirectoryServices.Protocols.LdapConnection.Timeout"/> —
    /// the underlying system library doesn't distinguish between a connect-timeout and an
    /// operation-timeout, so there is no separate <c>ConnectTimeoutSeconds</c> here: a
    /// dedicated knob for it would only document a setting that has no real effect.
    /// </summary>
    public int BindTimeoutSeconds { get; set; } = 5;

    // ---- Optional Service-Bind for Group-SID-resolution ----
    /// <summary>Optional service-account DN used for the memberOf-to-SID roundtrip when
    /// the user's own bind connection isn't allowed to see all of their groups.</summary>
    public string? ServiceBindDn { get; set; }
    public string? ServicePassword { get; set; }

    /// <summary>Only members of at least one listed AD group may be JIT-provisioned.</summary>
    public List<string> AllowedGroupSids { get; set; } = new();

    /// <summary>Background refresh cadence for provisioned external identities.</summary>
    public int DirectorySyncIntervalMinutes { get; set; } = 5;

    /// <summary>Maximum parallel service-bind lookups in one sync pass.</summary>
    public int DirectorySyncMaxConcurrency { get; set; } = 16;

    // ---- Global Role Mappings (AD-Group-SID -> global UserRole) ----
    /// <summary>
    /// Maps AD-group SIDs to a global <see cref="UserRole"/>. Highest matching role
    /// wins: a user who is in both <c>Domain Admins</c> and an <c>Operator</c> group
    /// logs in as Admin. No matching mapping row = the user gets the global
    /// <see cref="UserRole.Viewer"/> role.
    /// </summary>
    public List<GlobalRoleMapping> GlobalRoleMappings { get; set; } = new();

    // ---- Default folder grant for JIT-provisioned users (security default: none) ----
    /// <summary>
    /// Optional default folder role that a user who was JIT-provisioned via LDAP inherits
    /// on Root. Default <c>null</c> = no automatic grant; all permissions come from
    /// AD-group mappings or explicit admin grants. Small/legacy installations can set this
    /// to <c>FolderViewer</c> so every domain user at least gets read access to whatever
    /// sits in Root — deliberately opt-in.
    /// </summary>
    public SharedFolderRole? JitUserDefaultRootRole { get; set; }
}

/// <summary>One row from <see cref="LdapOptions.GlobalRoleMappings"/>.</summary>
public sealed class GlobalRoleMapping
{
    /// <summary>SID of an AD group, in the domain format <c>S-1-5-21-...</c>.</summary>
    public string GroupSid { get; set; } = string.Empty;

    /// <summary>Global role that users receive when they are a member of this group.</summary>
    public UserRole Role { get; set; } = UserRole.Viewer;
}

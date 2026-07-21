namespace NodePilot.Api.Security.Scim;

public sealed class ScimOptions
{
    public const string SectionName = "Authentication:Scim";

    public bool Enabled { get; set; }
    public string? BearerToken { get; set; }

    /// <summary>
    /// Optional predecessor accepted during a bounded operator-controlled rotation window.
    /// Clear it after the identity provider has switched to <see cref="BearerToken"/>.
    /// </summary>
    public string? PreviousBearerToken { get; set; }

    /// <summary>
    /// Identity authority used to link provisioned SCIM externalId values to OIDC subjects.
    /// Defaults to the configured OIDC issuer.
    /// </summary>
    public string? Authority { get; set; }
}

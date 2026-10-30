namespace NodePilot.Api.Configuration.Validators;

public sealed class AllowedHostsBootValidator : IBootValidator
{
    public string Name => "AllowedHosts";

    internal static bool IsUnsafe(string? hosts)
        => string.IsNullOrWhiteSpace(hosts) || hosts == "*"
           || hosts.StartsWith("{{", StringComparison.Ordinal) && hosts.EndsWith("}}", StringComparison.Ordinal);

    internal static string? GetError(IConfiguration configuration)
        => configuration.GetValue<bool>("Security:StrictAllowedHosts") && IsUnsafe(configuration["AllowedHosts"])
            ? "Security:StrictAllowedHosts=true and AllowedHosts is unsafe. Set AllowedHosts to a concrete FQDN (semicolon-separated for multiple)."
            : null;

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        if (GetError(configuration) is { } error)
            issues.Add(new(Name, BootValidationSeverity.Error, "AllowedHosts", error));
    }
}

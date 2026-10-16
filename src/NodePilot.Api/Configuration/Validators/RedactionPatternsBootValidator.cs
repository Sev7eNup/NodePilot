using NodePilot.Engine.Security;

namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Validates <c>Logging:Redaction:Patterns</c> at boot. A pattern that does not compile would
/// otherwise disable the masking the operator configured; this reports every broken entry at once.
/// </summary>
public sealed class RedactionPatternsBootValidator : IBootValidator
{
    public string Name => "RedactionPatterns";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        foreach (var (key, message) in OutputRedactor.FindInvalidCustomPatterns(configuration))
        {
            issues.Add(new BootValidationIssue(
                Name, BootValidationSeverity.Error, key,
                $"not a valid regular expression ({message}). Secrets this pattern should mask would stay visible."));
        }
    }
}

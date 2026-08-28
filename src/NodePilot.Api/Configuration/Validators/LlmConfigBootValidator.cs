using NodePilot.Ai;

namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Mirrors the SSRF / cloud-metadata block in
/// <see cref="LlmServiceCollectionExtensions.AddNodePilotAi"/> so the Settings UI
/// cannot persist an LLM configuration that would break the next service start.
///
/// <para>A save with <c>Llm:Enabled=true</c> and <c>Llm:BaseUrl=http://169.254.169.254/</c>
/// would otherwise reach <c>appsettings.runtime.json</c> and fail only on the next restart
/// with <c>SECURITY: Llm:BaseUrl …</c>, leaving the service unable to boot until the override
/// file is edited by hand. Evaluating the same rule against the merged post-save config
/// rejects it with 400 BadRequest before the file is written.</para>
/// </summary>
public sealed class LlmConfigBootValidator : IBootValidator
{
    public string Name => "LlmConfig";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        if (!bool.TryParse(configuration["Llm:Enabled"], out var enabled) || !enabled)
            return; // AddNodePilotAi skips the check as well when Llm:Enabled=false.

        // Runs the same helper as AddNodePilotAi, so a save this validator accepts cannot produce
        // a configuration that refuses to boot. Every profile is checked, not just the active one:
        // switching the active profile is a plain settings save without a restart, so a metadata
        // endpoint parked in an inactive profile would only take effect on the switch.
        foreach (var issue in LlmProfileValidation.ValidateProfileEndpoints(configuration))
            issues.Add(new BootValidationIssue(Name, BootValidationSeverity.Error, issue.ConfigKey, issue.Message));

        // Same deal for Llm:Proxy:*. A "Custom" mode without an address builds no proxy, so the
        // first LLM call after a restart would fail on a value the save could have rejected.
        foreach (var issue in LlmProfileValidation.ValidateProxy(configuration))
            issues.Add(new BootValidationIssue(Name, BootValidationSeverity.Error, issue.ConfigKey, issue.Message));

        // Deliberately a Warning, not an Error: the AI features are opt-in, and a half-finished
        // profile setup must not keep the service from booting. The endpoints answer
        // 503 LLM_NO_ACTIVE_PROFILE instead.
        if (!LlmProfileValidation.HasResolvableActiveProfile(configuration))
        {
            issues.Add(new BootValidationIssue(
                Name, BootValidationSeverity.Warning, "Llm:ActiveProfileId",
                "Llm:Enabled=true but no active LLM profile resolves. Every AI endpoint will answer "
                + "503 LLM_NO_ACTIVE_PROFILE until a profile is added and selected under "
                + "Settings → System → Integrations → LLM."));
        }
    }
}

using NodePilot.Ai;

namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Mirrors the SSRF / cloud-metadata block in
/// <see cref="LlmServiceCollectionExtensions.AddNodePilotAi"/> so the Settings UI
/// cannot persist an LLM configuration that would break the next service start.
///
/// <para>Without this validator, an admin save with
/// <c>Llm:Enabled=true</c> + <c>Llm:BaseUrl=http://169.254.169.254/</c> would pass the
/// existing boot-validator pipeline, get written to <c>appsettings.runtime.json</c>,
/// and only fail on the next restart with <c>SECURITY: Llm:BaseUrl …</c> — at which
/// point the service can't boot and the operator has to hand-edit the override file.
/// This validator closes that loop: the same rule, evaluated against the merged
/// post-save config, surfaces the same error as a 400 BadRequest BEFORE the file is
/// written.</para>
/// </summary>
public sealed class LlmConfigBootValidator : IBootValidator
{
    public string Name => "LlmConfig";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        if (!bool.TryParse(configuration["Llm:Enabled"], out var enabled) || !enabled)
            return; // Llm:Enabled=false → AddNodePilotAi skips the check too; stay consistent.

        // Exactly the helper AddNodePilotAi calls, so a save this validator accepts can never
        // produce a configuration that refuses to boot. Covers EVERY profile, not just the active
        // one: switching the active profile is a plain settings save with no restart, so a parked
        // metadata endpoint would only detonate on the switch.
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

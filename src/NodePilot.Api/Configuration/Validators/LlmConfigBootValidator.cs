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

        var baseUrl = configuration["Llm:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) return;

        if (LlmEndpointGuard.IsCloudMetadataEndpoint(baseUrl))
        {
            issues.Add(new BootValidationIssue(
                Name, BootValidationSeverity.Error, "Llm:BaseUrl",
                $"'{baseUrl}' points at a cloud-metadata endpoint (169.254.0.0/16, " +
                "metadata.google.internal, metadata.azure.com). This range is always blocked. " +
                "Choose a real LLM endpoint or disable Llm:Enabled."));
        }
    }
}

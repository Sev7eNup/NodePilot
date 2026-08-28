namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Rejects an unrecognized <c>Deployment:Mode</c> value at boot with a clear message.
/// <see cref="DeploymentModeReader.IsDesktop"/> fails safe by reading an unknown value as
/// Server, so a typo never weakens the posture, but it would silently ignore the configured
/// intent. This validator reports it as a boot error instead.
/// </summary>
public sealed class DeploymentModeBootValidator : IBootValidator
{
    public string Name => "DeploymentMode";

    public void Validate(IConfiguration configuration, IList<BootValidationIssue> issues)
    {
        if (DeploymentModeReader.IsRecognized(configuration))
            return;

        issues.Add(new BootValidationIssue(
            Name, BootValidationSeverity.Error, DeploymentModeReader.Key,
            $"Deployment:Mode '{configuration[DeploymentModeReader.Key]}' is not supported. " +
            "Allowed values: 'Server' (default) or 'Desktop'."));
    }
}

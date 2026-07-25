namespace NodePilot.Api.Configuration.Validators;

/// <summary>
/// Rejects an unrecognized <c>Deployment:Mode</c> value at boot with a clear message.
/// <see cref="DeploymentModeReader.IsDesktop"/> itself fails safe (unknown → Server), so a
/// typo never silently weakens the posture — but it would silently ignore the operator's
/// intent, so this validator surfaces it as a boot Error instead.
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

namespace NodePilot.Api.Configuration;

/// <summary>
/// A pre-flight check that runs against the final <see cref="IConfiguration"/> right
/// before <c>app.Build()</c>. Each validator inspects a slice of the configuration
/// and adds <see cref="BootValidationIssue"/>s for any inconsistencies; the
/// <see cref="BootValidatorRunner"/> aggregates them and decides whether to throw.
///
/// <para>The shared abstraction serves two purposes:</para>
///
/// <list type="number">
///   <item>The Admin Settings API runs the same checks when saving a section override,
///   so the rules for what a save accepts and what a boot accepts cannot drift apart.</item>
///   <item>All validators run in one pass, so a single boot reports every configuration
///   fix the operator needs instead of only the first one.</item>
/// </list>
/// </summary>
public interface IBootValidator
{
    /// <summary>Stable identifier for this validator, used in error messages and logs.</summary>
    string Name { get; }

    /// <summary>
    /// Inspect <paramref name="configuration"/> and append any problems to
    /// <paramref name="issues"/>. Must not throw for validation problems. An exception
    /// here means the validator itself is broken and is treated as a bug, not as a
    /// configuration problem.
    /// </summary>
    void Validate(IConfiguration configuration, IList<BootValidationIssue> issues);
}

/// <summary>
/// A single validation finding. Errors fail the boot; warnings are logged but don't
/// stop the host from starting. <see cref="ConfigKey"/> is optional but should be set
/// whenever the finding maps to a specific configuration key, so the Settings UI can
/// surface the error inline on the matching input field.
/// </summary>
/// <param name="ValidatorName">Which validator raised this finding.</param>
/// <param name="Severity">Error fails the boot or rejects the save; Warning is logged only.</param>
/// <param name="ConfigKey">Optional configuration key (e.g. <c>"Cluster:NodeId"</c>) the issue is
/// about.</param>
/// <param name="Message">Human-readable description, including how to fix the issue.</param>
public sealed record BootValidationIssue(
    string ValidatorName,
    BootValidationSeverity Severity,
    string? ConfigKey,
    string Message);

public enum BootValidationSeverity
{
    Warning,
    Error,
}

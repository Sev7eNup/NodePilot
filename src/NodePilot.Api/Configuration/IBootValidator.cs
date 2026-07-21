namespace NodePilot.Api.Configuration;

/// <summary>
/// A pre-flight check that runs against the final <see cref="IConfiguration"/> right
/// before <c>app.Build()</c>. Each validator inspects a slice of the configuration
/// and adds <see cref="BootValidationIssue"/>s for any inconsistencies — the
/// <see cref="BootValidatorRunner"/> aggregates them and decides whether to throw.
///
/// <para><b>Why this exists:</b> Boot-time validation used to live as imperative
/// throws scattered through <c>Program.cs</c> and the <c>Hosting/</c> folder
/// (<c>ClusterConfigValidator.Validate</c>, <c>SecretProtectorRegistry</c>-internal
/// checks, etc.). Two problems with that model:</para>
///
/// <list type="number">
///   <item>The Admin Settings API needs the SAME checks during a Save — validate that
///   the merged config (existing + new section override) would still let the service
///   boot. Without a reusable abstraction we'd duplicate the rules and inevitably
///   drift between "what Save accepts" and "what Boot accepts".</item>
///   <item>Aggregation: a single boot today fails on the FIRST imperative throw, so
///   the operator fixes one key, restarts, fails on the next, fixes that, restarts,
///   etc. Validators that all run in one pass surface every fix needed up front.</item>
/// </list>
/// </summary>
public interface IBootValidator
{
    /// <summary>Stable identifier for this validator, used in error messages and logs.</summary>
    string Name { get; }

    /// <summary>
    /// Inspect <paramref name="configuration"/> and append any problems to
    /// <paramref name="issues"/>. Must NOT throw on validation problems — throwing is
    /// reserved for the validator itself being broken (NRE, etc.) and is treated as a
    /// bug, not as a configuration problem.
    /// </summary>
    void Validate(IConfiguration configuration, IList<BootValidationIssue> issues);
}

/// <summary>
/// A single validation finding. Errors fail the boot; warnings are logged but don't
/// stop the host from starting. <see cref="ConfigKey"/> is optional but should be set
/// whenever the finding maps to a specific configuration key — it lets the Settings
/// UI surface the error inline on the right input field.
/// </summary>
/// <param name="ValidatorName">Which validator raised this finding.</param>
/// <param name="Severity">Error → boot fails / save rejected; Warning → logged only.</param>
/// <param name="ConfigKey">Optional configuration key (e.g. <c>"Cluster:NodeId"</c>) the issue is about.</param>
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

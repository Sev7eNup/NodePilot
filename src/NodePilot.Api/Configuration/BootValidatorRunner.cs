using System.Reflection;
using System.Text;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Discovers and runs every <see cref="IBootValidator"/> with a parameterless
/// constructor in the NodePilot.Api assembly, aggregates the results, and either
/// throws (any error-severity findings) or logs (warnings only).
///
/// <para>Reflection-based discovery is deliberate: we want adding a new validator
/// to be "drop a class implementing the interface, done" — no DI registration,
/// no manual list to keep in sync. The "parameterless constructor" requirement
/// keeps boot-time validators side-effect-free (no DI, no I/O — pure config
/// inspection) and side-steps the chicken-and-egg with the service provider.</para>
/// </summary>
public static class BootValidatorRunner
{
    /// <summary>
    /// Discover all validators in the API assembly, run each, aggregate findings.
    /// Throws <see cref="InvalidOperationException"/> if any error-severity issues are
    /// reported. Warnings are written to <paramref name="warningLogger"/> (if supplied)
    /// but do not block the boot.
    /// </summary>
    public static IReadOnlyList<BootValidationIssue> RunAll(
        IConfiguration configuration,
        Action<string>? warningLogger = null)
    {
        var validators = DiscoverValidators(typeof(BootValidatorRunner).Assembly);
        return RunAll(configuration, validators, warningLogger);
    }

    /// <summary>
    /// Overload that takes an explicit validator list — used by tests so they can
    /// exercise the runner without depending on the full validator catalog.
    /// </summary>
    public static IReadOnlyList<BootValidationIssue> RunAll(
        IConfiguration configuration,
        IEnumerable<IBootValidator> validators,
        Action<string>? warningLogger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(validators);

        var issues = new List<BootValidationIssue>();
        foreach (var validator in validators)
        {
            validator.Validate(configuration, issues);
        }

        var errors = issues.Where(i => i.Severity == BootValidationSeverity.Error).ToList();
        var warnings = issues.Where(i => i.Severity == BootValidationSeverity.Warning).ToList();

        if (warningLogger is not null)
        {
            foreach (var w in warnings)
                warningLogger($"[boot-validator:{w.ValidatorName}] {w.ConfigKey ?? "(no key)"}: {w.Message}");
        }

        if (errors.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Boot validation failed. Refusing to start until the following configuration issues are fixed:");
            foreach (var e in errors)
            {
                sb.Append("  - [").Append(e.ValidatorName).Append("] ");
                if (e.ConfigKey is not null) sb.Append(e.ConfigKey).Append(": ");
                sb.AppendLine(e.Message);
            }
            throw new InvalidOperationException(sb.ToString());
        }

        return issues;
    }

    /// <summary>
    /// Non-throwing variant — runs the auto-discovered validator catalog and returns
    /// every issue (warnings and errors) without aborting. Used by the Admin Settings
    /// API to surface validation errors as HTTP 400 rather than 500.
    /// </summary>
    public static IReadOnlyList<BootValidationIssue> RunAllSafely(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<BootValidationIssue>();
        foreach (var v in DiscoverValidators(typeof(BootValidatorRunner).Assembly))
        {
            v.Validate(configuration, issues);
        }
        return issues;
    }

    internal static List<IBootValidator> DiscoverValidators(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface)
            .Where(t => typeof(IBootValidator).IsAssignableFrom(t))
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IBootValidator)Activator.CreateInstance(t)!)
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ToList();
    }
}

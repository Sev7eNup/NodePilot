using NodePilot.Core.Configuration;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Detects the hardware NodePilot runs on and builds the process-wide
/// <see cref="PerformancePlan"/>. Lives in Api because it reads
/// <see cref="DeploymentModeReader"/> and <see cref="IConfiguration"/>; the sizing arithmetic
/// itself lives in <see cref="PerformanceSizing"/> in Core, which takes plain values so it stays
/// pure and testable (Core must not reference Api — see the dependency graph in CLAUDE.md).
///
/// <para>The plan is built once at boot and registered as a singleton. It is not re-resolved on
/// configuration reload: the runspace pool and dispatch queue are constructed at startup and
/// cannot be resized in-process. The Settings UI shows the operator's desired mode next to the
/// active plan, and the difference between them drives the restart hint.</para>
/// </summary>
public static class PerformancePlanFactory
{
    /// <summary>Builds the plan from live configuration and the detected hardware.</summary>
    public static PerformancePlan Create(IConfiguration configuration)
        => PerformanceSizing.Create(
            Detect(configuration),
            configuration.GetValue(PerformanceSizing.ConfigKeys.ManualTuning, false),
            ReadConfigured(configuration));

    /// <summary>
    /// Hardware limits as this process can actually use them: both APIs honor container and
    /// Windows job-object limits, so a cgroup-limited container is sized for its own slice,
    /// not for the host it happens to run on.
    /// </summary>
    public static DetectedResources Detect(IConfiguration configuration)
        => new(Environment.ProcessorCount, DetectUsableMemoryBytes(), DeploymentModeReader.IsDesktop(configuration));

    /// <summary>
    /// <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c> is the memory the GC believes it may
    /// use as of the last collection — under a container limit it already reflects that limit.
    /// A value of zero or implausibly small means detection failed; the caller then sizes on
    /// CPU alone instead of trusting a bogus number.
    /// </summary>
    private static long? DetectUsableMemoryBytes()
    {
        try
        {
            var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            return total >= PerformanceSizing.MinPlausibleMemoryBytes ? total : null;
        }
        catch (Exception)
        {
            // Never let hardware detection stop the host from booting — CPU-only sizing is a
            // safe fallback.
            return null;
        }
    }

    private static Dictionary<string, int?> ReadConfigured(IConfiguration configuration)
    {
        var values = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var key in PerformanceSizing.ConfigKeys.All)
            values[key] = configuration.GetValue<int?>(key);
        return values;
    }

    /// <summary>
    /// One-line summary for the startup log, showing what was detected, what was chosen, and
    /// which constraint bound each value.
    /// </summary>
    public static string Describe(PerformancePlan plan)
    {
        var mem = plan.Resources.UsableMemoryBytes is { } b
            ? $"{b / 1024d / 1024d / 1024d:0.#} GB"
            : "unknown (CPU-only sizing)";
        var mode = plan.ManualTuning ? "manual" : "auto";
        return $"Performance sizing [{mode}]: {plan.Resources.ProcessorCount} cores, {mem}, " +
               $"{(plan.Resources.IsDesktop ? "Desktop" : "Server")} posture → " +
               $"runspaces {plan.MinRunspaces.Value}-{plan.MaxRunspaces.Value} ({plan.MaxRunspaces.Bound}), " +
               $"steps {plan.MaxConcurrentSteps.Value} ({plan.MaxConcurrentSteps.Bound}), " +
               $"threads {plan.MinWorkerThreads.Value} ({plan.MinWorkerThreads.Bound}), " +
               $"dispatch {plan.DispatchWorkerCount.Value}/{plan.DispatchCapacity.Value} " +
               $"({plan.DispatchWorkerCount.Bound})";
    }
}

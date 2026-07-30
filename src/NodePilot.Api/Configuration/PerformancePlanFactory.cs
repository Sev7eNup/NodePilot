using NodePilot.Core.Configuration;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Detects the hardware NodePilot is running on and builds the process-wide
/// <see cref="PerformancePlan"/>. Kept in the Api because it reads
/// <see cref="DeploymentModeReader"/> and <see cref="IConfiguration"/>; the sizing arithmetic
/// itself lives in <see cref="PerformanceSizing"/> in Core, which takes plain values so it stays
/// pure and testable (Core must not reference Api — see the dependency graph in CLAUDE.md).
///
/// <para>The plan is built exactly once, during boot, and registered as a singleton. It is
/// deliberately <em>not</em> re-resolved on configuration reload: the runspace pool and the
/// dispatch queue are constructed at startup and cannot be re-sized in-process, so a live
/// re-resolve would leave the hot-reloadable ThreadPool tuned for one mode while everything else
/// still ran in the other. What the operator changes in the Settings UI is the <em>desired</em>
/// mode; the difference to the active plan is what drives the restart hint.</para>
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
    /// Hardware as this process may actually use it — both APIs honour container and Windows
    /// job-object limits, which is the whole point: a cgroup-limited container must be sized for
    /// its slice, not for the host it happens to sit on.
    /// </summary>
    public static DetectedResources Detect(IConfiguration configuration)
        => new(Environment.ProcessorCount, DetectUsableMemoryBytes(), DeploymentModeReader.IsDesktop(configuration));

    /// <summary>
    /// <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c> is the memory the GC believes it may
    /// use, as of the last collection — under a container limit it already reflects that limit
    /// (and may itself be an implementation-defined fraction of it). Anything zero or
    /// implausibly small means detection did not work; the caller then sizes on CPU alone instead
    /// of trusting a bogus number.
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
            // perfectly safe fallback.
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
    /// One-line summary for the startup log. Without this the effective sizing is invisible in
    /// the field — an operator wondering why their box behaves differently needs to see what was
    /// detected, what was chosen, and which constraint bound each value.
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

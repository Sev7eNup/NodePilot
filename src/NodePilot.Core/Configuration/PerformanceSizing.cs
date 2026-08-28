namespace NodePilot.Core.Configuration;

/// <summary>
/// Hardware facts the sizing algorithm reads. Passed in as values so the algorithm stays pure
/// and testable; detection happens in the Api layer, which Core must not reference.
/// </summary>
/// <param name="ProcessorCount">Usable logical processors. Honours container CPU limits.</param>
/// <param name="UsableMemoryBytes">
/// Memory this process may plan with, or <c>null</c> when detection failed or returned an
/// implausible value. The algorithm then sizes on the CPU dimension alone.
/// </param>
/// <param name="IsDesktop">
/// <c>Deployment:Mode=Desktop</c>. The desktop package shares one machine with Postgres, the
/// Electron shell and the user's own applications, so NodePilot claims a smaller share.
/// </param>
public readonly record struct DetectedResources(int ProcessorCount, long? UsableMemoryBytes, bool IsDesktop);

/// <summary>Which constraint produced a value. Shown in the UI and the startup log.</summary>
public enum SizingBound
{
    /// <summary>The CPU-derived formula was the smallest.</summary>
    Cpu,
    /// <summary>The RAM sub-budget was the smallest.</summary>
    Ram,
    /// <summary>Result would have fallen below the minimum viable value.</summary>
    Floor,
    /// <summary>Result would have exceeded the maximum safe value.</summary>
    Ceiling,
    /// <summary>Taken verbatim from configuration because manual tuning is switched on.</summary>
    Manual,
}

/// <summary>One resolved knob: the value plus why it came out that way.</summary>
public readonly record struct SizedValue(int Value, SizingBound Bound)
{
    public static implicit operator int(SizedValue v) => v.Value;
}

/// <summary>
/// The complete, immutable sizing decision for this process. Built once at boot and read by
/// every consumer, so a configuration reload can never leave the hot-reloadable ThreadPool
/// tuned for one mode while the boot-fixed runspace pool and dispatch queue run in the other.
/// </summary>
public sealed record PerformancePlan
{
    public required bool ManualTuning { get; init; }
    public required DetectedResources Resources { get; init; }

    public required SizedValue MinRunspaces { get; init; }
    public required SizedValue MaxRunspaces { get; init; }
    public required SizedValue MaxConcurrentSteps { get; init; }
    public required SizedValue MinWorkerThreads { get; init; }
    public required SizedValue MinIoCompletionThreads { get; init; }
    public required SizedValue DispatchWorkerCount { get; init; }

    // Engine:MaxConcurrentExecutions is deliberately absent. Those values are safety caps
    // against trigger loops and sub-workflow cascades, not throughput levers, so deriving them
    // from hardware would disarm a guard the operator set on purpose. WorkflowEngine reads them
    // straight from configuration.
}

/// <summary>
/// Derives a safe boot-time sizing plan from the detected hardware, or passes the operator's
/// configured values through when manual tuning is switched on.
/// <para>
/// The result is a safe default that scales with the machine, not a universal optimum. The
/// optimum also depends on workflow count, activity mix, step duration, remote latency and DB
/// provider, none of which are knowable at boot, so automatic sizing targets light to moderate
/// load and the manual switch covers everything beyond that. The floors and ceilings below bound
/// the automatic range; see <c>docs/performance-improvements.md</c> for the load profiles.
/// </para>
/// </summary>
public static class PerformanceSizing
{
    // Auto-mode bounds. Floors keep a small machine booting and working; ceilings stop the
    // formulas extrapolating past the range the defaults are validated for.
    internal const int MinRunspacesFloor = 1, MinRunspacesCeiling = 8;
    internal const int MaxRunspacesFloor = 8, MaxRunspacesCeiling = 64;
    internal const int StepsFloor = 32, StepsCeiling = 600;
    internal const int ThreadsFloor = 64, ThreadsCeiling = 768;
    internal const int WorkerCountFloor = 20, WorkerCountCeiling = 200;

    // Share of detected memory NodePilot plans with. Desktop shares the machine with Postgres,
    // the Electron shell and the user's own applications, so it claims noticeably less.
    internal const double ServerMemoryShare = 0.60;
    internal const double DesktopMemoryShare = 0.25;

    // Fixed cost before any tuning takes effect: runtime, EF model, caches, telemetry. Subtracted
    // from the budget so the tunable knobs only get what is left.
    internal const long BaselineBytes = 512L * 1024 * 1024;

    // Sub-budgets of the app budget. They share one pool: sizing each knob against the whole
    // budget independently would spend the same memory several times over.
    internal const double RunspaceShare = 0.50;
    internal const double StepShare = 0.25;
    // The remaining share is headroom for GC slack, spikes and anything not modelled here. The
    // DB pool is deliberately not modelled: its dominant cost is server-side Postgres memory,
    // which no application-side setting controls.

    // Marginal memory cost per unit. Deliberately conservative estimates, so the memory dimension
    // can only ever make the plan smaller than the CPU dimension, never larger.
    internal const long RunspaceCostBytes = 8L * 1024 * 1024;
    internal const long StepCostBytes = 256L * 1024;

    /// <summary>
    /// Below this, memory detection counts as failed rather than merely small: no supported host
    /// runs NodePilot in under 1 GB, so a lower reading is meaningless. Public because the
    /// detecting Api layer applies the same threshold.
    /// </summary>
    public const long MinPlausibleMemoryBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Builds the plan. <paramref name="configured"/> holds the operator's explicit values and is
    /// read only when <paramref name="manualTuning"/> is set; a key missing there falls back to
    /// the automatic value, so a partly filled manual section cannot produce nonsense.
    /// </summary>
    public static PerformancePlan Create(
        DetectedResources resources,
        bool manualTuning,
        IReadOnlyDictionary<string, int?> configured)
    {
        var cores = Math.Max(1, resources.ProcessorCount);
        var budget = AppBudgetBytes(resources);

        // Automatic values first: they double as the fallback for a manual section with gaps.
        var maxRunspaces = Resolve(cores * 4, RamCap(budget, RunspaceShare, RunspaceCostBytes),
            MaxRunspacesFloor, MaxRunspacesCeiling);
        var steps = Resolve(cores * 32, RamCap(budget, StepShare, StepCostBytes),
            StepsFloor, StepsCeiling);
        var threads = Resolve(Math.Max(200, cores * 16), null, ThreadsFloor, ThreadsCeiling);
        var workers = Resolve(cores * 3, null, WorkerCountFloor, WorkerCountCeiling);

        // Always 1 under automatic sizing. RunspacePool.Open() materialises the minimum eagerly
        // while the pool grows on demand under real load, so a larger minimum only holds memory
        // and threads per pool without improving throughput.
        var minRunspaces = Resolve(1, null, MinRunspacesFloor, MinRunspacesCeiling);

        if (!manualTuning)
        {
            return new PerformancePlan
            {
                ManualTuning = false,
                Resources = resources,
                MinRunspaces = minRunspaces,
                MaxRunspaces = maxRunspaces,
                MaxConcurrentSteps = steps,
                MinWorkerThreads = threads,
                MinIoCompletionThreads = threads,
                DispatchWorkerCount = workers,
            };
        }

        return new PerformancePlan
        {
            ManualTuning = true,
            Resources = resources,
            MinRunspaces = Manual(configured, ConfigKeys.MinRunspaces, minRunspaces),
            MaxRunspaces = Manual(configured, ConfigKeys.MaxRunspaces, maxRunspaces),
            MaxConcurrentSteps = Manual(configured, ConfigKeys.MaxConcurrentSteps, steps),
            MinWorkerThreads = Manual(configured, ConfigKeys.MinWorkerThreads, threads),
            MinIoCompletionThreads = Manual(configured, ConfigKeys.MinIoCompletionThreads, threads),
            DispatchWorkerCount = Manual(configured, ConfigKeys.DispatchWorkerCount, workers),
        };
    }

    /// <summary>
    /// Memory the plan may distribute, or 0 when detection failed. The caller then sizes on CPU
    /// alone rather than inventing a number.
    /// </summary>
    internal static long AppBudgetBytes(DetectedResources resources)
    {
        if (resources.UsableMemoryBytes is not { } total || total < MinPlausibleMemoryBytes)
            return 0;

        var share = resources.IsDesktop ? DesktopMemoryShare : ServerMemoryShare;
        var budget = (long)(total * share) - BaselineBytes;
        return budget > 0 ? budget : 0;
    }

    private static int? RamCap(long appBudget, double share, long costPerUnit) =>
        appBudget <= 0 ? null : (int)Math.Min(int.MaxValue, (long)(appBudget * share) / costPerUnit);

    /// <summary>
    /// Smallest of the applicable constraints wins, then floor and ceiling. Floor is applied last
    /// so a viable minimum is always produced, even on hardware below the supported range.
    /// </summary>
    private static SizedValue Resolve(int cpuValue, int? ramValue, int floor, int ceiling)
    {
        var bound = SizingBound.Cpu;
        var value = cpuValue;

        if (ramValue is { } ram && ram < value)
        {
            value = ram;
            bound = SizingBound.Ram;
        }

        if (value > ceiling) return new SizedValue(ceiling, SizingBound.Ceiling);
        if (value < floor) return new SizedValue(floor, SizingBound.Floor);
        return new SizedValue(value, bound);
    }

    private static SizedValue Manual(IReadOnlyDictionary<string, int?> configured, string key, SizedValue auto) =>
        configured.TryGetValue(key, out var v) && v is { } value && value > 0
            ? new SizedValue(value, SizingBound.Manual)
            : auto;

    /// <summary>
    /// Configuration keys manual mode reads. Shared with the Api so the two cannot drift.
    /// </summary>
    public static class ConfigKeys
    {
        public const string ManualTuning = "Performance:ManualTuning";
        public const string MinRunspaces = "Engine:Runspace:MinRunspaces";
        public const string MaxRunspaces = "Engine:Runspace:MaxRunspaces";
        public const string MaxConcurrentSteps = "Engine:MaxConcurrentSteps";
        public const string MinWorkerThreads = "Threading:MinWorkerThreads";
        public const string MinIoCompletionThreads = "Threading:MinIoCompletionThreads";
        public const string DispatchWorkerCount = "ExecutionDispatch:WorkerCount";

        public static readonly string[] All =
        [
            MinRunspaces, MaxRunspaces, MaxConcurrentSteps, MinWorkerThreads, MinIoCompletionThreads,
            DispatchWorkerCount,
        ];
    }
}

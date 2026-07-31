namespace NodePilot.Core.Configuration;

/// <summary>
/// Hardware facts the sizing algorithm is derived from. Passed in as values so the algorithm
/// stays pure and testable: the Api layer detects them (it owns <c>DeploymentModeReader</c>,
/// which Core must not reference — see the dependency graph in CLAUDE.md).
/// </summary>
/// <param name="ProcessorCount">Usable logical processors. Honours container/job CPU limits.</param>
/// <param name="UsableMemoryBytes">
/// Memory this process may plan with, or <c>null</c> when detection failed or returned an
/// implausible value — the algorithm then falls back to the CPU dimension alone.
/// </param>
/// <param name="IsDesktop">
/// <c>Deployment:Mode=Desktop</c>. The desktop package co-locates Postgres, the Electron shell
/// and the user's own applications on one machine, so NodePilot may claim a smaller share.
/// </param>
public readonly record struct DetectedResources(int ProcessorCount, long? UsableMemoryBytes, bool IsDesktop);

/// <summary>Which constraint produced a value — surfaced in the UI and the startup log.</summary>
public enum SizingBound
{
    /// <summary>The CPU-derived formula was the smallest.</summary>
    Cpu,
    /// <summary>The RAM sub-budget was the smallest.</summary>
    Ram,
    /// <summary>Result would have fallen below the minimum viable value.</summary>
    Floor,
    /// <summary>Result would have exceeded what has been measured as safe.</summary>
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
/// The complete, immutable sizing decision for this process. Built exactly once at boot
/// (see the Api's boot snapshot) and read by every consumer, so a configuration reload can
/// never leave the hot-reloadable ThreadPool tuned for one mode while the boot-fixed runspace
/// pool and dispatch queue still run in the other.
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
    public required SizedValue DispatchCapacity { get; init; }

    // Engine:MaxConcurrentExecutions is deliberately absent. Those are safety caps against
    // pathological cases (trigger loops, sub-workflow cascades), not a throughput lever — an
    // operator who sets one means it, and deriving it from hardware would disarm the guard they
    // configured. WorkflowEngine reads them straight from configuration.
}

/// <summary>
/// Derives a safe boot-time sizing plan from the detected hardware, or passes the operator's
/// configured values through when manual tuning is switched on.
///
/// <para><b>What this is and is not.</b> This produces a <i>safe, monotonically scaling default
/// with bounded resource risk</i> — not a universal optimum. The optimum additionally depends on
/// workflow count, activity mix, step duration, remote latency and DB provider, none of which are
/// knowable at boot. The load-profile table in <c>docs/performance-improvements.md</c> is
/// explicitly not a hardware scaling series: its "20 cores / 500 workflows" and
/// "32 cores / 1000 workflows" rows are measured <i>load</i> points. Auto therefore targets the
/// light-to-moderate rows; reaching the measured high-load profile is what the manual switch is
/// for.</para>
///
/// <para><b>Ceilings come from measurements, not taste.</b> The roadmap's Sperrvermerk records
/// that raising <c>MaxConcurrentSteps</c> 600→1500 measured 42% worse and lowering it 600→300
/// measured 9% worse, so 600 is a real optimum rather than a guess. The runspace auto-ceiling of
/// 64 is the "16 cores / 50 workflows" row: the most auto can justify without knowing the load.
/// The measured 768-runspace profile is deliberately left to manual so auto can never silently
/// downgrade it.</para>
/// </summary>
public static class PerformanceSizing
{
    // Auto-mode bounds. Floors keep a 2-core / 4 GB box booting and working; ceilings stop
    // extrapolation past the range that has actually been measured.
    internal const int MinRunspacesFloor = 1, MinRunspacesCeiling = 8;
    internal const int MaxRunspacesFloor = 8, MaxRunspacesCeiling = 64;
    internal const int StepsFloor = 32, StepsCeiling = 600;
    internal const int ThreadsFloor = 64, ThreadsCeiling = 768;
    internal const int WorkerCountFloor = 20, WorkerCountCeiling = 200;
    internal const int CapacityFloor = 128, CapacityCeiling = 2048;

    // Share of detected memory NodePilot plans with. Desktop co-locates Postgres, the Electron
    // shell and the user's own applications, so it claims noticeably less. These are named
    // assumptions, not magic numbers — they are documented in performance-improvements.md.
    internal const double ServerMemoryShare = 0.60;
    internal const double DesktopMemoryShare = 0.25;

    // Fixed cost before any tuning takes effect: runtime, EF model, caches, telemetry. Measured
    // idle footprint is 383-444 MB depending on profile; 512 MB is that rounded up.
    internal const long BaselineBytes = 512L * 1024 * 1024;

    // Sub-budgets of the app budget. One shared household — sizing each knob against the *whole*
    // budget independently would spend the same memory several times over.
    internal const double RunspaceShare = 0.50;
    internal const double StepShare = 0.25;
    internal const double QueueShare = 0.05;
    // Remaining 20% is deliberate headroom: GC slack, spikes, and everything not modelled here.
    // The DB pool is intentionally NOT modelled — its dominant cost is server-side Postgres
    // memory, which no app-side setting controls, and under load only ~85 of 480 pool slots are
    // occupied (roadmap Sperrvermerk). Modelling it would be false precision.

    // Marginal memory costs. PLACEHOLDERS pending the Phase 0 calibration described in the plan:
    // measured 1.2-1.4 MB per pooled runspace in an empty test host versus roughly 8 MB per
    // runspace inclusive during the 500-parallel run — a factor of six. Until the calibration
    // lands, the conservative end of that range is used, and the memory dimension can only ever
    // make the plan *smaller* than the CPU dimension, never larger.
    internal const long RunspaceCostBytes = 8L * 1024 * 1024;
    internal const long StepCostBytes = 256L * 1024;
    internal const long QueueEntryCostBytes = 8L * 1024;

    /// <summary>
    /// Below this, memory detection counts as failed rather than merely small: no supported host
    /// runs NodePilot in under 1 GB, so a lower reading means the platform gave us something
    /// meaningless. Public because the detection side (Api) applies the same threshold.
    /// </summary>
    public const long MinPlausibleMemoryBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>
    /// Builds the plan. <paramref name="configured"/> supplies the operator's explicit values and
    /// is consulted only when <paramref name="manualTuning"/> is set; a key missing there still
    /// falls back to the auto value, so a half-filled manual section cannot produce nonsense.
    /// </summary>
    public static PerformancePlan Create(
        DetectedResources resources,
        bool manualTuning,
        IReadOnlyDictionary<string, int?> configured)
    {
        var cores = Math.Max(1, resources.ProcessorCount);
        var budget = AppBudgetBytes(resources);

        // Auto values first — they double as the fallback for a manual section with gaps.
        var maxRunspaces = Resolve(cores * 4, RamCap(budget, RunspaceShare, RunspaceCostBytes),
            MaxRunspacesFloor, MaxRunspacesCeiling);
        var steps = Resolve(cores * 32, RamCap(budget, StepShare, StepCostBytes),
            StepsFloor, StepsCeiling);
        var threads = Resolve(Math.Max(200, cores * 16), null, ThreadsFloor, ThreadsCeiling);
        var workers = Resolve(cores * 3, null, WorkerCountFloor, WorkerCountCeiling);
        var capacity = Resolve(workers.Value * 8, RamCap(budget, QueueShare, QueueEntryCostBytes),
            CapacityFloor, CapacityCeiling);

        // Always 1 under automatic sizing. RunspacePool.Open() materialises the minimum eagerly,
        // and eager pre-warm is a measured anti-pattern (28% regression, see
        // performance-improvements.md) — the pool grows organically under real load anyway. It is
        // also what the documented light and moderate load rows prescribe. Deriving it from
        // MaxRunspaces instead cost 4-8 runspaces per pool for no benefit; with many pools alive
        // at once that was enough to wedge the test suite at 421 threads.
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
                DispatchCapacity = capacity,
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
            DispatchCapacity = Manual(configured, ConfigKeys.DispatchCapacity, capacity),
        };
    }

    /// <summary>
    /// Memory the plan may distribute, or 0 when detection failed — the caller then sizes on CPU
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
    /// so a viable minimum is always produced, even on hardware below the tested range.
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

    /// <summary>Configuration keys the manual mode reads. Shared with the Api so the two cannot drift.</summary>
    public static class ConfigKeys
    {
        public const string ManualTuning = "Performance:ManualTuning";
        public const string MinRunspaces = "Engine:Runspace:MinRunspaces";
        public const string MaxRunspaces = "Engine:Runspace:MaxRunspaces";
        public const string MaxConcurrentSteps = "Engine:MaxConcurrentSteps";
        public const string MinWorkerThreads = "Threading:MinWorkerThreads";
        public const string MinIoCompletionThreads = "Threading:MinIoCompletionThreads";
        public const string DispatchWorkerCount = "ExecutionDispatch:WorkerCount";
        public const string DispatchCapacity = "ExecutionDispatch:Capacity";

        public static readonly string[] All =
        [
            MinRunspaces, MaxRunspaces, MaxConcurrentSteps, MinWorkerThreads, MinIoCompletionThreads,
            DispatchWorkerCount, DispatchCapacity,
        ];
    }
}

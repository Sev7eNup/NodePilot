using FluentAssertions;
using NodePilot.Core.Configuration;
using Xunit;

namespace NodePilot.Engine.Tests.Configuration;

/// <summary>
/// The sizing algorithm produces a <em>safe, monotonically scaling default with bounded resource
/// risk</em> — not a universal optimum, which CPU and memory alone cannot determine (workflow
/// count, activity mix, step duration and remote latency all matter and are unknown at boot).
/// These tests pin exactly that contract: no double-spending of memory, no extrapolation past the
/// measured range, no silent downgrade of the measured high-load profile.
///
/// Hardware is always injected so results never depend on the machine running the suite.
/// </summary>
public class PerformanceSizingTests
{
    private const long GB = 1024L * 1024 * 1024;
    private static readonly Dictionary<string, int?> NoConfig = new();

    private static PerformancePlan Auto(int cores, double ramGb, bool desktop = false) =>
        PerformanceSizing.Create(
            new DetectedResources(cores, (long)(ramGb * GB), desktop), manualTuning: false, NoConfig);

    // --- The memory budget is a single household, not one per knob -------------------------

    [Theory]
    [InlineData(2, 4)]
    [InlineData(4, 8)]
    [InlineData(8, 16)]
    [InlineData(12, 32)]
    [InlineData(20, 64)]
    [InlineData(32, 8)]
    [InlineData(64, 256)]
    public void Create_Auto_TotalPlannedMemory_StaysWithinAppBudget(int cores, double ramGb)
    {
        var resources = new DetectedResources(cores, (long)(ramGb * GB), IsDesktop: false);
        var plan = PerformanceSizing.Create(resources, manualTuning: false, NoConfig);

        var planned =
            (long)plan.MaxRunspaces.Value * PerformanceSizing.RunspaceCostBytes +
            (long)plan.MaxConcurrentSteps.Value * PerformanceSizing.StepCostBytes;

        var budget = PerformanceSizing.AppBudgetBytes(resources);
        // Floors may legitimately push a tiny box past its share — that is the deliberate
        // "must still boot and work" guarantee. Everything above the floor range must fit.
        if (budget > 0 && plan.MaxRunspaces.Bound != SizingBound.Floor)
            planned.Should().BeLessThanOrEqualTo(budget,
                "sizing each knob against the whole budget independently would spend the same memory several times");
    }

    // --- Reproduces the light/moderate rows of the documented heuristic ---------------------
    // The high-load rows (20 cores/500 WFs, 32 cores/1000 WFs) are deliberately NOT reproduced:
    // they are measured *load* points reachable through manual tuning, not hardware defaults.

    [Fact]
    public void Create_Auto_EightCores_MatchesDocumentedLightLoadRow()
    {
        var plan = Auto(cores: 8, ramGb: 16);

        plan.MaxRunspaces.Value.Should().Be(32, "docs/archived/performance-measurements.md row '8-Core / 20 par. WFs'");
        plan.MaxConcurrentSteps.Value.Should().Be(256, "same row");
        plan.MinRunspaces.Value.Should().Be(1, "same row — the pool grows lazily; eager pre-warm measured a 28% regression");
    }

    [Fact]
    public void Create_Auto_SixteenCores_MatchesDocumentedModerateLoadRow()
    {
        var plan = Auto(cores: 16, ramGb: 32);

        plan.MaxRunspaces.Value.Should().Be(64, "docs row '16-Core / 50 par. WFs'");
        plan.MaxConcurrentSteps.Value.Should().Be(512, "same row");
        plan.MinRunspaces.Value.Should().Be(1, "same row");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(128)]
    public void Create_Auto_NeverPreWarmsRunspaces(int cores)
    {
        // RunspacePool.Open() materialises the minimum immediately, so any auto value above 1
        // is eager pre-warm by another name — measured at a 28% regression, and with many pools
        // alive at once it was enough to wedge the whole test suite.
        Auto(cores, ramGb: 64).MinRunspaces.Value.Should().Be(1);
    }

    // --- Edge cases across the whole plausible hardware space -------------------------------

    [Fact]
    public void Create_Auto_TinyBox_StillProducesWorkableFloors()
    {
        var plan = Auto(cores: 2, ramGb: 4);

        plan.MaxRunspaces.Value.Should().BeGreaterThanOrEqualTo(PerformanceSizing.MaxRunspacesFloor);
        plan.MaxConcurrentSteps.Value.Should().BeGreaterThanOrEqualTo(PerformanceSizing.StepsFloor);
        plan.DispatchWorkerCount.Value.Should().BeGreaterThanOrEqualTo(PerformanceSizing.WorkerCountFloor);
    }

    [Fact]
    public void Create_Auto_ManyCoresLittleMemory_IsBoundByRam()
    {
        // The memory dimension is a safety net for genuinely constrained hosts — a cgroup-limited
        // container, not a modest server. Because the auto ceilings are deliberately conservative
        // (64 runspaces is ~512 MB at the current coefficient), an ordinary 8-16 GB box is bound
        // by CPU or by the ceiling, never by memory. It has to bite where it matters: a 32-core
        // container squeezed into 2 GB must not size itself as if it owned the host.
        var plan = Auto(cores: 32, ramGb: 2);

        plan.MaxRunspaces.Bound.Should().Be(SizingBound.Ram,
            "2 GB cannot host what 32 cores would otherwise justify");
        plan.MaxRunspaces.Value.Should().BeLessThan(PerformanceSizing.MaxRunspacesCeiling);
    }

    [Fact]
    public void Create_Auto_OrdinaryServer_IsNotMemoryBound()
    {
        // Documents the flip side of the above so the safety-net role stays explicit: on normal
        // hardware the memory term must never be the thing that shrinks the plan.
        Auto(cores: 32, ramGb: 8).MaxRunspaces.Bound.Should().NotBe(SizingBound.Ram);
        Auto(cores: 8, ramGb: 16).MaxRunspaces.Bound.Should().NotBe(SizingBound.Ram);
    }

    [Fact]
    public void Create_Auto_FewCoresHugeMemory_IsBoundByCpu()
    {
        var plan = Auto(cores: 4, ramGb: 128);

        plan.MaxRunspaces.Bound.Should().Be(SizingBound.Cpu, "memory is abundant, so the CPU formula binds");
        plan.MaxRunspaces.Value.Should().Be(16);
    }

    [Fact]
    public void Create_Auto_HugeBox_StopsAtMeasuredCeiling_DoesNotExtrapolate()
    {
        var plan = Auto(cores: 128, ramGb: 512);

        plan.MaxConcurrentSteps.Value.Should().Be(PerformanceSizing.StepsCeiling,
            "raising the step cap past 600 measured 42% worse — auto must not extrapolate past the tested range");
        plan.MaxConcurrentSteps.Bound.Should().Be(SizingBound.Ceiling);
        plan.MaxRunspaces.Value.Should().Be(PerformanceSizing.MaxRunspacesCeiling);
    }

    [Fact]
    public void Create_Auto_UndetectableMemory_FallsBackToCpuOnly()
    {
        var plan = PerformanceSizing.Create(
            new DetectedResources(8, UsableMemoryBytes: null, IsDesktop: false), manualTuning: false, NoConfig);

        plan.MaxRunspaces.Value.Should().Be(32, "an unknown memory size must not shrink the plan below the CPU result");
        plan.MaxRunspaces.Bound.Should().Be(SizingBound.Cpu);
    }

    [Fact]
    public void Create_Auto_ImplausiblySmallMemory_IsTreatedAsUndetected()
    {
        // A platform reporting 64 MB is lying, not describing a tiny host — trusting it would
        // collapse every knob onto its floor.
        var plan = PerformanceSizing.Create(
            new DetectedResources(8, 64L * 1024 * 1024, IsDesktop: false), manualTuning: false, NoConfig);

        plan.MaxRunspaces.Bound.Should().Be(SizingBound.Cpu);
    }

    [Fact]
    public void Create_Auto_DesktopPosture_ClaimsLessMemoryThanServer()
    {
        var server = Auto(cores: 8, ramGb: 8);
        var desktop = Auto(cores: 8, ramGb: 8, desktop: true);

        desktop.MaxRunspaces.Value.Should().BeLessThanOrEqualTo(server.MaxRunspaces.Value,
            "a desktop also runs Postgres, the Electron shell and the user's own applications");
    }

    // --- Monotonicity: more hardware never yields a smaller plan ----------------------------

    [Fact]
    public void Create_Auto_IsMonotonicInCores()
    {
        var previous = 0;
        foreach (var cores in new[] { 1, 2, 4, 8, 12, 16, 20, 32, 64, 128 })
        {
            var value = Auto(cores, ramGb: 256).MaxConcurrentSteps.Value;
            value.Should().BeGreaterThanOrEqualTo(previous, "adding cores must never shrink the plan");
            previous = value;
        }
    }

    [Fact]
    public void Create_Auto_IsMonotonicInMemory()
    {
        var previous = 0;
        foreach (var ramGb in new[] { 2, 4, 8, 16, 32, 64, 128, 256 })
        {
            var value = Auto(cores: 32, ramGb).MaxRunspaces.Value;
            value.Should().BeGreaterThanOrEqualTo(previous, "adding memory must never shrink the plan");
            previous = value;
        }
    }

    // --- Manual mode: the measured profile must survive verbatim ----------------------------

    [Fact]
    public void Create_Manual_UsesConfiguredValues_EvenOnSmallHardware()
    {
        // The Sperrvermerk protects this exact profile. Auto never reaches it; switching manual
        // tuning on must reproduce it unchanged, whatever hardware is detected.
        var configured = new Dictionary<string, int?>
        {
            [PerformanceSizing.ConfigKeys.MinRunspaces] = 256,
            [PerformanceSizing.ConfigKeys.MaxRunspaces] = 768,
            [PerformanceSizing.ConfigKeys.MaxConcurrentSteps] = 600,
            [PerformanceSizing.ConfigKeys.MinWorkerThreads] = 768,
            [PerformanceSizing.ConfigKeys.MinIoCompletionThreads] = 768,
            [PerformanceSizing.ConfigKeys.DispatchWorkerCount] = 600,
        };

        var plan = PerformanceSizing.Create(
            new DetectedResources(8, 16 * GB, IsDesktop: false), manualTuning: true, configured);

        plan.MinRunspaces.Value.Should().Be(256);
        plan.MaxRunspaces.Value.Should().Be(768, "manual tuning must never be clipped by the auto ceiling");
        plan.MaxConcurrentSteps.Value.Should().Be(600);
        plan.MinWorkerThreads.Value.Should().Be(768);
        plan.DispatchWorkerCount.Value.Should().Be(600);
        plan.MaxRunspaces.Bound.Should().Be(SizingBound.Manual);
    }

    [Theory]
    [InlineData(4, 600, true)]    // automatic 128: 600 is more than twice that
    [InlineData(4, 256, false)]   // exactly twice is not reported
    [InlineData(8, 600, true)]    // automatic 256: the measured case, 2.3 times
    [InlineData(20, 600, false)]  // the documented high-load profile on the hardware it was measured on
    public void Create_Manual_StepLimitFarAboveAutomatic_IsFlagged(int cores, int configuredSteps, bool expected)
    {
        var configured = new Dictionary<string, int?> { [PerformanceSizing.ConfigKeys.MaxConcurrentSteps] = configuredSteps };

        var plan = PerformanceSizing.Create(
            new DetectedResources(cores, 64 * GB, IsDesktop: false), manualTuning: true, configured);

        plan.AutomaticMaxConcurrentSteps.Should().Be(Math.Min(cores * 32, PerformanceSizing.StepsCeiling));
        plan.StepLimitFarAboveAutomatic.Should().Be(expected);
    }

    [Fact]
    public void Create_Auto_StepLimit_IsNeverFlagged()
    {
        var configured = new Dictionary<string, int?> { [PerformanceSizing.ConfigKeys.MaxConcurrentSteps] = 600 };

        var plan = PerformanceSizing.Create(
            new DetectedResources(4, 8 * GB, IsDesktop: false), manualTuning: false, configured);

        plan.StepLimitFarAboveAutomatic.Should().BeFalse("the stored value is not in force under automatic sizing");
    }

    [Fact]
    public void Create_Manual_MissingKey_FallsBackToAutoValue()
    {
        // A half-filled manual section must not produce a zero-sized pool.
        var configured = new Dictionary<string, int?>
        {
            [PerformanceSizing.ConfigKeys.MaxRunspaces] = 768,
        };

        var plan = PerformanceSizing.Create(
            new DetectedResources(8, 16 * GB, IsDesktop: false), manualTuning: true, configured);

        plan.MaxRunspaces.Value.Should().Be(768);
        plan.MaxConcurrentSteps.Value.Should().Be(256, "an absent key falls back to the hardware-derived value");
        plan.MaxConcurrentSteps.Bound.Should().NotBe(SizingBound.Manual);
    }

    [Fact]
    public void Create_Manual_NonPositiveValue_IsIgnored()
    {
        var configured = new Dictionary<string, int?>
        {
            [PerformanceSizing.ConfigKeys.MaxRunspaces] = 0,
        };

        var plan = PerformanceSizing.Create(
            new DetectedResources(8, 16 * GB, IsDesktop: false), manualTuning: true, configured);

        plan.MaxRunspaces.Value.Should().Be(32, "a zero would disable the pool entirely");
    }

    // --- Script processes: four per core, a share of the memory, never more than the steps --

    [Theory]
    [InlineData(4, 5, 16)]
    [InlineData(8, 16, 32)]
    [InlineData(16, 64, 64)]
    public void Create_Auto_ScriptProcesses_FollowTheCores(int cores, double ramGb, int expected)
    {
        var plan = Auto(cores, ramGb);

        plan.MaxScriptProcesses.Value.Should().Be(expected);
        plan.MaxScriptProcesses.Bound.Should().Be(SizingBound.Cpu);
    }

    [Theory]
    [InlineData(16, 8, 40)]
    [InlineData(32, 16, 81)]
    public void Create_Auto_ScriptProcesses_WithLittleMemory_FollowTheMemoryShare(int cores, double ramGb, int expected)
    {
        var plan = Auto(cores, ramGb);

        plan.MaxScriptProcesses.Value.Should().Be(expected);
        plan.MaxScriptProcesses.Bound.Should().Be(SizingBound.Ram);
    }

    [Fact]
    public void Create_ScriptProcesses_NeverExceedTheStepLimit()
    {
        var configured = new Dictionary<string, int?> { [PerformanceSizing.ConfigKeys.MaxConcurrentSteps] = 10 };

        var plan = PerformanceSizing.Create(
            new DetectedResources(8, 64 * GB, IsDesktop: false), manualTuning: true, configured);

        plan.MaxScriptProcesses.Value.Should().Be(10);
        plan.MaxScriptProcesses.Bound.Should().Be(SizingBound.Ceiling, "the value was derived and capped, not configured");
    }

    [Fact]
    public void Create_Auto_ScriptProcesses_OnADesktop_TakeASmallerShare()
    {
        Auto(cores: 8, ramGb: 16, desktop: true).MaxScriptProcesses.Value.Should().Be(30);
    }

    [Fact]
    public void Create_Auto_ScriptProcesses_OnATinyHost_KeepTheFloor()
    {
        var plan = Auto(cores: 4, ramGb: 1);

        plan.MaxScriptProcesses.Value.Should().Be(PerformanceSizing.ScriptProcessesFloor);
        plan.MaxScriptProcesses.Bound.Should().Be(SizingBound.Floor);
    }

    [Fact]
    public void Create_ScriptProcesses_WithoutAMemoryReading_FollowTheCores()
    {
        var plan = PerformanceSizing.Create(
            new DetectedResources(8, UsableMemoryBytes: null, IsDesktop: false), manualTuning: false, NoConfig);

        plan.MaxScriptProcesses.Value.Should().Be(32);
        plan.MaxScriptProcesses.Bound.Should().Be(SizingBound.Cpu);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Create_Manual_ScriptProcesses_WithoutAValue_StayDerived(int? configuredValue)
    {
        // A manually raised step limit alone must not raise the script process limit.
        var configured = new Dictionary<string, int?>
        {
            [PerformanceSizing.ConfigKeys.MaxConcurrentSteps] = 600,
            [PerformanceSizing.ConfigKeys.MaxScriptProcesses] = configuredValue,
        };

        var plan = PerformanceSizing.Create(
            new DetectedResources(4, 5 * GB, IsDesktop: false), manualTuning: true, configured);

        plan.MaxConcurrentSteps.Value.Should().Be(600);
        plan.MaxScriptProcesses.Value.Should().Be(16);
        plan.MaxScriptProcesses.Bound.Should().Be(SizingBound.Cpu);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_ScriptProcesses_TakeAConfiguredValueInBothModes(bool manualTuning)
    {
        // Unlike the other knobs this one is not part of the manual preset: raising it must not
        // require taking over every other value by hand.
        var configured = new Dictionary<string, int?> { [PerformanceSizing.ConfigKeys.MaxScriptProcesses] = 60 };

        var plan = PerformanceSizing.Create(
            new DetectedResources(4, 5 * GB, IsDesktop: false), manualTuning, configured);

        plan.MaxScriptProcesses.Value.Should().Be(60);
        plan.MaxScriptProcesses.Bound.Should().Be(SizingBound.Manual);
    }

    [Fact]
    public void ConfigKeys_ExcludeTheExecutionSafetyCaps()
    {
        // Engine:MaxConcurrentExecutions guards against trigger loops and sub-workflow cascades.
        // Pulling it into the sizing plan would mean automatic mode silently overrides a cap the
        // operator set deliberately — it stays purely configuration-driven.
        PerformanceSizing.ConfigKeys.All.Should().NotContain(k => k.StartsWith("Engine:MaxConcurrentExecutions"));
    }

    [Fact]
    public void Create_Auto_IgnoresConfiguredValues()
    {
        var configured = new Dictionary<string, int?>
        {
            [PerformanceSizing.ConfigKeys.MaxRunspaces] = 768,
            [PerformanceSizing.ConfigKeys.MaxConcurrentSteps] = 600,
        };

        var plan = PerformanceSizing.Create(
            new DetectedResources(8, 16 * GB, IsDesktop: false), manualTuning: false, configured);

        plan.MaxRunspaces.Value.Should().Be(32, "with the switch off the configured numbers are inert");
        plan.MaxConcurrentSteps.Value.Should().Be(256);
    }
}

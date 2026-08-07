using System.ComponentModel;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Sources;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

/// <summary>
/// Black-box tests for <see cref="FileWatcherTriggerSource"/>. Validation paths are pure
/// (no FS interaction) and covered fully. The happy-path uses a real temp directory so
/// FileSystemWatcher actually fires — no good way to mock that without rewriting the source.
///
/// The liveness tests below do not need a real native-handle failure: the exception-to-verdict
/// decision lives in the pure <c>ClassifyWatcherError</c>, and <c>OnWatcherError</c> is internal
/// so the Error event can be driven directly. Likewise the start deadline is tested through
/// <c>RunBoundedAsync</c> rather than against a genuinely unreachable share, which would make the
/// suite depend on how the CI runner's network stack fails.
/// </summary>
public class FileWatcherTriggerSourceTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Escapes a Windows path for embedding in a JSON string literal.</summary>
    private static string Esc(string path) => path.Replace("\\", "\\\\");

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    private static IConfiguration ConfigWith(params (string key, string val)[] entries)
    {
        var dict = entries.ToDictionary(e => e.key, e => (string?)e.val);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static IConfiguration WithAllowedRoots(params string[] roots)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < roots.Length; i++)
            dict[$"Trigger:FileWatcher:AllowedRoots:{i}"] = roots[i];
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static TriggerContext Ctx(string configJson, Func<Dictionary<string, string>, Task>? onFire = null) => new()
    {
        WorkflowId = Guid.NewGuid(),
        NodeId = "trg",
        Config = Cfg(configJson),
        OnFire = onFire ?? (_ => Task.CompletedTask),
    };

    [Fact]
    public async Task StartAsync_Throws_WhenDirectoryMissing()
    {
        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());

        var act = () => src.StartAsync(Ctx("""{}"""), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*'directory' is required*");
    }

    [Fact]
    public async Task StartAsync_Throws_WhenDirectoryIsHardBlockedSystemPath()
    {
        // The hard-blocklist (C:\Windows etc.) prevents a workflow author from pointing the
        // process-identity FSW at sensitive system paths. Default behavior — no opt-in.
        if (!OperatingSystem.IsWindows())
            return; // hard-blocklist is Windows-only

        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());

        var act = () => src.StartAsync(Ctx("""{"directory":"C:\\Windows\\System32"}"""), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system path*");
    }

    [Fact]
    public async Task StartAsync_AllowsSystemPath_WhenAllowSystemPathsConfigSet()
    {
        // Admin override: AllowSystemPaths=true bypasses the hard-blocklist. We don't want
        // to actually open a watcher on C:\Windows in tests, so we expect a different failure
        // (the directory might not exist for a path we choose, or AllowedRoots check fails).
        // What we're pinning: the hard-blocklist throw does NOT fire when the flag is set.
        if (!OperatingSystem.IsWindows())
            return;

        var src = new FileWatcherTriggerSource(
            NullLogger<FileWatcherTriggerSource>.Instance,
            ConfigWith(("Trigger:FileWatcher:AllowSystemPaths", "true")));

        // C:\Windows exists, so we can't rely on DirectoryNotFoundException. Instead, use
        // a plausibly-non-existent system path that gets past the system-path check (because
        // AllowSystemPaths=true) and then trips on the does-not-exist check.
        var nonExistent = @"C:\Windows\NodePilot-Test-DoesNotExist-" + Guid.NewGuid().ToString("N");

        var act = () => src.StartAsync(Ctx($$"""{"directory":"{{nonExistent.Replace("\\", "\\\\")}}"}"""),
            CancellationToken.None);

        // Asserts: not the system-path message — a downstream check fired first.
        await act.Should().ThrowAsync<Exception>()
            .Where(ex => !ex.Message.Contains("system path"));
    }

    [Fact]
    public async Task StartAsync_Throws_WhenDirectoryNotInAllowedRoots()
    {
        // Once AllowedRoots is configured, anything outside is rejected — even directories
        // that exist on disk. Tests this by pointing at the temp folder while AllowedRoots
        // contains only an unrelated directory.
        using var tempDir = new TempDirectory();
        var unrelatedRoot = Path.Combine(Path.GetTempPath(), "nodepilot-allow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unrelatedRoot);
        try
        {
            var src = new FileWatcherTriggerSource(
                NullLogger<FileWatcherTriggerSource>.Instance,
                WithAllowedRoots(unrelatedRoot));

            var configJson = $$"""{"directory":"{{tempDir.Path.Replace("\\", "\\\\")}}"}""";
            var act = () => src.StartAsync(Ctx(configJson), CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*not within any configured Trigger:FileWatcher:AllowedRoots*");
        }
        finally
        {
            try { Directory.Delete(unrelatedRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StartAsync_Throws_WhenDirectoryDoesNotExist()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), "nodepilot-missing-" + Guid.NewGuid().ToString("N"));
        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());

        var configJson = $$"""{"directory":"{{nonExistent.Replace("\\", "\\\\")}}"}""";
        var act = () => src.StartAsync(Ctx(configJson), CancellationToken.None);

        await act.Should().ThrowAsync<DirectoryNotFoundException>()
            .WithMessage("*does not exist*");
    }

    [Fact]
    public async Task StartAsync_FiresOnFire_WhenFileCreatedInWatchedDirectory()
    {
        // Smoke test: spin up the watcher on a real temp dir, drop a file, expect OnFire
        // to land within a few seconds. This is the "trigger source actually integrates with
        // FileSystemWatcher" check — the validation tests above don't exercise the
        // subscription wiring at all.
        using var tempDir = new TempDirectory();

        var fired = new TaskCompletionSource<Dictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());
        var ctx = Ctx(
            $$"""{"directory":"{{tempDir.Path.Replace("\\", "\\\\")}}","filter":"*.txt","watchType":"created"}""",
            onFire: parameters =>
            {
                fired.TrySetResult(parameters);
                return Task.CompletedTask;
            });

        try
        {
            await src.StartAsync(ctx, CancellationToken.None);

            // Brief delay for the watcher to fully arm; FSW occasionally drops the very first
            // event if a file is written immediately on Windows.
            await Task.Delay(150);
            await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "hello.txt"), "hi");

            var result = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));

            result.Should().ContainKey("filePath");
            result.Should().ContainKey("fileAction");
            result["fileAction"].Should().Be("created");
            result["filePath"].Should().EndWith("hello.txt");
        }
        finally
        {
            await src.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_IsSafe_WhenStartAsyncWasNeverCalled()
    {
        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());

        await src.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_StopsFurtherEvents()
    {
        // After Dispose, the FSW must be torn down so a subsequent file create doesn't
        // sneak through into OnFire — would leak goroutines into later tests.
        using var tempDir = new TempDirectory();
        var fireCount = 0;

        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());
        var ctx = Ctx(
            $$"""{"directory":"{{tempDir.Path.Replace("\\", "\\\\")}}","filter":"*.txt"}""",
            onFire: _ =>
            {
                Interlocked.Increment(ref fireCount);
                return Task.CompletedTask;
            });

        await src.StartAsync(ctx, CancellationToken.None);
        await src.DisposeAsync();

        await File.WriteAllTextAsync(Path.Combine(tempDir.Path, "after-dispose.txt"), "x");
        await Task.Delay(750); // longer than the 500ms debounce; if the watcher is alive we'd see at least one fire.

        fireCount.Should().Be(0);
    }

    // ---- liveness: a watcher whose path died must report itself unhealthy ----

    [Fact]
    public void ClassifyWatcherError_ReturnsNull_ForInternalBufferOverflow()
    {
        // Overflow is survivable: the runtime re-issues ReadDirectoryChangesW, so the watcher
        // keeps running. Faulting here would make the orchestrator dispose and re-create the
        // source in a loop under sustained churn — losing more events than the overflow did.
        FileWatcherTriggerSource.ClassifyWatcherError(new InternalBufferOverflowException("full"))
            .Should().BeNull();
    }

    [Fact]
    public void ClassifyWatcherError_ReturnsReason_ForNativeHandleFailure()
    {
        // ERROR_NETNAME_DELETED (64) — what Windows reports when a watched UNC share vanishes.
        var reason = FileWatcherTriggerSource.ClassifyWatcherError(new Win32Exception(64));

        reason.Should().NotBeNull();
        reason.Should().Contain(nameof(Win32Exception));
    }

    [Fact]
    public async Task Health_IsHealthy_AfterSuccessfulStart()
    {
        using var tempDir = new TempDirectory();
        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());

        try
        {
            await src.StartAsync(Ctx($$"""{"directory":"{{Esc(tempDir.Path)}}"}"""), CancellationToken.None);

            src.Health.IsHealthy.Should().BeTrue();
            src.Health.Reason.Should().BeNull();
        }
        finally
        {
            await src.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnWatcherError_MarksUnhealthy_ForNativeHandleFailure()
    {
        // The scenario this whole mechanism exists for: the share disappears at runtime, FSW
        // raises Error, the watcher is dead. Without the fault flag the orchestrator would keep
        // the corpse registered forever and never notice the share coming back.
        using var tempDir = new TempDirectory();
        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());

        try
        {
            await src.StartAsync(Ctx($$"""{"directory":"{{Esc(tempDir.Path)}}"}"""), CancellationToken.None);

            src.OnWatcherError(this, new ErrorEventArgs(new Win32Exception(64)));

            src.Health.IsHealthy.Should().BeFalse();
            src.Health.Reason.Should().Contain(nameof(Win32Exception));
        }
        finally
        {
            await src.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnWatcherError_StaysHealthy_ForBufferOverflow()
    {
        // Anti-flap pin: an overflow must never cost the source its registration.
        using var tempDir = new TempDirectory();
        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());

        try
        {
            await src.StartAsync(Ctx($$"""{"directory":"{{Esc(tempDir.Path)}}"}"""), CancellationToken.None);

            src.OnWatcherError(this, new ErrorEventArgs(new InternalBufferOverflowException("full")));

            src.Health.IsHealthy.Should().BeTrue();
        }
        finally
        {
            await src.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnWatcherError_KeepsFirstFaultReason_WhenErrorsRepeat()
    {
        // The first fault is the diagnostic one. Later noise on the way down must not overwrite
        // what the operator sees in the eviction log line.
        using var tempDir = new TempDirectory();
        var src = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, EmptyConfig());

        try
        {
            await src.StartAsync(Ctx($$"""{"directory":"{{Esc(tempDir.Path)}}"}"""), CancellationToken.None);

            src.OnWatcherError(this, new ErrorEventArgs(new Win32Exception(64)));
            var first = src.Health.Reason;
            src.OnWatcherError(this, new ErrorEventArgs(new IOException("later noise")));

            src.Health.Reason.Should().Be(first);
            src.Health.Reason.Should().NotContain("later noise");
        }
        finally
        {
            await src.DisposeAsync();
        }
    }

    // ---- bounded start: an unreachable share must not stall trigger reconciliation ----

    [Fact]
    public async Task RunBoundedAsync_ReturnsResult_WhenWorkCompletesInTime()
    {
        var result = await FileWatcherTriggerSource.RunBoundedAsync(
            () => 42, TimeSpan.FromSeconds(30), disposeAbandoned: null, CancellationToken.None);

        result.Should().Be(42);
    }

    [Fact]
    public async Task RunBoundedAsync_Throws_AndDisposesAbandonedResult_WhenWorkExceedsDeadline()
    {
        // Stands in for a Directory.Exists / CreateFile hanging on a dead SMB redirector: the
        // orchestrator registers triggers sequentially, so this must fail fast rather than block
        // every other workflow's triggers. And whatever the abandoned work eventually produces
        // has to be released — an armed watcher nobody owns would keep firing forever.
        using var release = new ManualResetEventSlim(false);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var act = () => FileWatcherTriggerSource.RunBoundedAsync(
            () => { release.Wait(TimeSpan.FromSeconds(30)); return new object(); },
            TimeSpan.FromMilliseconds(50),
            _ => disposed.TrySetResult(),
            CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();

        release.Set();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ---- health probe: catches a path that vanished without the watcher raising Error ----

    /// <summary>Polls <see cref="FileWatcherTriggerSource.Health"/> until it faults or time runs out.</summary>
    private static async Task<TriggerHealth> WaitForFaultAsync(FileWatcherTriggerSource src, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!src.Health.IsHealthy) return src.Health;
            await Task.Delay(25);
        }
        return src.Health;
    }

    /// <summary>
    /// Starts a source whose reachability check is driven by <paramref name="reachable"/> instead
    /// of the real filesystem. The scenario the probe covers — a host gone so hard the pending
    /// change notification is never completed — cannot be staged locally: deleting the watched
    /// directory raises an FSW Error, so the primary fault path fires first and hides the probe.
    ///
    /// The registration check goes through the same seam, so this absorbs that first call and
    /// answers it reachable. <paramref name="reachable"/> therefore only ever sees probe calls,
    /// and a test can express "healthy at start, gone afterwards" as a plain constant.
    /// </summary>
    private static async Task<FileWatcherTriggerSource> StartWithProbeAsync(
        TempDirectory tempDir, int probeSeconds, Func<string, bool> reachable)
    {
        var startupCheckDone = 0;
        var src = new FileWatcherTriggerSource(
            NullLogger<FileWatcherTriggerSource>.Instance,
            ConfigWith(("Trigger:FileWatcher:HealthProbeSeconds", probeSeconds.ToString())))
        {
            DirectoryProbe = dir =>
                Interlocked.Exchange(ref startupCheckDone, 1) == 0 || reachable(dir),
        };
        await src.StartAsync(Ctx($$"""{"directory":"{{Esc(tempDir.Path)}}"}"""), CancellationToken.None);
        return src;
    }

    [Fact]
    public async Task Health_BecomesUnhealthy_WhenWatchedDirectoryDisappears_WithoutTheProbe()
    {
        // End-to-end pin of the real mechanism against an actual FileSystemWatcher: pulling the
        // watched directory out from under it raises Error, which must fault the source. The probe
        // is off, so only the Error path can produce this — the same path a vanishing UNC share
        // takes, without needing a network in the test suite.
        var tempDir = new TempDirectory();
        var src = new FileWatcherTriggerSource(
            NullLogger<FileWatcherTriggerSource>.Instance,
            ConfigWith(("Trigger:FileWatcher:HealthProbeSeconds", "0")));

        try
        {
            await src.StartAsync(Ctx($$"""{"directory":"{{Esc(tempDir.Path)}}"}"""), CancellationToken.None);
            src.Health.IsHealthy.Should().BeTrue();

            tempDir.Dispose();

            var health = await WaitForFaultAsync(src, TimeSpan.FromSeconds(10));

            health.IsHealthy.Should().BeFalse();
            health.Reason.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await src.DisposeAsync();
            tempDir.Dispose();
        }
    }

    [Fact]
    public async Task HealthProbe_MarksUnhealthy_AfterPathBecomesUnreachable()
    {
        // Without this backstop, a watcher that went deaf without raising Error would report
        // healthy forever while nothing watches the drop folder any more.
        using var tempDir = new TempDirectory();
        var src = await StartWithProbeAsync(tempDir, probeSeconds: 1, reachable: _ => false);

        try
        {
            var health = await WaitForFaultAsync(src, TimeSpan.FromSeconds(20));

            health.IsHealthy.Should().BeFalse();
            health.Reason.Should().Contain("unreachable");
        }
        finally
        {
            await src.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_Throws_WhenReachabilityCheckExceedsDeadline()
    {
        // A path that hangs instead of answering — an unreachable SMB host does not send a reset,
        // so the redirector just blocks. Registration must give up rather than hold the
        // orchestrator's sequential sync pass hostage on behalf of one workflow.
        using var tempDir = new TempDirectory();
        using var release = new ManualResetEventSlim(false);
        var src = new FileWatcherTriggerSource(
            NullLogger<FileWatcherTriggerSource>.Instance,
            ConfigWith(("Trigger:FileWatcher:PathTimeoutSeconds", "1")))
        {
            DirectoryProbe = _ => { release.Wait(TimeSpan.FromSeconds(30)); return true; },
        };

        try
        {
            var act = () => src.StartAsync(Ctx($$"""{"directory":"{{Esc(tempDir.Path)}}"}"""), CancellationToken.None);

            await act.Should().ThrowAsync<TimeoutException>().WithMessage("*did not respond within*");
        }
        finally
        {
            release.Set(); // let the abandoned thread unwind before the test process moves on
            await src.DisposeAsync();
        }
    }

    [Fact]
    public async Task HealthProbe_TreatsATimedOutCheckAsUnreachable()
    {
        // Same hang, but after the watcher is already running: the probe must not sit there
        // forever, and a check that never answers is exactly as bad as one that answers "gone".
        using var tempDir = new TempDirectory();
        using var release = new ManualResetEventSlim(false);
        var calls = 0;
        var src = new FileWatcherTriggerSource(
            NullLogger<FileWatcherTriggerSource>.Instance,
            ConfigWith(
                ("Trigger:FileWatcher:HealthProbeSeconds", "1"),
                ("Trigger:FileWatcher:PathTimeoutSeconds", "1")))
        {
            // First call is the registration check; every probe afterwards hangs.
            DirectoryProbe = _ =>
            {
                if (Interlocked.Increment(ref calls) > 1) release.Wait(TimeSpan.FromSeconds(30));
                return true;
            },
        };

        try
        {
            await src.StartAsync(Ctx($$"""{"directory":"{{Esc(tempDir.Path)}}"}"""), CancellationToken.None);

            var health = await WaitForFaultAsync(src, TimeSpan.FromSeconds(25));

            health.IsHealthy.Should().BeFalse();
            health.Reason.Should().Contain("unreachable");
        }
        finally
        {
            release.Set();
            await src.DisposeAsync();
        }
    }

    [Fact]
    public async Task HealthProbe_StaysHealthy_ForSingleTransientFailure()
    {
        // One failed probe must not evict a healthy watcher — that would cause exactly the event
        // loss the probe exists to prevent. Only ProbeFailuresBeforeFault in a row counts.
        using var tempDir = new TempDirectory();
        var probes = 0;
        var src = await StartWithProbeAsync(tempDir, probeSeconds: 1,
            reachable: _ => Interlocked.Increment(ref probes) != 1); // only the first probe fails

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4)); // several ticks past the transient failure

            probes.Should().BeGreaterThan(2, "the probe should have run past the single failure");
            src.Health.IsHealthy.Should().BeTrue();
        }
        finally
        {
            await src.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_StopsHealthProbe()
    {
        // A probe surviving disposal would keep a timer alive per re-created source and keep
        // touching a path the orchestrator has long moved on from.
        using var tempDir = new TempDirectory();
        var probes = 0;
        var src = await StartWithProbeAsync(tempDir, probeSeconds: 1,
            reachable: _ => { Interlocked.Increment(ref probes); return true; });

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await src.DisposeAsync();
        var afterDispose = Volatile.Read(ref probes);

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        Volatile.Read(ref probes).Should().Be(afterDispose);
    }

    [Fact]
    public async Task StartAsync_SkipsHealthProbe_WhenIntervalIsZero()
    {
        using var tempDir = new TempDirectory();
        var probes = 0;
        var src = await StartWithProbeAsync(tempDir, probeSeconds: 0,
            reachable: _ => { Interlocked.Increment(ref probes); return false; });

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));

            Volatile.Read(ref probes).Should().Be(0);
            src.Health.IsHealthy.Should().BeTrue();
        }
        finally
        {
            await src.DisposeAsync();
        }
    }

    /// <summary>
    /// Disposable temp directory wrapper. Cleans up the directory tree on Dispose, even
    /// when the test threw mid-way — important because per-test temp dirs accumulate
    /// quickly on a CI runner if leftover.
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "nodepilot-fw-test-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}

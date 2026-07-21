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
/// </summary>
public class FileWatcherTriggerSourceTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement.Clone();

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

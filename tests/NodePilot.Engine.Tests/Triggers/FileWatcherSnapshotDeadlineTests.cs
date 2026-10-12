using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Sources;
using Xunit;

namespace NodePilot.Engine.Tests.Triggers;

public sealed class FileWatcherSnapshotDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedReconciliationScan_DoesNotBlockDisposalOrPublishLateSnapshot(bool waitForTimeout)
    {
        var directory = Path.Combine(Path.GetTempPath(), "nodepilot-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var lateFile = Path.Combine(directory, "late.txt");
        await File.WriteAllTextAsync(lateFile, "late result");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fires = 0;
        var checkpointWrites = 0;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trigger:FileWatcher:AllowedRoots:0"] = directory,
            ["Trigger:FileWatcher:HealthProbeSeconds"] = "1",
            ["Trigger:FileWatcher:PathTimeoutSeconds"] = waitForTimeout ? "1" : "30",
        }).Build();
        var source = new FileWatcherTriggerSource(NullLogger<FileWatcherTriggerSource>.Instance, configuration);
        Task? disposal = null;
        try
        {
            await source.StartAsync(new TriggerContext
            {
                WorkflowId = Guid.NewGuid(), NodeId = "files",
                Config = JsonSerializer.SerializeToElement(new { directory, filter = "*.watched", watchType = "created" }),
                OnFire = _ => { Interlocked.Increment(ref fires); return Task.CompletedTask; },
                SaveCheckpoint = _ => { Interlocked.Increment(ref checkpointWrites); return Task.FromResult(true); },
            }, CancellationToken.None);
            source.SnapshotFiles = (_, _, _) =>
            {
                entered.TrySetResult();
                release.Wait(); // Models an SMB enumeration that ignores managed cancellation.
                returned.TrySetResult();
                return [lateFile];
            };
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (waitForTimeout)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (source.Health.IsHealthy && DateTime.UtcNow < deadline)
                    await Task.Delay(20);
                source.Health.IsHealthy.Should().BeFalse("an unresponsive reconciliation scan must be evicted");
                source.Health.Reason.Should().Contain("snapshot");
            }
            disposal = source.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(2));
            source.SnapshotPathsForTests().Should().BeEmpty();
            Volatile.Read(ref fires).Should().Be(0);
            Volatile.Read(ref checkpointWrites).Should().Be(0);
            release.Set();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Disposal drained the mutable reconciliation; the abandoned worker owns only a local snapshot.
            source.SnapshotPathsForTests().Should().BeEmpty();
            Volatile.Read(ref fires).Should().Be(0);
            Volatile.Read(ref checkpointWrites).Should().Be(0);
        }
        finally
        {
            release.Set();
            if (disposal is not null) await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            else await source.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}

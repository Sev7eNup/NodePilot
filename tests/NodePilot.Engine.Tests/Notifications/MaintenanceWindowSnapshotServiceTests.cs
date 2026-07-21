using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Scheduler;
using Xunit;

namespace NodePilot.Engine.Tests.Notifications;

/// <summary>
/// The snapshot service is the backstop that catches maintenance-window edits made on another
/// HA node. It must refresh on startup and must NEVER let a failed refresh kill the loop —
/// a crashed loop leaves every node evaluating windows against a permanently stale snapshot.
/// </summary>
public class MaintenanceWindowSnapshotServiceTests
{
    [Fact]
    public async Task ExecuteAsync_RefreshesSnapshotOnStartup()
    {
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eval = new Mock<IMaintenanceWindowEvaluator>();
        eval.Setup(e => e.RefreshAsync(It.IsAny<CancellationToken>()))
            .Returns(() => { refreshed.TrySetResult(); return Task.CompletedTask; });

        var svc = new MaintenanceWindowSnapshotService(eval.Object, NullLogger<MaintenanceWindowSnapshotService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(refreshed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await svc.StopAsync(CancellationToken.None);

        completed.Should().BeSameAs(refreshed.Task, "the loop must refresh the snapshot immediately on startup");
        eval.Verify(e => e.RefreshAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExecuteAsync_RefreshThrows_IsSwallowedAndServiceStopsCleanly()
    {
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eval = new Mock<IMaintenanceWindowEvaluator>();
        eval.Setup(e => e.RefreshAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                attempted.TrySetResult();
                throw new InvalidOperationException("db unreachable");
            });

        var svc = new MaintenanceWindowSnapshotService(eval.Object, NullLogger<MaintenanceWindowSnapshotService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // refresh attempted and threw internally

        // The exception is swallowed (fail-open); StopAsync must complete without surfacing it.
        var stop = () => svc.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();
        eval.Verify(e => e.RefreshAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}

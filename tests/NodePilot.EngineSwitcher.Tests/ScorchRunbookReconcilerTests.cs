using FluentAssertions;
using NodePilot.EngineSwitcher.Configuration;
using NodePilot.EngineSwitcher.Services;
using Xunit;

namespace NodePilot.EngineSwitcher.Tests;

public sealed class ScorchRunbookReconcilerTests
{
    [Fact]
    public async Task StopAllManagedJobs_StopsEveryActiveSourceJob()
    {
        var first = new ScorchRunbook(Guid.NewGuid(), "First");
        var second = new ScorchRunbook(Guid.NewGuid(), "Second");
        var client = new StatefulScorchClient([first, second]);
        client.Jobs.Add(new ScorchJob(Guid.NewGuid(), first.Id, "Running"));
        client.Jobs.Add(new ScorchJob(Guid.NewGuid(), second.Id, "Pending"));
        client.Jobs.Add(new ScorchJob(Guid.NewGuid(), second.Id, "Completed"));
        var reconciler = new ScorchRunbookReconciler(new FixedScorchFactory(client), new RecordingLogger());

        var mutations = 0;
        await reconciler.StopAllManagedJobsAsync(Configuration(), null, () => mutations++, CancellationToken.None);

        client.Jobs.Should().OnlyContain(job => job.Status != "Running" && job.Status != "Pending");
        client.Stopped.Should().HaveCount(2);
        mutations.Should().Be(1);
    }

    // Reading the job list changes nothing, so the caller must not arm its fail-closed cleanup.
    [Fact]
    public async Task StopAllManagedJobs_WithoutAnyActiveJob_ReportsNoMutation()
    {
        var client = new StatefulScorchClient([]);
        var reconciler = new ScorchRunbookReconciler(new FixedScorchFactory(client), new RecordingLogger());
        var mutated = false;

        await reconciler.StopAllManagedJobsAsync(Configuration(), null, () => mutated = true, CancellationToken.None);

        mutated.Should().BeFalse();
    }

    [Fact]
    public async Task Reconcile_StopsUnlistedJobsAndStartsOnlyMissingAllowedRunbooks()
    {
        var alreadyRunning = new ScorchRunbook(Guid.NewGuid(), "Keep");
        var missing = new ScorchRunbook(Guid.NewGuid(), "Start");
        var unlisted = new ScorchRunbook(Guid.NewGuid(), "Stop");
        var client = new StatefulScorchClient([alreadyRunning, missing, unlisted]);
        client.Jobs.Add(new ScorchJob(Guid.NewGuid(), alreadyRunning.Id, "Running"));
        client.Jobs.Add(new ScorchJob(Guid.NewGuid(), unlisted.Id, "Running"));
        var reconciler = new ScorchRunbookReconciler(new FixedScorchFactory(client), new RecordingLogger());

        await reconciler.ReconcileAsync(Configuration(), ["Keep", missing.Id.ToString()], null, CancellationToken.None);

        client.Jobs.Where(job => job.Status == "Running").Select(job => job.RunbookId)
            .Should().BeEquivalentTo([alreadyRunning.Id, missing.Id]);
        client.Started.Should().Equal(missing.Id);
        client.Stopped.Should().ContainSingle();
    }

    [Fact]
    public async Task Reconcile_DoesNotTreatPendingAllowedJobAsSuccessfullyRunning()
    {
        var allowed = new ScorchRunbook(Guid.NewGuid(), "Start");
        var client = new StatefulScorchClient([allowed]);
        client.Jobs.Add(new ScorchJob(Guid.NewGuid(), allowed.Id, "Pending"));
        var reconciler = new ScorchRunbookReconciler(new FixedScorchFactory(client), new RecordingLogger());

        await reconciler.ReconcileAsync(Configuration(), [allowed.Name], null, CancellationToken.None);

        client.Started.Should().ContainSingle().Which.Should().Be(allowed.Id);
        client.StartedOn.Should().ContainSingle().Which.Should().Equal("CM1");
        client.Jobs.Should().ContainSingle(job => job.RunbookId == allowed.Id && job.Status == "Running");
    }

    // An allowlist of ordinary runbooks finishes in seconds. Demanding that every one of them is
    // running at the same moment could only ever be satisfied by long-lived monitor runbooks.
    [Fact]
    public async Task Reconcile_WhenAStartedRunbookFinishesImmediately_Settles()
    {
        var allowed = new ScorchRunbook(Guid.NewGuid(), "Start");
        var client = new StatefulScorchClient([allowed]) { StartedJobStatus = "Completed" };
        var reconciler = new ScorchRunbookReconciler(new FixedScorchFactory(client), new RecordingLogger());

        await reconciler.ReconcileAsync(
            Configuration(reconciliationTimeoutSeconds: 5), [allowed.Name], null, CancellationToken.None);

        client.Started.Should().ContainSingle().Which.Should().Be(allowed.Id);
    }

    // The deadline used to surface as a bare TaskCanceledException, which the coordinator let
    // through and the async void command handler turned into a process crash.
    [Fact]
    public async Task Reconcile_WhenAnAllowedRunbookNeverLeavesPending_FailsWithATimeoutNamingIt()
    {
        var allowed = new ScorchRunbook(Guid.NewGuid(), "Start");
        var client = new StatefulScorchClient([allowed]) { StartedJobStatus = "Pending" };
        var reconciler = new ScorchRunbookReconciler(new FixedScorchFactory(client), new RecordingLogger());

        var action = () => reconciler.ReconcileAsync(
            Configuration(reconciliationTimeoutSeconds: 1), [allowed.Name], null, CancellationToken.None);

        (await action.Should().ThrowAsync<TimeoutException>()).Which.Message
            .Should().Contain("did not settle within 1 seconds")
            .And.Contain(allowed.Id.ToString());
    }

    private static ScorchWorkloadConfiguration Configuration(int reconciliationTimeoutSeconds = 60) =>
        new(@"\\server\share\scorch.txt", "http://localhost:81",
            ReconciliationTimeoutSeconds: reconciliationTimeoutSeconds);

    private sealed class FixedScorchFactory(IScorchApiClient client) : IScorchApiClientFactory
    {
        public IScorchApiClient Create(ScorchWorkloadConfiguration configuration) => client;
    }

    private sealed class StatefulScorchClient(IEnumerable<ScorchRunbook> runbooks) : IScorchApiClient
    {
        public List<ScorchRunbook> Runbooks { get; } = runbooks.ToList();
        public List<ScorchRunbookServer> RunbookServers { get; } =
            [new ScorchRunbookServer("CM1")];
        public List<ScorchJob> Jobs { get; } = [];
        public List<Guid> Started { get; } = [];
        public List<IReadOnlyList<string>> StartedOn { get; } = [];
        public List<Guid> Stopped { get; } = [];
        public string StartedJobStatus { get; init; } = "Running";
        public void Dispose() { }
        public Task<IReadOnlyList<ScorchRunbook>> ListRunbooksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScorchRunbook>>(Runbooks.ToArray());
        public Task<IReadOnlyList<ScorchRunbookServer>> ListRunbookServersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScorchRunbookServer>>(RunbookServers.ToArray());
        public Task<IReadOnlyList<ScorchJob>> ListJobsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScorchJob>>(Jobs.ToArray());
        public Task StartRunbookAsync(
            Guid runbookId,
            IReadOnlyList<string> runbookServers,
            CancellationToken cancellationToken)
        {
            Started.Add(runbookId);
            StartedOn.Add(runbookServers);
            Jobs.Add(new ScorchJob(Guid.NewGuid(), runbookId, StartedJobStatus));
            return Task.CompletedTask;
        }
        public Task StopJobAsync(Guid jobId, CancellationToken cancellationToken)
        {
            Stopped.Add(jobId);
            var index = Jobs.FindIndex(job => job.Id == jobId);
            Jobs[index] = Jobs[index] with { Status = "Canceled" };
            return Task.CompletedTask;
        }
    }
}

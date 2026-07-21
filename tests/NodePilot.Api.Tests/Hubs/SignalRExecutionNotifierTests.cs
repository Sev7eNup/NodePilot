using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NodePilot.Api.Hubs;
using NodePilot.Core.Enums;
using Xunit;

namespace NodePilot.Api.Tests.Hubs;

public sealed class SignalRExecutionNotifierTests : IDisposable
{
    private sealed class Capture
    {
        public string? Method;
        public IReadOnlyList<string>? Groups;
        public object?[]? Args;
    }

    public SignalRExecutionNotifierTests()
    {
        ExecutionHub.ClearGroupsForTest();
        ExecutionHub.ClearOpsFeedForTest();
    }

    public void Dispose()
    {
        ExecutionHub.ClearGroupsForTest();
        ExecutionHub.ClearOpsFeedForTest();
    }

    private static (SignalRExecutionNotifier notifier, Capture capture) Build(bool hasSubscribers = true)
    {
        var capture = new Capture();
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask)
             .Callback<string, object?[], CancellationToken>((method, args, _) =>
             {
                 capture.Method = method;
                 capture.Args = args;
             });

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Groups(It.IsAny<IReadOnlyList<string>>()))
               .Returns(proxy.Object)
               .Callback<IReadOnlyList<string>>(g => capture.Groups = g);

        var hub = new Mock<IHubContext<ExecutionHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        return (new SignalRExecutionNotifier(hub.Object, hasSubscribers: (_, _) => hasSubscribers), capture);
    }

    private static LiveEventBatchItem FirstItem(Capture capture)
    {
        capture.Method.Should().Be("LiveEventsBatch");
        capture.Args.Should().HaveCount(1);
        var batch = capture.Args![0].Should().BeOfType<LiveEventsBatch>().Subject;
        batch.Events.Should().HaveCount(1);
        return batch.Events[0];
    }

    [Fact]
    public async Task StepStartedAsync_DropsEventWhenNoSubscribers()
    {
        var (notifier, capture) = Build(hasSubscribers: false);

        await notifier.StepStartedAsync(Guid.NewGuid(), Guid.NewGuid(), "step-1", "Check Disk", "runScript", DateTime.UtcNow);
        await notifier.FlushForTestAsync();

        capture.Method.Should().BeNull();
    }

    [Fact]
    public async Task StepStartedAsync_SendsBatchToExecutionGroupOnly_WhenExecutionGroupHasSubscribers()
    {
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        // Step events now go only to the per-execution group. Workflow-group subscribers
        // receive ExecutionStatusChanged only (so the Live tab shows status badges without
        // pulling step events for every one of 100 concurrent runs).
        ExecutionHub.RegisterGroupForTest("conn-1", execId.ToString());

        await notifier.StepStartedAsync(execId, wfId, "step-1", "Check Disk", "runScript", DateTime.UtcNow);
        await notifier.FlushForTestAsync();

        capture.Groups.Should().BeEquivalentTo(new[] { execId.ToString() });
        var item = FirstItem(capture);
        item.Type.Should().Be("StepStarted");
        item.Event.Should().BeOfType<StepStartedEvent>();
    }

    [Fact]
    public async Task StepStartedAsync_DropsEventWhenOnlyWorkflowGroupHasSubscribers()
    {
        // If the client is watching the workflow (editor is open) but hasn't called
        // JoinExecution, step events must not be sent — they only go to the execution group.
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        // Register the workflow group only, NOT the execution group.
        ExecutionHub.RegisterGroupForTest("conn-1", $"workflow-{wfId}");

        await notifier.StepStartedAsync(execId, wfId, "step-1", "Check Disk", "runScript", DateTime.UtcNow);
        await notifier.FlushForTestAsync();

        capture.Method.Should().BeNull("step events must be dropped when no execution-group subscriber exists");
    }

    [Fact]
    public async Task StepCompletedAsync_PayloadCarriesStatusAndOutputParameters()
    {
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        ExecutionHub.RegisterGroupForTest("conn-1", execId.ToString());
        var pars = new Dictionary<string, string> { ["host"] = "web01", ["count"] = "42" };

        await notifier.StepCompletedAsync(
            execId, wfId, "s1", "Step 1", ExecutionStatus.Succeeded,
            "ok", null, DateTime.UtcNow, pars, "trace-output-bytes");
        await notifier.FlushForTestAsync();

        var item = FirstItem(capture);
        item.Type.Should().Be("StepCompleted");
        var evt = item.Event.Should().BeOfType<StepCompletedEvent>().Subject;
        evt.Status.Should().Be("Succeeded");
        evt.OutputParameters.Should().NotBeNull();
        evt.OutputParameters!["host"].Should().Be("web01");
        evt.OutputParameters["count"].Should().Be("42");
        evt.TraceOutput.Should().Be("trace-output-bytes");
    }

    [Fact]
    public async Task StepCompletedAsync_DefenseInDepthRedactsSensitiveParameterNames()
    {
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        ExecutionHub.RegisterGroupForTest("conn-1", execId.ToString());

        await notifier.StepCompletedAsync(
            execId, wfId, "s1", "Step 1", ExecutionStatus.Succeeded,
            "ok", null, DateTime.UtcNow,
            new Dictionary<string, string>
            {
                ["dbPassword"] = "opaque-secret-value",
                ["promptTokens"] = "42",
            });
        await notifier.FlushForTestAsync();

        var evt = FirstItem(capture).Event.Should().BeOfType<StepCompletedEvent>().Subject;
        evt.OutputParameters!["dbPassword"].Should().Be("***");
        evt.OutputParameters["promptTokens"].Should().Be("42");
    }

    [Fact]
    public async Task StepCompletedAsync_PayloadCarriesOutputVariableAlias()
    {
        // The UI databus uses the outputVariable alias to mirror the engine's dual-lookup
        // contract: both {stepId}.output and {alias}.output must show up live. The alias
        // travels on the StepCompleted payload (engine knows it from node.Data.OutputVariable);
        // a null value means the producer node has no alias and the UI should fall back to
        // stepId-only keys.
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        ExecutionHub.RegisterGroupForTest("conn-1", execId.ToString());

        await notifier.StepCompletedAsync(
            execId, wfId, "step-1", "Disk Check", ExecutionStatus.Succeeded,
            "free=42G", null, DateTime.UtcNow,
            outputParameters: null, traceOutput: null, stepType: "runScript",
            startedAt: null, outputVariable: "diskCheck");
        await notifier.FlushForTestAsync();

        var evt = FirstItem(capture).Event.Should().BeOfType<StepCompletedEvent>().Subject;
        evt.OutputVariable.Should().Be("diskCheck");
    }

    [Fact]
    public async Task StepCompletedAsync_NullOutputParameters_DropsEmptyMapFromPayload()
    {
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        ExecutionHub.RegisterGroupForTest("conn-1", execId.ToString());

        await notifier.StepCompletedAsync(
            execId, wfId, "s1", null, ExecutionStatus.Succeeded,
            "ok", null, DateTime.UtcNow, null, null);
        await notifier.FlushForTestAsync();

        var evt = FirstItem(capture).Event.Should().BeOfType<StepCompletedEvent>().Subject;
        evt.OutputParameters.Should().BeNull();
    }

    [Fact]
    public async Task ExecutionStatusChangedAsync_SendsToBothExecutionAndWorkflowGroups()
    {
        // Status events fan to both groups: the per-execution watcher AND the workflow
        // firehose. This is the only event that workflow-group subscribers receive —
        // it powers the status badge in the Live tab without requiring JoinExecution.
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();

        await notifier.ExecutionStatusChangedAsync(execId, wfId, ExecutionStatus.Succeeded, null, DateTime.UtcNow);
        await notifier.FlushForTestAsync();

        capture.Groups.Should().BeEquivalentTo(new[] { execId.ToString(), $"workflow-{wfId}" });
    }

    [Fact]
    public async Task ExecutionStatusChangedAsync_DispatchesStatusString()
    {
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();

        await notifier.ExecutionStatusChangedAsync(execId, wfId, ExecutionStatus.Failed, "boom", DateTime.UtcNow);
        await notifier.FlushForTestAsync();

        var item = FirstItem(capture);
        item.Type.Should().Be("ExecutionStatusChanged");
        var evt = item.Event.Should().BeOfType<ExecutionStatusEvent>().Subject;
        evt.Status.Should().Be("Failed");
        evt.ErrorMessage.Should().Be("boom");
    }

    [Fact]
    public async Task ExecutionStatusChangedAsync_GoesToBatchingChannel_AndArrivesAfterFlush()
    {
        // ExecutionStatusChanged now goes through the shared batching channel so that
        // 200 concurrent terminal events are coalesced into a single flush rather than
        // 200 individual SendAsync calls. The channel capacity (32768) is large enough
        // that status events are never dropped under realistic concurrency.
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();

        await notifier.ExecutionStatusChangedAsync(execId, wfId, ExecutionStatus.Succeeded, null, DateTime.UtcNow);

        // Event is in the channel, not yet on the wire — capture must be empty here.
        capture.Method.Should().BeNull("status event must be in channel until flush");

        await notifier.FlushForTestAsync();

        capture.Method.Should().Be("LiveEventsBatch");
        var item = FirstItem(capture);
        item.Type.Should().Be("ExecutionStatusChanged");
    }

    [Fact]
    public async Task ExecutionStatusChangedAsync_DropsEventWhenNoSubscribers()
    {
        var (notifier, capture) = Build(hasSubscribers: false);

        await notifier.ExecutionStatusChangedAsync(Guid.NewGuid(), Guid.NewGuid(),
            ExecutionStatus.Succeeded, null, DateTime.UtcNow);

        capture.Method.Should().BeNull();
    }

    [Fact]
    public async Task ExecutionStatusChanged_OpsFeed_SendsOnlyToConnectionsWhoseScopeCoversFolder()
    {
        // RBAC leak guard at the notifier level: an event for a workflow in FolderA must be
        // sent only to ops-feed connections scoped to FolderA (plus unrestricted), never to a
        // connection scoped to a different folder.
        var folderA = Guid.NewGuid();
        var folderB = Guid.NewGuid();
        var wfId = Guid.NewGuid();

        ExecutionHub.RegisterOpsFeedForTest("conn-A", unrestricted: false, [folderA]);
        ExecutionHub.RegisterOpsFeedForTest("conn-B", unrestricted: false, [folderB]);

        IReadOnlyList<string>? sentToConnections = null;
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
             .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Clients(It.IsAny<IReadOnlyList<string>>()))
               .Returns(proxy.Object)
               .Callback<IReadOnlyList<string>>(conns => sentToConnections = conns);
        var hub = new Mock<IHubContext<ExecutionHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        // No per-execution/per-workflow group subscribers → the only path is the ops-feed.
        // Folder resolver maps our workflow to FolderA.
        var notifier = new SignalRExecutionNotifier(
            hub.Object,
            hasSubscribers: (_, _) => false,
            folderResolver: (_, _) => Task.FromResult<Guid?>(folderA));

        await notifier.ExecutionStatusChangedAsync(Guid.NewGuid(), wfId, ExecutionStatus.Failed, "boom", DateTime.UtcNow);
        await notifier.FlushForTestAsync();

        sentToConnections.Should().NotBeNull();
        sentToConnections.Should().BeEquivalentTo("conn-A");
        sentToConnections.Should().NotContain("conn-B");
    }

    [Fact]
    public async Task ExecutionStatusChanged_NoOpsFeedAndNoGroupSubscribers_DropsEvent()
    {
        // Both group subscribers absent AND ops-feed empty → nothing is sent.
        var (notifier, capture) = Build(hasSubscribers: false);

        await notifier.ExecutionStatusChangedAsync(Guid.NewGuid(), Guid.NewGuid(),
            ExecutionStatus.Succeeded, null, DateTime.UtcNow);
        await notifier.FlushForTestAsync();

        capture.Method.Should().BeNull();
    }

    [Fact]
    public async Task StepPausedAsync_TakesSnapshotOfVariables_NotLiveReference()
    {
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        ExecutionHub.RegisterGroupForTest("conn-1", execId.ToString());
        var liveVars = new Dictionary<string, string> { ["x"] = "1" };

        await notifier.StepPausedAsync(execId, wfId, "s1", null, liveVars, DateTime.UtcNow, "breakpoint");
        liveVars["x"] = "MUTATED-AFTER-SEND";
        await notifier.FlushForTestAsync();

        var item = FirstItem(capture);
        item.Type.Should().Be("StepPaused");
        var evt = item.Event.Should().BeOfType<StepPausedEvent>().Subject;
        evt.Variables["x"].Should().Be("1");
        ReferenceEquals(evt.Variables, liveVars).Should().BeFalse();
    }

    [Fact]
    public async Task StepResumedAsync_DispatchesStepResumedBatchItem()
    {
        var (notifier, capture) = Build();
        var execId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        ExecutionHub.RegisterGroupForTest("conn-1", execId.ToString());

        await notifier.StepResumedAsync(execId, wfId, "s1");
        await notifier.FlushForTestAsync();

        var item = FirstItem(capture);
        item.Type.Should().Be("StepResumed");
        item.Event.Should().BeOfType<StepResumedEvent>();
    }

    [Fact]
    public async Task SendBatchAsync_SendsAllGroupsConcurrently_NotSequentially()
    {
        // Regression guard: SendBatchAsync previously awaited each group's SendAsync
        // sequentially. With N parallel executions this meant N × send_latency total
        // flush time, overflowing the 50ms flush interval under load.
        // After the fix (Task.WhenAll), all sends fire concurrently — total time stays
        // near the cost of a single send regardless of N.
        const int n = 10;
        var sentGroups = new ConcurrentBag<IReadOnlyList<string>>();
        var started = 0;
        var allSendsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSends = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = new Mock<IClientProxy>();
        proxy.Setup(p => p.SendCoreAsync(
                It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
             .Returns<string, object?[], CancellationToken>(async (_, _, ct) =>
             {
                 if (Interlocked.Increment(ref started) == n)
                     allSendsStarted.TrySetResult();

                 await releaseSends.Task.WaitAsync(ct);
             });

        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Groups(It.IsAny<IReadOnlyList<string>>()))
               .Returns<IReadOnlyList<string>>(groups =>
               {
                   sentGroups.Add(groups);
                   return proxy.Object;
               });

        var hub = new Mock<IHubContext<ExecutionHub>>();
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        // hasSubscribers must return true so all events enter the channel and are sent.
        var notifier = new SignalRExecutionNotifier(hub.Object, hasSubscribers: (_, _) => true);

        for (int i = 0; i < n; i++)
        {
            await notifier.ExecutionStatusChangedAsync(
                Guid.NewGuid(), Guid.NewGuid(), ExecutionStatus.Succeeded, null, DateTime.UtcNow);
        }

        var flush = notifier.FlushForTestAsync();

        await allSendsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        sentGroups.Should().HaveCount(n, "every execution group must have received its batch");

        // All sends must start before any mocked send is released; a sequential loop would hang here.
        releaseSends.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

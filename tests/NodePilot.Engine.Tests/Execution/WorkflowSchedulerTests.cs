using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using System.Collections.Concurrent;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Execution;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

// WorkflowScheduler's step gate is a process-global static semaphore, and several tests here
// call Configure(1) to shrink it to a single slot. Without this attribute the class runs in
// parallel with every other collection in the assembly — including the WorkflowEngine tests that
// drive RunAsync through that same gate (WorkflowEngineCapacityTests even runs a 10-way parallel
// execution). They then contend for the one slot we installed, which skews the timing this file
// depends on. Join the collection those engine tests already share so the gate has one owner.
[Collection("SerialEngineTests")]
public class WorkflowSchedulerTests
{
    private static WorkflowNode Node(string id, string type = "runScript", string config = "{}")
    {
        using var doc = JsonDocument.Parse(config);
        return new WorkflowNode
        {
            Id = id,
            Type = type,
            Data = new WorkflowNodeData { Config = doc.RootElement.Clone() },
        };
    }

    [Fact]
    public void EvaluateSuccessorReadiness_NonJunction_WaitsForAllIncomingSources()
    {
        var node = Node("target");
        var edges = new List<WorkflowEdge>
        {
            new() { Id = "e1", Source = "a", Target = "target" },
            new() { Id = "e2", Source = "b", Target = "target" },
        };
        var completed = new HashSet<string> { "a" };
        var skipped = new HashSet<string>();
        var results = new Dictionary<string, ActivityResult>
        {
            ["a"] = new() { Success = true },
        };

        WorkflowScheduler.EvaluateSuccessorReadiness(node, edges, completed, skipped, results)
            .Ready.Should().BeFalse();

        skipped.Add("b");

        WorkflowScheduler.EvaluateSuccessorReadiness(node, edges, completed, skipped, results)
            .Ready.Should().BeTrue();
    }

    [Fact]
    public void EvaluateSuccessorReadiness_WaitNofM_RequiresConfiguredSuccessfulPredecessors()
    {
        var join = Node("join", "junction", """{"mode":"waitNofM","requiredCount":2}""");
        var edges = new List<WorkflowEdge>
        {
            new() { Id = "e1", Source = "a", Target = "join" },
            new() { Id = "e2", Source = "b", Target = "join" },
            new() { Id = "e3", Source = "c", Target = "join" },
        };
        var completed = new HashSet<string> { "a", "b" };
        var skipped = new HashSet<string>();
        var results = new Dictionary<string, ActivityResult>
        {
            ["a"] = new() { Success = true },
            ["b"] = new() { Success = false },
        };

        var first = WorkflowScheduler.EvaluateSuccessorReadiness(join, edges, completed, skipped, results);
        first.Ready.Should().BeFalse();
        first.JunctionMode.Should().Be("waitNofM");

        results["b"] = new ActivityResult { Success = true };

        WorkflowScheduler.EvaluateSuccessorReadiness(join, edges, completed, skipped, results)
            .Ready.Should().BeTrue();
    }

    [Fact]
    public void MarkSubtreeSkipped_DoesNotCascadeThroughNodeWithLivePredecessor()
    {
        var adjacency = new Dictionary<string, List<string>>
        {
            ["a"] = ["c"],
            ["b"] = ["c"],
            ["c"] = ["d"],
            ["d"] = [],
        };
        var reverseAdjacency = new Dictionary<string, List<string>>
        {
            ["a"] = [],
            ["b"] = [],
            ["c"] = ["a", "b"],
            ["d"] = ["c"],
        };
        var skipped = new HashSet<string>();

        WorkflowScheduler.MarkSubtreeSkipped("a", skipped, adjacency, reverseAdjacency);

        skipped.Should().BeEquivalentTo("a");

        WorkflowScheduler.MarkSubtreeSkipped("b", skipped, adjacency, reverseAdjacency);

        skipped.Should().BeEquivalentTo("a", "b", "c", "d");
    }

    [Fact]
    public async Task RunAsync_WaitAnyJunction_WaitsPastACompletedEdgeWhoseConditionIsFalse()
    {
        var fast = Node("fast");
        var slow = Node("slow");
        var join = Node("join", "junction", """{"mode":"waitAny"}""");
        var final = Node("final");
        var nodes = new[] { fast, slow, join, final };
        var edges = new[]
        {
            new WorkflowEdge { Id = "e1", Source = "fast", Target = "join", Condition = "fast.failed" },
            new WorkflowEdge { Id = "e2", Source = "slow", Target = "join", Condition = "slow.success" },
            new WorkflowEdge { Id = "e3", Source = "join", Target = "final" },
        };
        var adjacency = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var reverse = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var incoming = nodes.ToDictionary(n => n.Id, _ => new List<WorkflowEdge>());
        var byEndpoints = new Dictionary<(string Source, string Target), WorkflowEdge>();
        foreach (var edge in edges)
        {
            adjacency[edge.Source].Add(edge.Target);
            reverse[edge.Target].Add(edge.Source);
            incoming[edge.Target].Add(edge);
            byEndpoints[(edge.Source, edge.Target)] = edge;
        }

        var results = new ConcurrentDictionary<string, ActivityResult>();
        var completed = new HashSet<string>();
        var skipped = new HashSet<string>();

        await WorkflowScheduler.RunAsync(
            [fast, slow], nodes.ToDictionary(n => n.Id), adjacency, reverse, incoming, byEndpoints,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            results, completed, skipped,
            async (node, ct) =>
            {
                await Task.Delay(node.Id == "fast" ? 5 : node.Id == "slow" ? 50 : 1, ct);
                return new ActivityResult { Success = true, Output = node.Id };
            },
            NullLogger.Instance, CancellationToken.None);

        completed.Should().Contain(["join", "final"]);
        skipped.Should().NotContain(["join", "final"]);
    }

    [Fact]
    public async Task RunAsync_WaitAllJunction_DoesNotRunWhenOneCompletedInputConditionIsFalse()
    {
        var failed = Node("failed");
        var successful = Node("successful");
        var join = Node("join", "junction", """{"mode":"waitAll"}""");
        var nodes = new[] { failed, successful, join };
        var edges = new[]
        {
            new WorkflowEdge { Id = "e1", Source = "failed", Target = "join", Condition = "failed.success" },
            new WorkflowEdge { Id = "e2", Source = "successful", Target = "join", Condition = "successful.success" },
        };
        var adjacency = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var reverse = nodes.ToDictionary(n => n.Id, _ => new List<string>());
        var incoming = nodes.ToDictionary(n => n.Id, _ => new List<WorkflowEdge>());
        var byEndpoints = new Dictionary<(string Source, string Target), WorkflowEdge>();
        foreach (var edge in edges)
        {
            adjacency[edge.Source].Add(edge.Target);
            reverse[edge.Target].Add(edge.Source);
            incoming[edge.Target].Add(edge);
            byEndpoints[(edge.Source, edge.Target)] = edge;
        }

        var results = new ConcurrentDictionary<string, ActivityResult>();
        var completed = new HashSet<string>();
        var skipped = new HashSet<string>();

        await WorkflowScheduler.RunAsync(
            [failed, successful], nodes.ToDictionary(n => n.Id), adjacency, reverse, incoming, byEndpoints,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            results, completed, skipped,
            async (node, ct) =>
            {
                await Task.Delay(node.Id == "failed" ? 5 : 50, ct);
                return new ActivityResult { Success = node.Id != "failed", Output = node.Id };
            },
            NullLogger.Instance, CancellationToken.None);

        completed.Should().NotContain("join");
        skipped.Should().Contain("join");
    }

    [Fact]
    public async Task RunAsync_WaitAnyCancelsAndAwaitsRacingInFlightPredecessor()
    {
        var fast = Node("fast");
        var slow = Node("slow");
        var join = Node("join", "junction", """{"mode":"waitAny"}""");
        var final = Node("final");
        var nodes = new List<WorkflowNode> { fast, slow, join, final };
        var nodesById = nodes.ToDictionary(n => n.Id);
        var edges = new List<WorkflowEdge>
        {
            new() { Id = "e1", Source = "fast", Target = "join" },
            new() { Id = "e2", Source = "slow", Target = "join" },
            new() { Id = "e3", Source = "join", Target = "final" },
        };
        var adjacency = new Dictionary<string, List<string>>
        {
            ["fast"] = ["join"],
            ["slow"] = ["join"],
            ["join"] = ["final"],
            ["final"] = [],
        };
        var reverseAdjacency = new Dictionary<string, List<string>>
        {
            ["fast"] = [],
            ["slow"] = [],
            ["join"] = ["fast", "slow"],
            ["final"] = ["join"],
        };
        var incomingEdgesByTarget = nodes.ToDictionary(n => n.Id, _ => new List<WorkflowEdge>());
        var activeEdgeByEndpoints = new Dictionary<(string Source, string Target), WorkflowEdge>();
        foreach (var edge in edges)
        {
            incomingEdgesByTarget[edge.Target].Add(edge);
            activeEdgeByEndpoints.TryAdd((edge.Source, edge.Target), edge);
        }

        var results = new ConcurrentDictionary<string, ActivityResult>();
        var completed = new HashSet<string>();
        var skipped = new HashSet<string>();
        var slowCancelled = false;

        await WorkflowScheduler.RunAsync(
            [fast, slow],
            nodesById,
            adjacency,
            reverseAdjacency,
            incomingEdgesByTarget,
            activeEdgeByEndpoints,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            results,
            completed,
            skipped,
            async (node, ct) =>
            {
                if (node.Id == "slow")
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        slowCancelled = true;
                        return new ActivityResult { Success = false, ErrorOutput = "cancelled" };
                    }
                }
                else if (node.Id == "fast")
                {
                    await Task.Delay(10, ct);
                }

                return new ActivityResult { Success = true, Output = node.Id };
            },
            NullLogger.Instance,
            CancellationToken.None);

        slowCancelled.Should().BeTrue();
        completed.Should().Contain(new[] { "fast", "slow", "join", "final" });
        results["final"].Success.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WithGlobalStepCap_LimitsConcurrentInFlightSteps()
    {
        // Per-process step concurrency gate — caps in-flight steps across ALL executions.
        // Prevents 50 parallel workflows × ~10 fan-out = 500 concurrent step tasks from
        // saturating ThreadPool / DbContext / regex passes. Ten sibling roots all kick
        // off at once but only 2 may run concurrently when Configure(2) is set.
        WorkflowScheduler.ResetForTests();
        WorkflowScheduler.Configure(2);
        try
        {
            var roots = Enumerable.Range(0, 10).Select(i => Node($"r{i}")).ToList();
            var nodesById = roots.ToDictionary(n => n.Id);
            var adjacency = roots.ToDictionary(n => n.Id, _ => new List<string>());
            var reverseAdjacency = roots.ToDictionary(n => n.Id, _ => new List<string>());
            var incomingEdgesByTarget = roots.ToDictionary(n => n.Id, _ => new List<WorkflowEdge>());
            var activeEdgeByEndpoints = new Dictionary<(string Source, string Target), WorkflowEdge>();

            var results = new ConcurrentDictionary<string, ActivityResult>();
            var completed = new HashSet<string>();
            var skipped = new HashSet<string>();

            int concurrent = 0;
            int peak = 0;

            await WorkflowScheduler.RunAsync(
                roots,
                nodesById,
                adjacency,
                reverseAdjacency,
                incomingEdgesByTarget,
                activeEdgeByEndpoints,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                results,
                completed,
                skipped,
                async (node, ct) =>
                {
                    var current = Interlocked.Increment(ref concurrent);
                    // Atomic max — Interlocked.Exchange wins the CAS race against other threads.
                    int snapshot;
                    do { snapshot = peak; } while (current > snapshot
                        && Interlocked.CompareExchange(ref peak, current, snapshot) != snapshot);
                    try
                    {
                        await Task.Delay(20, ct);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref concurrent);
                    }
                    return new ActivityResult { Success = true, Output = node.Id };
                },
                NullLogger.Instance,
                CancellationToken.None);

            completed.Should().HaveCount(10);
            peak.Should().BeLessThanOrEqualTo(2,
                "Configure(2) caps in-flight steps; the runner must not exceed it");
        }
        finally
        {
            WorkflowScheduler.ResetForTests();
        }
    }

    [Fact]
    public async Task RunWithCurrentStepGateReleased_AllowsQueuedStepToRunWhileParentWaits()
    {
        WorkflowScheduler.ResetForTests();
        WorkflowScheduler.Configure(1);
        try
        {
            var holder = Node("holder");
            var follower = Node("follower");
            var roots = new[] { holder, follower };
            var nodesById = roots.ToDictionary(n => n.Id);
            var adjacency = roots.ToDictionary(n => n.Id, _ => new List<string>());
            var reverseAdjacency = roots.ToDictionary(n => n.Id, _ => new List<string>());
            var incomingEdgesByTarget = roots.ToDictionary(n => n.Id, _ => new List<WorkflowEdge>());
            var activeEdgeByEndpoints = new Dictionary<(string Source, string Target), WorkflowEdge>();
            var results = new ConcurrentDictionary<string, ActivityResult>();
            var completed = new HashSet<string>();
            var skipped = new HashSet<string>();
            var holderReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var followerRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            await WorkflowScheduler.RunAsync(
                roots,
                nodesById,
                adjacency,
                reverseAdjacency,
                incomingEdgesByTarget,
                activeEdgeByEndpoints,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                results,
                completed,
                skipped,
                async (node, ct) =>
                {
                    if (node.Id == "holder")
                    {
                        return await WorkflowScheduler.RunWithCurrentStepGateReleasedAsync(async () =>
                        {
                            holderReleased.SetResult();
                            await followerRan.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                            return new ActivityResult { Success = true, Output = "holder" };
                        }, ct);
                    }

                    await holderReleased.Task.WaitAsync(TimeSpan.FromSeconds(2), ct);
                    followerRan.SetResult();
                    return new ActivityResult { Success = true, Output = "follower" };
                },
                NullLogger.Instance,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

            completed.Should().BeEquivalentTo("holder", "follower");
            results["holder"].Success.Should().BeTrue();
            results["follower"].Success.Should().BeTrue();
        }
        finally
        {
            WorkflowScheduler.ResetForTests();
        }
    }

    [Fact]
    public async Task RunAsync_WaitAnyJunction_DoesNotPropagateGateCancelOce()
    {
        // Reproduces the regression where sibling steps queued on the global step-gate
        // got cancelled by a waitAny-junction race; gate.WaitAsync(stepCt) threw OCE
        // *before* StepRunner could swallow it, and the exception bubbled up to
        // WorkflowEngine's catch (OperationCanceledException), wrongly marking the whole
        // execution as Cancelled. After the fix, the scheduler swallows the gate-wait OCE
        // and treats the step like SkipRequested so the workflow continues.
        //
        // Setup: gate=1, fast holds the slot. Five queued siblings all call
        // gate.WaitAsync — when fast completes, only one can grab the slot synchronously,
        // the other four stay parked in WaitAsync. The junction-race then cancels all
        // five CTSs; the four still in WaitAsync throw OCE. Without the fix the very
        // first OCE crashes RunAsync via `await completedTask` and the test never returns.
        WorkflowScheduler.ResetForTests();
        WorkflowScheduler.Configure(1);
        try
        {
            var fast = Node("fast");
            var queuedNodes = Enumerable.Range(0, 5).Select(i => Node($"q{i}")).ToList();
            var queuedIds = queuedNodes.Select(n => n.Id).ToHashSet();
            var join = Node("join", "junction", """{"mode":"waitAny"}""");
            var final = Node("final");
            var roots = new List<WorkflowNode> { fast };
            roots.AddRange(queuedNodes);
            var allNodes = new List<WorkflowNode>(roots) { join, final };
            var nodesById = allNodes.ToDictionary(n => n.Id);

            var edges = new List<WorkflowEdge>
            {
                new() { Id = "e_fast", Source = "fast", Target = "join" },
            };
            edges.AddRange(queuedNodes.Select((n, i) => new WorkflowEdge
            {
                Id = $"e_q{i}", Source = n.Id, Target = "join",
            }));
            edges.Add(new WorkflowEdge { Id = "e_final", Source = "join", Target = "final" });

            var adjacency = allNodes.ToDictionary(n => n.Id, _ => new List<string>());
            var reverseAdjacency = allNodes.ToDictionary(n => n.Id, _ => new List<string>());
            foreach (var e in edges)
            {
                adjacency[e.Source].Add(e.Target);
                reverseAdjacency[e.Target].Add(e.Source);
            }

            var incomingEdgesByTarget = allNodes.ToDictionary(n => n.Id, _ => new List<WorkflowEdge>());
            var activeEdgeByEndpoints = new Dictionary<(string Source, string Target), WorkflowEdge>();
            foreach (var edge in edges)
            {
                incomingEdgesByTarget[edge.Target].Add(edge);
                activeEdgeByEndpoints.TryAdd((edge.Source, edge.Target), edge);
            }

            var results = new ConcurrentDictionary<string, ActivityResult>();
            var completed = new HashSet<string>();
            var skipped = new HashSet<string>();

            await WorkflowScheduler.RunAsync(
                roots,
                nodesById,
                adjacency,
                reverseAdjacency,
                incomingEdgesByTarget,
                activeEdgeByEndpoints,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                results,
                completed,
                skipped,
                async (node, ct) =>
                {
                    // The five queued siblings block until cancelled — that is what makes this
                    // test deterministic. Releasing the gate and cancelling the race losers are
                    // concurrent, unordered events: there is no happens-before edge between
                    // fast's `finally { gate.Release(); }` and the scheduler continuation that
                    // cancels the siblings. With a finite sibling delay, a loaded runner (CI also
                    // pays for XPlat coverage instrumentation) lets the 1-slot gate hand off
                    // several siblings that run to completion and legitimately land in
                    // `completed` — correct engine behaviour, but it breaks the assertion below.
                    // Blocking forever removes the race: whichever sibling grabs the slot can
                    // never finish on its own, so the remaining four stay parked in
                    // gate.WaitAsync and all five end up cancelled. The regression path under
                    // test (OCE thrown out of gate.WaitAsync) is still covered by those four.
                    // join and final must NOT block — they run after the race is decided and the
                    // workflow has to reach its end for the assertions to mean anything.
                    if (node.Id == "fast") await Task.Delay(50, ct);
                    else if (queuedIds.Contains(node.Id)) await Task.Delay(Timeout.Infinite, ct);
                    else await Task.Delay(5, ct);
                    return new ActivityResult { Success = true, Output = node.Id };
                },
                NullLogger.Instance,
                CancellationToken.None)
                // Safety net: the queued siblings only ever end via cancellation, so a regression
                // that stops cancelling them would hang this test — and with it the CI job —
                // instead of failing. Fail loudly after 30s rather than blocking the runner.
                .WaitAsync(TimeSpan.FromSeconds(30));

            // Workflow must complete cleanly — no OCE escaping the scheduler.
            completed.Should().Contain(new[] { "fast", "join", "final" });
            results["final"].Success.Should().BeTrue();

            // Gate-cancelled siblings must end up in `skipped`, NOT in `completed`. The end-of-
            // execution writeback in WorkflowEngine.ExecuteAsync persists a Skipped StepExecution
            // row only for nodes in `skipped` AND not in `completed`. Before the fix these
            // nodes were added to `completed`, which silently dropped their row and caused
            // the same workflow to produce different StepExecution counts per run.
            foreach (var queued in queuedNodes)
            {
                skipped.Should().Contain(queued.Id, $"{queued.Id} was cancelled while parked on the gate");
                completed.Should().NotContain(queued.Id, $"{queued.Id} never ran — must not be in completed");
                results.Should().ContainKey(queued.Id);
            }
        }
        finally
        {
            WorkflowScheduler.ResetForTests();
        }
    }

    [Fact]
    public async Task RunAsync_WithGateDisabled_RunsAllStepsConcurrently()
    {
        // Configure(<=0) disables the gate — useful when operators tune for max throughput
        // and accept full ThreadPool saturation. All ten sibling roots run in parallel.
        WorkflowScheduler.ResetForTests();
        WorkflowScheduler.Configure(0);
        try
        {
            var roots = Enumerable.Range(0, 10).Select(i => Node($"r{i}")).ToList();
            var nodesById = roots.ToDictionary(n => n.Id);
            var adjacency = roots.ToDictionary(n => n.Id, _ => new List<string>());
            var reverseAdjacency = roots.ToDictionary(n => n.Id, _ => new List<string>());
            var incomingEdgesByTarget = roots.ToDictionary(n => n.Id, _ => new List<WorkflowEdge>());
            var activeEdgeByEndpoints = new Dictionary<(string Source, string Target), WorkflowEdge>();
            var results = new ConcurrentDictionary<string, ActivityResult>();
            var completed = new HashSet<string>();
            var skipped = new HashSet<string>();

            int concurrent = 0;
            int peak = 0;

            await WorkflowScheduler.RunAsync(
                roots, nodesById, adjacency, reverseAdjacency,
                incomingEdgesByTarget, activeEdgeByEndpoints,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                results, completed, skipped,
                async (_, ct) =>
                {
                    var current = Interlocked.Increment(ref concurrent);
                    int snapshot;
                    do { snapshot = peak; } while (current > snapshot
                        && Interlocked.CompareExchange(ref peak, current, snapshot) != snapshot);
                    try { await Task.Delay(20, ct); }
                    finally { Interlocked.Decrement(ref concurrent); }
                    return new ActivityResult { Success = true };
                },
                NullLogger.Instance,
                CancellationToken.None);

            peak.Should().BeGreaterThan(2, "with the gate disabled, all ready steps should run concurrently");
        }
        finally
        {
            WorkflowScheduler.ResetForTests();
        }
    }

    /// <summary>
    /// Regression: <see cref="ForEachActivity"/> waits on child executions whose own steps draw
    /// from the same global step gate. It must therefore release its slot while waiting, exactly
    /// as <c>StartWorkflowActivity</c> does — otherwise a parent starves the children it is
    /// waiting for and the run deadlocks rather than merely running slowly.
    ///
    /// <para>The setup reproduces the deadlock at the smallest scale that can express it: one
    /// gate slot, a forEach step and a sibling step, where the forEach child cannot finish until
    /// the sibling has run. Without the release this times out; with it, both complete.</para>
    /// </summary>
    [Fact]
    public async Task ForEach_ReleasesTheStepGate_WhileWaitingOnChildExecutions()
    {
        WorkflowScheduler.ResetForTests();
        WorkflowScheduler.Configure(1);
        using var db = Helpers.TestDbContext.Create();
        try
        {
            var child = new Workflow
            {
                Id = Guid.NewGuid(),
                Name = "GateChild",
                DefinitionJson = "{}",
                IsEnabled = true,
            };
            db.Workflows.Add(child);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            var siblingRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var services = new ServiceCollection();
            services.AddSingleton<IWorkflowEngine>(new GateProbeEngine(siblingRan.Task));
            var forEach = new ForEachActivity(
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                db,
                new InMemorySubWorkflowGate());

            var config = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["childWorkflowNameOrId"] = child.Id.ToString(),
                ["items"] = "only-item",
                ["itemsFormat"] = "lines",
            });

            var roots = new[] { Node("foreach", "forEach", config), Node("sibling") };
            var nodesById = roots.ToDictionary(n => n.Id);
            var adjacency = roots.ToDictionary(n => n.Id, _ => new List<string>());
            var reverseAdjacency = roots.ToDictionary(n => n.Id, _ => new List<string>());
            var incomingEdgesByTarget = roots.ToDictionary(n => n.Id, _ => new List<WorkflowEdge>());
            var activeEdgeByEndpoints = new Dictionary<(string Source, string Target), WorkflowEdge>();
            var results = new ConcurrentDictionary<string, ActivityResult>();
            var completed = new HashSet<string>();
            var skipped = new HashSet<string>();

            await WorkflowScheduler.RunAsync(
                roots, nodesById, adjacency, reverseAdjacency,
                incomingEdgesByTarget, activeEdgeByEndpoints,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                results, completed, skipped,
                async (node, ct) =>
                {
                    if (node.Id == "foreach")
                    {
                        return await forEach.ExecuteAsync(
                            new StepExecutionContext { WorkflowExecutionId = Guid.NewGuid(), StepId = "foreach" },
                            node.Data!.Config,
                            ct);
                    }

                    siblingRan.TrySetResult();
                    return new ActivityResult { Success = true, Output = "sibling" };
                },
                NullLogger.Instance,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            completed.Should().BeEquivalentTo("foreach", "sibling");
            results["foreach"].Success.Should().BeTrue(
                "the child execution completes once the sibling got its gate slot");
        }
        finally
        {
            WorkflowScheduler.ResetForTests();
        }
    }

    [Fact]
    public async Task RunAsync_FailedStep_LogsTheFailureWithoutTheRawErrorPayload()
    {
        // M-31. StepRunner returns the RAW ActivityResult on purpose — the data bus has to resolve
        // {{step.error}} to the real value — and redacts only on the way out to the DB, the UI,
        // telemetry and the support log. The scheduler used to interpolate result.ErrorOutput into
        // a LogWarning, which made the main log (and any SIEM shipping it) the single sink that saw
        // unredacted stderr while the UI showed "***".
        WorkflowScheduler.ResetForTests();
        const string secret = "Login failed for user 'sa' with password=Sup3rSecret!";

        var failing = Node("boom");
        var results = new ConcurrentDictionary<string, ActivityResult>();
        var completed = new HashSet<string>();
        var skipped = new HashSet<string>();
        var logger = new CapturingLogger();

        await WorkflowScheduler.RunAsync(
            [failing],
            new Dictionary<string, WorkflowNode> { ["boom"] = failing },
            new Dictionary<string, List<string>> { ["boom"] = [] },
            new Dictionary<string, List<string>> { ["boom"] = [] },
            new Dictionary<string, List<WorkflowEdge>> { ["boom"] = [] },
            new Dictionary<(string Source, string Target), WorkflowEdge>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            results,
            completed,
            skipped,
            (_, _) => Task.FromResult(new ActivityResult { Success = false, ErrorOutput = secret }),
            logger,
            CancellationToken.None);

        results["boom"].ErrorOutput.Should().Be(secret,
            "the graph still needs the real value so {{boom.error}} resolves downstream");

        logger.Messages.Should().NotContain(m => m.Contains("Sup3rSecret", StringComparison.Ordinal),
            "the scheduler must never echo raw activity output into the main log");
        logger.Messages.Should().Contain(m => m.Contains("boom", StringComparison.Ordinal),
            "the failure itself must stay visible at scheduler level");
    }

    /// <summary>
    /// Child engine for <see cref="ForEach_ReleasesTheStepGate_WhileWaitingOnChildExecutions"/>:
    /// the child execution only finishes after the sibling step has run, which can only happen
    /// if forEach gave up its gate slot.
    /// </summary>
    private sealed class GateProbeEngine(Task siblingRan) : IWorkflowEngine
    {
        public async Task<WorkflowExecution> ExecuteAsync(
            Workflow workflow, string triggeredBy, CancellationToken ct,
            Dictionary<string, string>? inputParameters = null,
            int? timeoutSeconds = null,
            bool debugEnabled = false,
            Guid? startedByUserId = null,
            Guid? parentExecutionId = null,
            int callDepth = 0,
            Guid? executionIdOverride = null,
            bool interactiveRun = false)
        {
            await siblingRan.WaitAsync(TimeSpan.FromSeconds(8), ct);
            return new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflow.Id,
                Status = ExecutionStatus.Succeeded,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
            };
        }

        public Task<bool> CancelAsync(Guid executionId, string? cancelledBy = null, CancellationToken ct = default)
            => Task.FromResult(false);

        public bool Resume(Guid executionId, string stepId, DebugResumeCommand command,
            IReadOnlyDictionary<string, string>? overrides = null)
            => false;

        public IReadOnlyCollection<string> GetPausedSteps(Guid executionId) => [];
    }
}

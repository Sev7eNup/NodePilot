using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using System.Collections.Concurrent;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

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
                    // fast holds the gate for 50ms — long enough for all five queued
                    // siblings to be parked in gate.WaitAsync. After fast completes,
                    // the scheduler's junction-race cancels every queued sibling's CTS
                    // while four of them are still inside WaitAsync.
                    if (node.Id == "fast") await Task.Delay(50, ct);
                    else await Task.Delay(5, ct);
                    return new ActivityResult { Success = true, Output = node.Id };
                },
                NullLogger.Instance,
                CancellationToken.None);

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
}

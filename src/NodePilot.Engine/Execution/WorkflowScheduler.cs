using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Conditions;

namespace NodePilot.Engine.Execution;

public static class WorkflowScheduler
{
    private sealed class InFlightStep(WorkflowNode node, CancellationTokenSource cancellation)
    {
        public WorkflowNode Node { get; } = node;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool SkipRequested { get; set; }
    }

    private sealed class StepGateLease(SemaphoreSlim gate)
    {
        public SemaphoreSlim Gate { get; } = gate;
        public bool IsHeld { get; set; } = true;
    }

    // Global step-concurrency gate. Without this, hundreds of parallel workflow steps
    // contend for the ThreadPool, DbContext writes, OutputRedactor regex passes, and DI
    // scope creation — sustained 100% CPU on moderately-sized hosts.
    //
    // The cap trades parallelism for steady-state CPU. Default = ProcessorCount * 32 so
    // a 16-core host caps at 512 concurrent in-flight steps. Sized to absorb a bursty
    // 50-execution × 12-branch fan-out (~600 step tasks) without making most of them
    // queue on the gate, where a junction-race cancel would have to be unwound through
    // exception paths. Set <= 0 to disable the gate entirely.
    //
    // Deadlock note: startWorkflow(waitForCompletion=true) releases its step slot while
    // waiting on the child workflow, then reacquires it before returning the child result.
    // That keeps parent waiters from consuming the entire global step pool.
    private static volatile SemaphoreSlim? _stepSemaphore;
    private static int _maxConcurrentSteps = Environment.ProcessorCount * 32;
    private static readonly object _semaphoreLock = new();
    private static readonly AsyncLocal<StepGateLease?> _currentStepGateLease = new();

    /// <summary>
    /// Configures the global concurrent-step cap. Called once at startup from
    /// <c>Program.cs</c>. Subsequent calls replace the semaphore — only safe before
    /// any execution has started, so do not retune at runtime without quiescing.
    /// </summary>
    public static void Configure(int maxConcurrentSteps)
    {
        lock (_semaphoreLock)
        {
            _maxConcurrentSteps = maxConcurrentSteps;
            _stepSemaphore = null;
        }
    }

    private static SemaphoreSlim? GetSemaphore()
    {
        if (_stepSemaphore is { } existing) return existing;
        lock (_semaphoreLock)
        {
            if (_stepSemaphore is { } cached) return cached;
            if (_maxConcurrentSteps <= 0) return null;
            _stepSemaphore = new SemaphoreSlim(_maxConcurrentSteps, _maxConcurrentSteps);
            return _stepSemaphore;
        }
    }

    /// <summary>Test hook: reset the semaphore so unit tests can configure freely.</summary>
    internal static void ResetForTests()
    {
        lock (_semaphoreLock)
        {
            _stepSemaphore = null;
            _maxConcurrentSteps = Environment.ProcessorCount * 8;
        }
    }

    internal static async Task<T> RunWithCurrentStepGateReleasedAsync<T>(
        Func<Task<T>> work,
        CancellationToken reacquireCt)
    {
        var lease = _currentStepGateLease.Value;
        if (lease is null || !lease.IsHeld)
            return await work().ConfigureAwait(false);

        lease.Gate.Release();
        lease.IsHeld = false;
        var previous = _currentStepGateLease.Value;
        _currentStepGateLease.Value = null;
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            await lease.Gate.WaitAsync(reacquireCt).ConfigureAwait(false);
            lease.IsHeld = true;
            _currentStepGateLease.Value = previous;
        }
    }

    /// <summary>
    /// Event-driven scheduling loop: dequeues ready nodes, starts them as tasks, waits
    /// for any to complete, evaluates successors. Supports junction modes and waitAny racing.
    /// </summary>
    internal static async Task RunAsync(
        IReadOnlyCollection<WorkflowNode> rootNodes,
        IReadOnlyDictionary<string, WorkflowNode> nodesById,
        Dictionary<string, List<string>> adjacency,
        Dictionary<string, List<string>> reverseAdjacency,
        IReadOnlyDictionary<string, List<WorkflowEdge>> incomingEdgesByTarget,
        IReadOnlyDictionary<(string Source, string Target), WorkflowEdge> activeEdgeByEndpoints,
        IReadOnlyDictionary<string, string> outputVariableToStepId,
        ConcurrentDictionary<string, ActivityResult> results,
        HashSet<string> completed,
        HashSet<string> skipped,
        Func<WorkflowNode, CancellationToken, Task<ActivityResult>> executeStepAsync,
        ILogger logger,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? globalVariables = null,
        IReadOnlyDictionary<string, string>? inputParameters = null)
    {
        var queue = new Queue<WorkflowNode>(rootNodes);
        var enqueued = new HashSet<string>(rootNodes.Select(n => n.Id));
        var inFlight = new Dictionary<Task<ActivityResult>, InFlightStep>();
        var gate = GetSemaphore();

        while (queue.Count > 0 || inFlight.Count > 0)
        {
            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                if (skipped.Contains(n.Id)) continue;
                var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var task = gate is null
                    ? executeStepAsync(n, stepCts.Token)
                    : ExecuteWithGateAsync(gate, n, stepCts.Token, executeStepAsync);
                inFlight[task] = new InFlightStep(n, stepCts);
            }

            if (inFlight.Count == 0) continue;

            var completedTask = await Task.WhenAny(inFlight.Keys);
            var entry = inFlight[completedTask];
            var node = entry.Node;
            inFlight.Remove(completedTask);
            ActivityResult result;
            // True only when the OCE was raised by a gate-WaitAsync that we cancelled, i.e.
            // the step never reached StepRunner and therefore wrote no StepExecution row.
            // Distinct from `entry.SkipRequested`, which is also set during the junction-race
            // fanout below for past-the-gate siblings whose StepRunner already wrote a
            // Cancelled row through its own OCE catch.
            bool gateRaceCancelled = false;
            try
            {
                result = await completedTask;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && entry.Cancellation.IsCancellationRequested)
            {
                // Junction-Race: a waitAny/waitNofM successor was satisfied while this step
                // was still queued on the global step-gate semaphore. The scheduler cancelled
                // its per-step CTS (further down in this loop) and gate.WaitAsync(stepCt)
                // threw before StepRunner could swallow it. Without this catch, the OCE
                // propagates up to WorkflowEngine.ExecuteAsync's catch (OperationCanceledException)
                // and the entire execution is marked Cancelled — even though the workflow
                // logically should continue along the winning branch.
                EngineMetrics.Cancellations.Add(1,
                    new KeyValuePair<string, object?>("reason", "junction_race"));
                result = new ActivityResult { Success = false, ErrorOutput = "Step skipped by junction race." };
                entry.SkipRequested = true;
                gateRaceCancelled = true;
            }
            finally
            {
                entry.Cancellation.Dispose();
            }

            results[node.Id] = result;

            if (gateRaceCancelled)
            {
                // Route this node into `skipped` (not `completed`) so the end-of-execution
                // writeback in WorkflowEngine.ExecuteAsync persists a StepExecution row with
                // Status=Skipped. Without this, gate-cancelled steps disappeared from the
                // StepExecutions table entirely — StepRunner never ran, so no Cancelled row
                // was written there; and putting them in `completed` excluded them from the
                // end-of-execution skipped pass. Symptom: identical workflow + parameters
                // produced different StepExecution row counts per run.
                skipped.Add(node.Id);
                continue;
            }

            completed.Add(node.Id);

            if (entry.SkipRequested || skipped.Contains(node.Id))
                continue;

            if (!result.Success)
                logger.LogWarning("Step {StepId} failed: {Error}", node.Id, result.ErrorOutput);

            foreach (var successor in adjacency[node.Id])
            {
                if (enqueued.Contains(successor) || skipped.Contains(successor)) continue;

                var successorNode = nodesById[successor];
                var (ready, junctionMode) = EvaluateSuccessorReadiness(
                    successorNode, incomingEdgesByTarget[successor], completed, skipped, results);

                if (!ready) continue;

                activeEdgeByEndpoints.TryGetValue((node.Id, successor), out var incomingEdge);
                if (incomingEdge is not null && !ConditionEvaluator.EvaluateEdge(incomingEdge, results, outputVariableToStepId, globalVariables, inputParameters))
                {
                    MarkSubtreeSkipped(successor, skipped, adjacency, reverseAdjacency);
                    continue;
                }

                queue.Enqueue(successorNode);
                enqueued.Add(successor);

                if (string.Equals(successorNode.Type, "junction", StringComparison.OrdinalIgnoreCase) && junctionMode != "waitAll")
                {
                    var preds = incomingEdgesByTarget[successor];
                    foreach (var predEdge in preds)
                    {
                        if (!completed.Contains(predEdge.Source) && !skipped.Contains(predEdge.Source))
                            MarkSubtreeSkipped(predEdge.Source, skipped, adjacency, reverseAdjacency, stopAtNode: successor);
                    }

                    foreach (var inFlightStep in inFlight.Values)
                    {
                        if (skipped.Contains(inFlightStep.Node.Id))
                        {
                            inFlightStep.SkipRequested = true;
                            if (!inFlightStep.Cancellation.IsCancellationRequested)
                                await inFlightStep.Cancellation.CancelAsync();
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Wraps the per-step task with a global semaphore acquire/release. Cancellation tokens
    /// are observed during the wait so a workflow-level cancel doesn't leave gates held.
    /// </summary>
    private static async Task<ActivityResult> ExecuteWithGateAsync(
        SemaphoreSlim gate,
        WorkflowNode node,
        CancellationToken stepCt,
        Func<WorkflowNode, CancellationToken, Task<ActivityResult>> executeStepAsync)
    {
        await gate.WaitAsync(stepCt).ConfigureAwait(false);
        var previousLease = _currentStepGateLease.Value;
        var lease = new StepGateLease(gate);
        _currentStepGateLease.Value = lease;
        try
        {
            return await executeStepAsync(node, stepCt).ConfigureAwait(false);
        }
        finally
        {
            _currentStepGateLease.Value = previousLease;
            if (lease.IsHeld)
                gate.Release();
        }
    }

    /// <summary>
    /// Computes whether a successor node is ready to be scheduled. Non-junction nodes require
    /// every incoming edge source to be completed-or-skipped. Junction nodes apply their mode:
    /// waitAll mirrors the non-junction rule, waitAny needs one success, and waitNofM needs
    /// requiredCount successes.
    /// </summary>
    internal static (bool Ready, string JunctionMode) EvaluateSuccessorReadiness(
        WorkflowNode successorNode,
        IReadOnlyList<WorkflowEdge> incomingEdges,
        HashSet<string> completed,
        HashSet<string> skipped,
        IReadOnlyDictionary<string, ActivityResult> results)
    {
        if (!string.Equals(successorNode.Type, "junction", StringComparison.OrdinalIgnoreCase))
        {
            var ready = incomingEdges.All(e => completed.Contains(e.Source) || skipped.Contains(e.Source));
            return (ready, "waitAll");
        }

        var cfg = successorNode.Data.Config;
        var junctionMode = cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("mode", out var mEl)
            ? mEl.GetString() ?? "waitAll"
            : "waitAll";

        var total = incomingEdges.Count;
        var completedSuccessPreds = incomingEdges.Count(e =>
            completed.Contains(e.Source) && results.TryGetValue(e.Source, out var r) && r.Success);
        var completedOrSkippedPreds = incomingEdges.Count(e =>
            completed.Contains(e.Source) || skipped.Contains(e.Source));

        var requiredCount = junctionMode switch
        {
            "waitAny" => 1,
            "waitNofM" => cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("requiredCount", out var rc)
                ? rc.GetInt32()
                : 1,
            _ => total,
        };

        var isReady = junctionMode == "waitAll"
            ? completedOrSkippedPreds >= total
            : completedSuccessPreds >= requiredCount;

        return (isReady, junctionMode);
    }

    /// <summary>
    /// Marks rootId as skipped and propagates downwards. A descendant is only skipped if
    /// all its predecessors are already skipped, so live alternative paths are preserved.
    /// </summary>
    internal static void MarkSubtreeSkipped(
        string rootId,
        HashSet<string> skipped,
        Dictionary<string, List<string>> adjacency,
        Dictionary<string, List<string>>? reverseAdjacency = null,
        string? stopAtNode = null)
    {
        var stack = new Stack<string>();
        stack.Push(rootId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == stopAtNode) continue;

            if (current != rootId && reverseAdjacency is not null
                && reverseAdjacency.TryGetValue(current, out var preds) && preds.Count > 0
                && preds.Any(p => !skipped.Contains(p)))
            {
                continue;
            }

            if (!skipped.Add(current)) continue;
            if (!adjacency.TryGetValue(current, out var successors)) continue;
            foreach (var next in successors)
            {
                if (next == stopAtNode) continue;
                stack.Push(next);
            }
        }
    }
}

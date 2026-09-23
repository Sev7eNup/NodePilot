using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Agents;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

[Collection("SerialEngineTests")]
public sealed class AgentGateTests
{
    [Fact]
    public async Task TwoParentsAwaitChildAgents_ReleaseBothGates_WithoutDeadlock()
    {
        WorkflowScheduler.ResetForTests(); WorkflowScheduler.Configure(2);
        using var gate = new AgentExecutionGate(Microsoft.Extensions.Options.Options.Create(new AgentOptions { MaxConcurrentRuns = 2 }));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var entered = 0;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var children = 0;
        try
        {
            await Schedule(["parent1", "parent2"], async (_, ct) =>
            {
                using var parentLease = await gate.AcquireAsync(ct);
                if (Interlocked.Increment(ref entered) == 2) barrier.SetResult();
                await barrier.Task.WaitAsync(ct);
                return await parentLease.WhileReleasedAsync(() => WorkflowScheduler.RunWithCurrentStepGateReleasedAsync(async () =>
                {
                    await Schedule(["child"], async (_, token) =>
                    {
                        using var childLease = await gate.AcquireAsync(token);
                        Interlocked.Increment(ref children);
                        return new ActivityResult { Success = true };
                    }, ct);
                    return new ActivityResult { Success = true };
                }, ct), ct);
            }, deadline.Token);
            Assert.Equal(2, children);
            using var first = await gate.AcquireAsync(deadline.Token);
            using var second = await gate.AcquireAsync(deadline.Token);
        }
        finally { WorkflowScheduler.ResetForTests(); }
    }

    [Fact]
    public async Task CancelledReacquisition_DoesNotReleaseAnotherOwnersSlot()
    {
        using var gate = new AgentExecutionGate(Microsoft.Extensions.Options.Options.Create(new AgentOptions { MaxConcurrentRuns = 1 }));
        var parent = await gate.AcquireAsync(TestContext.Current.CancellationToken);
        AgentExecutionGate.Lease? child = null;
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parent.WhileReleasedAsync(async () =>
        {
            child = await gate.AcquireAsync(TestContext.Current.CancellationToken);
            cancelled.Cancel();
            return true;
        }, cancelled.Token));
        parent.Dispose();
        using var waiter = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.AcquireAsync(waiter.Token));
        child!.Dispose();
        using var available = await gate.AcquireAsync(TestContext.Current.CancellationToken);
    }

    private static Task Schedule(string[] ids, Func<WorkflowNode, CancellationToken, Task<ActivityResult>> execute, CancellationToken ct)
    {
        var nodes = ids.Select(id => new WorkflowNode { Id = id, Type = "aiAgent", Data = new WorkflowNodeData { Config = JsonSerializer.SerializeToElement(new { }) } }).ToArray();
        return WorkflowScheduler.RunAsync(nodes, nodes.ToDictionary(n => n.Id), ids.ToDictionary(id => id, _ => new List<string>()),
            ids.ToDictionary(id => id, _ => new List<string>()), ids.ToDictionary(id => id, _ => new List<WorkflowEdge>()),
            new Dictionary<(string, string), WorkflowEdge>(), new Dictionary<string, string>(), new ConcurrentDictionary<string, ActivityResult>(),
            [], [], execute, NullLogger.Instance, ct);
    }
}

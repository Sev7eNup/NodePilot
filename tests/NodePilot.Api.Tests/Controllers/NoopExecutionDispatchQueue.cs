using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Tests.Controllers;

internal sealed class NoopExecutionDispatchQueue : IExecutionDispatchQueue
{
    public ValueTask EnqueueAsync(
        Func<CancellationToken, Task> workItem,
        CancellationToken ct,
        ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal)
        => ValueTask.CompletedTask;
}

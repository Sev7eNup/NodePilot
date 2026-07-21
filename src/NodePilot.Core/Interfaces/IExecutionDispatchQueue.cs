namespace NodePilot.Core.Interfaces;

/// <summary>
/// Bounded in-memory dispatch queue for detached workflow-start work items. API endpoints
/// and engine activities enqueue lightweight callbacks; a host-owned worker drains them.
/// </summary>
public interface IExecutionDispatchQueue
{
    ValueTask EnqueueAsync(
        Func<CancellationToken, Task> workItem,
        CancellationToken ct,
        ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal);
}

public enum ExecutionDispatchPriority
{
    Normal = 0,
    Interactive = 1,
}

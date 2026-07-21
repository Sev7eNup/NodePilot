namespace NodePilot.Api.ExecutionDispatch;

/// <summary>
/// Queue capacity and worker-pool size for background workflow-start dispatch.
/// Workers <c>await</c> <c>engine.ExecuteAsync</c> for the full workflow lifetime,
/// so <see cref="WorkerCount"/> is the real concurrency limit and <see cref="Capacity"/>
/// is the spike buffer — incoming starts beyond the worker count wait in the queue
/// until a worker frees up. The engine's <c>Engine:MaxConcurrentExecutions</c> caps
/// sit above <see cref="WorkerCount"/> as a sanity upper-bound for pathological cases
/// (trigger loops, sub-workflow cascades) and should not trip during normal operation.
/// </summary>
public sealed class ExecutionDispatchOptions
{
    public const string SectionName = "ExecutionDispatch";

    public int Capacity { get; set; } = 512;

    public int WorkerCount { get; set; } = 50;
}

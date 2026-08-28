namespace NodePilot.Api.ExecutionDispatch;

/// <summary>
/// Worker-pool size for durable background workflow-start dispatch.
/// Workers <c>await</c> <c>engine.ExecuteAsync</c> for the full workflow lifetime,
/// so <see cref="WorkerCount"/> is the dispatch concurrency limit. Incoming starts beyond
/// that limit remain durably persisted in the database outbox until a worker is available.
/// The engine's <c>Engine:MaxConcurrentExecutions</c> caps
/// sit above <see cref="WorkerCount"/> as a sanity upper-bound for pathological cases
/// (trigger loops, sub-workflow cascades) and should not trip during normal operation.
/// </summary>
public sealed class ExecutionDispatchOptions
{
    public const string SectionName = "ExecutionDispatch";

    public int WorkerCount { get; set; } = 50;
}

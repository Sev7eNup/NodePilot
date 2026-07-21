using NodePilot.Core.Interfaces;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Canned-data fake for the execution-log chat tools: returns the pre-configured runs/steps and
/// remembers the last arguments it was called with, so tests can assert on clamping + scoping.
/// </summary>
public sealed class FakeExecutionLogReader : IExecutionLogReader
{
    public List<ExecutionLogSummary> Executions { get; } = [];
    public Dictionary<Guid, ExecutionStepLogs> StepsByExecution { get; } = [];

    public Guid? LastWorkflowId { get; private set; }
    public int? LastTake { get; private set; }
    public Guid? LastExecutionId { get; private set; }

    public Task<IReadOnlyList<ExecutionLogSummary>> GetRecentExecutionsAsync(Guid workflowId, int take, CancellationToken ct)
    {
        LastWorkflowId = workflowId;
        LastTake = take;
        return Task.FromResult<IReadOnlyList<ExecutionLogSummary>>(Executions.Take(take).ToList());
    }

    public Task<ExecutionStepLogs?> GetExecutionStepsAsync(Guid workflowId, Guid executionId, CancellationToken ct)
    {
        LastWorkflowId = workflowId;
        LastExecutionId = executionId;
        return Task.FromResult(StepsByExecution.TryGetValue(executionId, out var logs) ? logs : null);
    }

    public static ExecutionLogSummary Summary(Guid id, string status = "Succeeded", string? errorMessage = null,
        int stepsTotal = 1, IReadOnlyList<FailedStepInfo>? failedSteps = null) =>
        new(id, status, new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 12, 0, 5, DateTimeKind.Utc), "manual", errorMessage,
            stepsTotal, failedSteps ?? []);

    public static StepExecutionLog Step(string stepId, string status = "Succeeded", string? output = null,
        string? errorOutput = null, string? stepName = null) =>
        new(stepId, stepName, "runScript", null, status,
            new DateTime(2026, 7, 1, 12, 0, 1, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 12, 0, 2, DateTimeKind.Utc), 1, output, errorOutput);
}

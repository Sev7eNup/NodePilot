using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>Database-side state transitions for the execution ownership lifecycle.</summary>
public static class ExecutionStateLifecycle
{
    public static Task<int> TryClaimPendingAsync(
        IQueryable<WorkflowExecution> candidates,
        CancellationToken ct)
        => candidates
            .Where(execution => execution.Status == ExecutionStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(execution => execution.Status, ExecutionStatus.Running), ct);

    public static Task<int> TrySetTerminalAsync(
        IQueryable<WorkflowExecution> candidates,
        ExecutionStatus status,
        DateTime completedAt,
        string? errorMessage,
        string? cancelledBy,
        CancellationToken ct)
    {
        if (status is not (ExecutionStatus.Succeeded
            or ExecutionStatus.Failed
            or ExecutionStatus.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(status), status, "A terminal status is required.");

        return candidates.ExecuteUpdateAsync(setters => setters
            .SetProperty(execution => execution.Status, status)
            .SetProperty(execution => execution.CompletedAt, completedAt)
            .SetProperty(execution => execution.ErrorMessage, errorMessage)
            .SetProperty(execution => execution.CancelledBy, cancelledBy), ct);
    }

    /// <summary>
    /// Marks every step still Running or Paused under the given execution as Cancelled. Called
    /// after an execution became terminal: a step whose terminal write never landed would
    /// otherwise stay Running forever, since nothing revisits steps of a finished execution.
    /// </summary>
    public static Task<int> CancelOrphanedStepsAsync(
        IQueryable<StepExecution> steps,
        Guid executionId,
        DateTime completedAt,
        string message,
        CancellationToken ct)
        => CancelOrphanedSteps(
            steps.Where(step => step.WorkflowExecutionId == executionId), completedAt, message, ct);

    /// <summary>Batch form of <see cref="CancelOrphanedStepsAsync(IQueryable{StepExecution}, Guid, DateTime, string, CancellationToken)"/>.</summary>
    public static Task<int> CancelOrphanedStepsAsync(
        IQueryable<StepExecution> steps,
        IReadOnlyCollection<Guid> executionIds,
        DateTime completedAt,
        string message,
        CancellationToken ct)
        => CancelOrphanedSteps(
            steps.Where(step => executionIds.Contains(step.WorkflowExecutionId)), completedAt, message, ct);

    private static Task<int> CancelOrphanedSteps(
        IQueryable<StepExecution> steps, DateTime completedAt, string message, CancellationToken ct)
        => steps
            .Where(step => step.Status == ExecutionStatus.Running || step.Status == ExecutionStatus.Paused)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(step => step.Status, ExecutionStatus.Cancelled)
                .SetProperty(step => step.CompletedAt, completedAt)
                .SetProperty(step => step.ErrorOutput, step => step.ErrorOutput ?? message), ct);
}

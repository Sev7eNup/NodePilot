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
}

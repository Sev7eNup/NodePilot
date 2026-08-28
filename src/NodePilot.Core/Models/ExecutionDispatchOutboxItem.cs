using NodePilot.Core.Interfaces;

namespace NodePilot.Core.Models;

/// <summary>
/// Durable handoff for an accepted <see cref="WorkflowExecution"/>. The row is created in the
/// same transaction as the Pending execution and removed after the engine claims or terminalizes
/// that execution.
/// </summary>
public sealed class ExecutionDispatchOutboxItem
{
    public Guid ExecutionId { get; set; }
    public Guid WorkflowId { get; set; }
    public string TriggeredBy { get; set; } = string.Empty;
    public byte[]? ProtectedParameters { get; set; }
    public int? TimeoutSeconds { get; set; }
    public bool DebugEnabled { get; set; }
    public Guid? StartedByUserId { get; set; }
    public Guid? ParentExecutionId { get; set; }
    public int CallDepth { get; set; }
    public bool RequireWorkflowEnabled { get; set; }
    public string MissingWorkflowMessage { get; set; } = string.Empty;
    public string PreOwnershipFailurePrefix { get; set; } = string.Empty;
    public ExecutionDispatchPriority Priority { get; set; }
    public bool RequireMaintenanceWindowCheck { get; set; }
    public bool BypassMaintenanceWindow { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime AvailableAt { get; set; } = DateTime.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public int AttemptCount { get; set; }

    public WorkflowExecution Execution { get; set; } = null!;
}

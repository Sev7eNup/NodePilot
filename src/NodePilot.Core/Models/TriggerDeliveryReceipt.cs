namespace NodePilot.Core.Models;

/// <summary>
/// Durable, deduplicated acknowledgement of one externally observed trigger signal. The row is
/// committed in the same transaction as the execution dispatch outbox (or a deliberate
/// suppression), so a source may safely retry until this acknowledgement exists.
/// </summary>
public sealed class TriggerDeliveryReceipt
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public string TriggerNodeId { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public Guid? ExecutionId { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public Workflow Workflow { get; set; } = null!;
}

/// <summary>
/// Last source cursor whose delivery was durably acknowledged. Sources use it to reconcile
/// signals after restart, failover, watcher overflow, or a temporary database outage.
/// </summary>
public sealed class TriggerDeliveryCheckpoint
{
    public Guid WorkflowId { get; set; }
    public string TriggerNodeId { get; set; } = string.Empty;
    public string TriggerType { get; set; } = string.Empty;
    public string ConfigurationHash { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Workflow Workflow { get; set; } = null!;
}

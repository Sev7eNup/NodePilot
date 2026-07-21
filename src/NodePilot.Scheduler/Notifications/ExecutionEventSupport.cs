using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.Notifications;

/// <summary>
/// Narrow execution projection shared by the execution-shaped collectors
/// (terminal scan, long-running, queued-long) and their recovery branches.
/// </summary>
internal sealed record ExecRow(
    Guid Id, Guid WorkflowId, ExecutionStatus Status, DateTime StartedAt, DateTime? CompletedAt,
    string? TriggeredBy, string? ErrorMessage, Guid? ParentExecutionId, string WorkflowName, Guid FolderId, string FolderPath,
    string? CancelledBy,
    // Resolved machine name of the last-failing step (terminal scans only; the
    // long-running/queued collectors pass null — a failed-step join is meaningless there).
    string? TargetMachine);

/// <summary>
/// Shared helpers for building execution-shaped <see cref="NotificationContext"/>s.
/// </summary>
internal static class ExecutionEventSupport
{
    /// <summary>Per-pass scan cap shared by the execution-shaped collectors.</summary>
    public const int ScanBatchSize = 200;

    private static readonly string[] CredentialFailureNeedles =
    [
        "credential",
        "credentials",
        "authentication",
        "authorization",
        "unauthorized",
        "access denied",
        "logon failure",
        "login failed",
        "invalid password",
        "password",
        "kerberos",
        "ntlm",
        "401",
        "403",
    ];

    public static NotificationContext BuildContext(ExecRow row)
    {
        var eventType = row.Status switch
        {
            ExecutionStatus.Succeeded => NotificationEventType.ExecutionSucceeded,
            ExecutionStatus.Cancelled => NotificationEventType.ExecutionCancelled,
            _ => NotificationEventType.ExecutionFailed,
        };
        var severity = eventType == NotificationEventType.ExecutionFailed ? NotificationSeverity.Warning : NotificationSeverity.Info;
        var duration = row.CompletedAt.HasValue ? (long?)(row.CompletedAt.Value - row.StartedAt).TotalMilliseconds : null;
        return new NotificationContext(
            EventType: eventType,
            Severity: severity,
            EventKey: $"exec:{row.Id:N}:{eventType}",
            WorkflowId: row.WorkflowId,
            WorkflowName: row.WorkflowName,
            FolderId: row.FolderId,
            FolderPath: row.FolderPath,
            ExecutionId: row.Id,
            Status: row.Status.ToString(),
            ErrorMessage: row.ErrorMessage,
            DurationMs: duration,
            OccurredAt: row.CompletedAt ?? row.StartedAt,
            TriggeredBy: row.TriggeredBy,
            CallDepth: row.ParentExecutionId.HasValue ? 1 : 0,
            IsSubWorkflow: row.ParentExecutionId.HasValue,
            // Last-failing step's resolved machine name; null for Succeeded/Cancelled and
            // for failures without a remote target (engine-local steps).
            TargetMachine: row.TargetMachine,
            SourceKey: null,
            Title: null,
            Summary: null,
            DeepLinkPath: $"/executions/{row.Id}",
            // Only meaningful for Cancelled; null for Succeeded/Failed. Lets a rule filter cancelledBy == "user".
            CancelledBy: eventType == NotificationEventType.ExecutionCancelled ? row.CancelledBy : null);
    }

    public static NotificationContext BuildCredentialFailureContext(ExecRow row)
    {
        var duration = row.CompletedAt.HasValue ? (long?)(row.CompletedAt.Value - row.StartedAt).TotalMilliseconds : null;
        return new NotificationContext(
            EventType: NotificationEventType.CredentialFailure,
            Severity: NotificationSeverity.Warning,
            EventKey: $"exec:{row.Id:N}:{NotificationEventType.CredentialFailure}",
            WorkflowId: row.WorkflowId,
            WorkflowName: row.WorkflowName,
            FolderId: row.FolderId,
            FolderPath: row.FolderPath,
            ExecutionId: row.Id,
            Status: row.Status.ToString(),
            ErrorMessage: row.ErrorMessage,
            DurationMs: duration,
            OccurredAt: row.CompletedAt ?? row.StartedAt,
            TriggeredBy: row.TriggeredBy,
            CallDepth: row.ParentExecutionId.HasValue ? 1 : 0,
            IsSubWorkflow: row.ParentExecutionId.HasValue,
            TargetMachine: row.TargetMachine,
            SourceKey: null,
            Title: $"Credential failure: {row.WorkflowName}",
            Summary: row.ErrorMessage,
            DeepLinkPath: $"/executions/{row.Id}");
    }

    public static bool LooksLikeCredentialFailure(ExecRow row)
    {
        if (row.Status != ExecutionStatus.Failed || string.IsNullOrWhiteSpace(row.ErrorMessage))
            return false;

        return CredentialFailureNeedles.Any(needle =>
            row.ErrorMessage.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// StepExecution.TargetMachine stores the RESOLVED raw target value — for designer-built
    /// workflows that is the machine GUID, not its name, while gauge signals
    /// (MachineUnreachable) carry names. Map GUID-shaped values to machine names in one
    /// batched lookup so the <c>targetMachine</c> event field has uniform name semantics;
    /// non-GUID values (dynamic hostname targets) and unknown ids pass through unchanged.
    /// </summary>
    public static async Task<List<ExecRow>> ResolveTargetMachineNamesAsync(
        NodePilotDbContext db, List<ExecRow> batch, CancellationToken ct)
    {
        var ids = batch
            .Select(r => Guid.TryParse(r.TargetMachine, out var g) ? g : (Guid?)null)
            .Where(g => g is not null)
            .Select(g => g!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0) return batch;

        var names = await db.ManagedMachines.AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, ct);

        return batch
            .Select(r => Guid.TryParse(r.TargetMachine, out var g) && names.TryGetValue(g, out var name)
                ? r with { TargetMachine = name }
                : r)
            .ToList();
    }
}

using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Trigger-registration health: one observation per trigger the orchestrator currently cannot keep
/// registered, exposing <c>unhealthySeconds</c>, <c>consecutiveFailures</c> and <c>triggerType</c>.
///
/// Fills the blind spot that made a dead fileWatcherTrigger an invisible incident. Nothing else
/// sees it: <c>schedule-missed</c> only walks cron triggers and needs an expected fire time, which
/// an event-driven trigger has none of; <c>service-stale</c> only measures heartbeat age, and the
/// orchestrator keeps beating because its sync pass succeeds even while every trigger it manages
/// is broken. A policy like <c>unhealthySeconds &gt; 60</c> pages once retries stop being
/// transient.
///
/// The only source reading process memory rather than the database — see
/// <see cref="TriggerHealthRegistry"/> for why, including the HA caveat.
/// </summary>
public sealed class TriggerUnhealthySource : ISystemAlertSource
{
    private readonly TriggerHealthRegistry _registry;

    public TriggerUnhealthySource(TriggerHealthRegistry registry) => _registry = registry;

    public string SourceId => "trigger-unhealthy";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Health, SystemAlertScopeCapability.WorkflowScoped, NotificationSeverity.Warning,
        Fields:
        [
            SystemAlertField.Of("unhealthySeconds", SystemAlertFieldType.Number, unit: "seconds"),
            SystemAlertField.Of("consecutiveFailures", SystemAlertFieldType.Number),
            // String rather than Enum: the handled trigger types are already listed twice in
            // TriggerOrchestrator (IsHandledHere + CreateSource), and a third copy here would be a
            // third thing to forget when a trigger type is added. Text operators filter it fine.
            SystemAlertField.Of("triggerType", SystemAlertFieldType.String),
        ],
        Parameters: [],
        Presets:
        [
            // A minute of failed retries is past the point where a share restart or a brief network
            // blip explains it — below that the backoff would page on ordinary maintenance.
            new SystemAlertPreset("registration-failing", NotificationSeverity.Warning, 0,
                SystemAlertConditions.Compare("unhealthySeconds", ">", "60")),
        ]);

    public Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct)
        => Task.FromResult(_registry.Snapshot().Count > 0);

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var entries = _registry.Snapshot();
        if (entries.Count == 0) return [];

        // Names and folders come from the database so the observation can be workflow-scoped and
        // deep-linked; the registry deliberately stores none of that, to stay a pure health record.
        var workflowIds = entries.Select(e => e.WorkflowId).Distinct().ToList();
        var workflows = await db.Workflows.AsNoTracking()
            .Where(w => workflowIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Name, w.FolderId })
            .ToDictionaryAsync(w => w.Id, ct);

        var now = DateTime.UtcNow;
        return entries.Select(e =>
        {
            var unhealthySeconds = Math.Max(0, (long)(now - e.SinceUtc).TotalSeconds);
            workflows.TryGetValue(e.WorkflowId, out var wf);
            var name = wf?.Name ?? e.WorkflowId.ToString("D");

            return new SystemAlertObservation(SourceId, $"{e.WorkflowId:N}:{e.NodeId}", NotificationSeverity.Warning,
                $"Trigger not registered: {name} ({e.TriggerType})",
                $"The {e.TriggerType} on '{name}' has been unable to register for {unhealthySeconds}s " +
                $"after {e.ConsecutiveFailures} attempt(s): {e.Reason}. It will not fire until this clears.",
                $"/workflows/{e.WorkflowId:D}",
                new Dictionary<string, object?>
                {
                    ["unhealthySeconds"] = unhealthySeconds,
                    ["consecutiveFailures"] = (long)e.ConsecutiveFailures,
                    ["triggerType"] = e.TriggerType,
                },
                WorkflowId: e.WorkflowId, WorkflowName: wf?.Name, FolderId: wf?.FolderId,
                SignalValue: unhealthySeconds);
        }).ToList();
    }
}

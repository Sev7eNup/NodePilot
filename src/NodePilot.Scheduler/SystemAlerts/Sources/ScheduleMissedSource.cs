using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler.Gauge;
using Quartz;

namespace NodePilot.Scheduler.SystemAlerts.Sources;

/// <summary>
/// Workflow-scoped schedule-health source (replaces an older, hard-coded "ScheduleMissed" metric with one
/// where each policy can set its own grace period). For every enabled scheduleTrigger node it reconstructs
/// the last expected Quartz (the cron-scheduling library NodePilot uses) fire within a lookback window and checks
/// whether a schedule-triggered execution was actually created; exposes <c>missed</c> (bool) and
/// <c>minutesLate</c>. Keyed per (workflow, node). The <c>graceMinutes</c> parameter debounces the "just fired,
/// execution not visible yet" race.
/// </summary>
public sealed class ScheduleMissedSource : ISystemAlertSource
{
    private const int DefaultGraceMinutes = 5;
    private const int LookbackHours = 48;
    private const int ExecutionSlackSeconds = 30;
    private const int MaxCronIterations = 10_000;

    public string SourceId => "schedule-missed";

    public SystemAlertSourceDescriptor Describe() => new(
        SourceId, SystemAlertCategory.Schedule, SystemAlertScopeCapability.WorkflowScoped, NotificationSeverity.Warning,
        Fields:
        [
            SystemAlertField.Of("missed", SystemAlertFieldType.Boolean),
            SystemAlertField.Of("minutesLate", SystemAlertFieldType.Number, unit: "minutes"),
        ],
        Parameters: [new SystemAlertParameter("graceMinutes", SystemAlertFieldType.Duration, Default: DefaultGraceMinutes, Unit: "minutes", Min: 1)],
        Presets: [new SystemAlertPreset("missed", NotificationSeverity.Warning, 0, SystemAlertConditions.Unary("missed", "isTrue"))]);

    public async Task<bool> IsAvailableAsync(NodePilotDbContext db, CancellationToken ct)
        => (await ScheduledWorkflowSignalHelpers.LoadEnabledScheduledWorkflowsAsync(db, ct)).Count > 0;

    public async Task<IReadOnlyList<SystemAlertObservation>> ObserveAsync(NodePilotDbContext db, SystemAlertQuery query, CancellationToken ct)
    {
        var grace = TimeSpan.FromMinutes(Math.Max(1, query.GetInt("graceMinutes", DefaultGraceMinutes)));
        var lookback = TimeSpan.FromHours(LookbackHours);
        var slack = TimeSpan.FromSeconds(ExecutionSlackSeconds);
        var now = DateTimeOffset.UtcNow;

        var workflows = await ScheduledWorkflowSignalHelpers.LoadEnabledScheduledWorkflowsAsync(db, ct);
        var observations = new List<SystemAlertObservation>();

        foreach (var wf in workflows)
        {
            foreach (var trigger in wf.ScheduleTriggers)
            {
                var cronRaw = ScheduledWorkflowSignalHelpers.CronExpression(trigger);
                if (cronRaw is null) continue;
                CronExpression cron;
                try { cron = new CronExpression(cronRaw) { TimeZone = TimeZoneInfo.Local }; }
                catch (FormatException) { continue; }

                var expected = PreviousFireBefore(cron, now - lookback, now);
                bool missed;
                long minutesLate = 0;
                if (expected is null)
                {
                    missed = false;
                }
                else
                {
                    var expectedUtc = expected.Value.UtcDateTime;
                    var hasExecution = await db.WorkflowExecutions.AsNoTracking()
                        .AnyAsync(e => e.WorkflowId == wf.Id && e.TriggeredBy == "scheduleTrigger"
                            && e.StartedAt >= expectedUtc - slack && e.StartedAt <= now.UtcDateTime, ct);
                    missed = expected.Value.Add(grace) < now && !hasExecution;
                    minutesLate = missed ? Math.Max(0, (long)(now - expected.Value).TotalMinutes) : 0;
                }

                observations.Add(new SystemAlertObservation(SourceId, $"{wf.Id:N}:{trigger.NodeId}", NotificationSeverity.Warning,
                    $"Schedule {(missed ? "missed" : "ok")}: {wf.Name}",
                    missed ? $"{wf.Name} schedule node {trigger.NodeId} did not run on time ({minutesLate} min late)."
                           : $"{wf.Name} schedule node {trigger.NodeId} ran on schedule.",
                    $"/workflows/{wf.Id:D}",
                    new Dictionary<string, object?> { ["missed"] = missed, ["minutesLate"] = minutesLate },
                    WorkflowId: wf.Id, WorkflowName: wf.Name, FolderId: wf.FolderId, FolderPath: wf.FolderPath, SignalValue: minutesLate));
            }
        }
        return observations;
    }

    private static DateTimeOffset? PreviousFireBefore(CronExpression cron, DateTimeOffset start, DateTimeOffset end)
    {
        DateTimeOffset? previous = null;
        var cursor = start;
        for (var i = 0; i < MaxCronIterations; i++)
        {
            var next = cron.GetNextValidTimeAfter(cursor);
            if (next is null || next.Value > end) break;
            previous = next.Value;
            cursor = next.Value;
        }
        return previous;
    }
}

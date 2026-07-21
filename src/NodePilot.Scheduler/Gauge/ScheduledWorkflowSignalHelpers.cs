using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;

namespace NodePilot.Scheduler.Gauge;

internal sealed record ScheduledWorkflowSignalCandidate(
    Guid Id,
    string Name,
    Guid FolderId,
    string FolderPath,
    IReadOnlyList<WorkflowTriggerDescriptor> ScheduleTriggers);

internal static class ScheduledWorkflowSignalHelpers
{
    public static async Task<IReadOnlyList<ScheduledWorkflowSignalCandidate>> LoadEnabledScheduledWorkflowsAsync(
        NodePilotDbContext db,
        CancellationToken ct)
    {
        var rows = await db.Workflows.AsNoTracking()
            .Where(w => w.IsEnabled)
            .Select(w => new
            {
                w.Id,
                w.Name,
                w.DefinitionJson,
                w.FolderId,
                FolderPath = w.Folder!.Path,
            })
            .ToListAsync(ct);

        var result = new List<ScheduledWorkflowSignalCandidate>();
        foreach (var row in rows)
        {
            if (!WorkflowDefinitionDocument.TryParse(row.DefinitionJson, out var definition) || definition is null)
                continue;

            var schedules = definition.TriggerDescriptors
                .Where(t => string.Equals(t.ActivityType, "scheduleTrigger", StringComparison.Ordinal))
                .ToList();
            if (schedules.Count == 0)
                continue;

            result.Add(new ScheduledWorkflowSignalCandidate(row.Id, row.Name, row.FolderId, row.FolderPath, schedules));
        }

        return result;
    }

    public static string? CronExpression(WorkflowTriggerDescriptor descriptor)
    {
        if (descriptor.Config.ValueKind != JsonValueKind.Object)
            return null;
        if (!descriptor.Config.TryGetProperty("cronExpression", out var cron) || cron.ValueKind != JsonValueKind.String)
            return null;
        var value = cron.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

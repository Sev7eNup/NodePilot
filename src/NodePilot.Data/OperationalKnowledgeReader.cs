using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using Quartz;

namespace NodePilot.Data;

/// <summary>
/// Default <see cref="IOperationalKnowledgeReader"/> for the global "AI Chat" knowledge assistant.
/// Lives next to <see cref="ExecutionLogReader"/> — same decoupling: it takes a pre-resolved
/// <see cref="AccessibleFolderSet"/> (never a <c>ClaimsPrincipal</c>) and never runs an unfiltered
/// query. Workflow definitions are redacted twice before leaving: key-based
/// (<see cref="WorkflowSecretRedactor"/>) plus a pattern-based pass over the serialized text
/// (<see cref="IAuditDetailsRedactor"/>) that also catches secrets hard-coded inside runScript
/// bodies. Free-text execution fields are pattern-redacted too — deliberately stricter than the
/// role gradient in <c>ExecutionsController</c>, because results go to an external LLM.
/// </summary>
public sealed class OperationalKnowledgeReader(NodePilotDbContext db, IAuditDetailsRedactor redactor)
    : IOperationalKnowledgeReader
{
    private const int MaxTake = 50;

    public async Task<IReadOnlyList<WorkflowKnowledgeSummary>> ListWorkflowsAsync(
        AccessibleFolderSet accessible, string? nameFilter, int take, CancellationToken ct)
    {
        if (IsNoAccess(accessible)) return [];
        take = Math.Clamp(take, 1, MaxTake);

        var q = Scoped(db.Workflows.AsNoTracking(), accessible);
        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            var f = nameFilter.Trim().ToLower();
            q = q.Where(w => w.Name.ToLower().Contains(f));
        }

        var rows = await q
            .OrderBy(w => w.Name)
            .Take(take)
            .Select(w => new { w.Id, w.Name, w.Description, w.IsEnabled, w.ActivityCount, w.TriggerTypesJson, w.UpdatedAt })
            .ToListAsync(ct);

        return rows.Select(w => new WorkflowKnowledgeSummary(
            w.Id, w.Name, w.Description, w.IsEnabled, w.ActivityCount,
            ParseTriggerTypes(w.TriggerTypesJson), w.UpdatedAt)).ToList();
    }

    public async Task<WorkflowKnowledgeDetail?> GetWorkflowDefinitionAsync(
        AccessibleFolderSet accessible, string idOrName, CancellationToken ct)
    {
        var wf = await ResolveWorkflowAsync(accessible, idOrName, ct);
        if (wf is null) return null;

        return new WorkflowKnowledgeDetail(
            wf.Id, wf.Name, wf.Description, wf.IsEnabled, RedactDefinition(wf.DefinitionJson));
    }

    public async Task<IReadOnlyList<ExecutionKnowledgeSummary>> ListRecentExecutionsAsync(
        AccessibleFolderSet accessible, string? status, int take, CancellationToken ct)
    {
        if (IsNoAccess(accessible)) return [];
        take = Math.Clamp(take, 1, MaxTake);

        IQueryable<WorkflowExecution> q = db.WorkflowExecutions.AsNoTracking();
        if (!accessible.IsUnrestricted)
            q = q.Where(e => accessible.FolderIds.Contains(e.Workflow.FolderId));
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ExecutionStatus>(status, ignoreCase: true, out var parsed))
            q = q.Where(e => e.Status == parsed);

        return await ProjectExecutionsAsync(q.OrderByDescending(e => e.StartedAt).Take(take), ct);
    }

    public async Task<IReadOnlyList<ExecutionKnowledgeSummary>> GetWorkflowExecutionsAsync(
        AccessibleFolderSet accessible, string idOrName, int take, CancellationToken ct)
    {
        var wf = await ResolveWorkflowAsync(accessible, idOrName, ct);
        if (wf is null) return [];
        take = Math.Clamp(take, 1, MaxTake);

        var q = db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.WorkflowId == wf.Id)
            .OrderByDescending(e => e.StartedAt)
            .Take(take);
        return await ProjectExecutionsAsync(q, ct);
    }

    public async Task<IReadOnlyList<MachineKnowledgeSummary>> ListMachinesAsync(int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, MaxTake);
        var rows = await db.ManagedMachines.AsNoTracking()
            .OrderBy(m => m.Name)
            .Take(take)
            .Select(m => new MachineKnowledgeSummary(
                m.Id, m.Name, m.Hostname, m.WinRmPort, m.UseSsl, m.IsReachable, m.LastConnectivityCheck, m.Tags))
            .ToListAsync(ct);
        return rows;
    }

    public async Task<IReadOnlyList<ScheduledFireForecast>> ListScheduledFiresAsync(
        AccessibleFolderSet accessible, string? idOrName, int perWorkflow, int maxWorkflows, CancellationToken ct)
    {
        if (IsNoAccess(accessible)) return [];
        perWorkflow = Math.Clamp(perWorkflow, 1, 5);
        maxWorkflows = Math.Clamp(maxWorkflows, 1, MaxTake);

        var q = Scoped(db.Workflows.AsNoTracking(), accessible).Where(w => w.IsEnabled);
        if (!string.IsNullOrWhiteSpace(idOrName))
        {
            var wf = await ResolveWorkflowAsync(accessible, idOrName, ct);
            if (wf is null || !wf.IsEnabled) return [];
            q = q.Where(w => w.Id == wf.Id);
        }

        var rows = await q
            .OrderBy(w => w.Name)
            .Select(w => new { w.Id, w.Name, w.DefinitionJson })
            .ToListAsync(ct);

        // Compute in the server's local time zone — the exact semantics the real scheduler fires on
        // (ScheduleTriggerSource uses TimeZoneInfo.Local) — then hand back UTC instants so the LLM
        // gets an unambiguous absolute time it can render in the caller's zone.
        var now = DateTimeOffset.UtcNow;
        var forecasts = new List<ScheduledFireForecast>();
        foreach (var w in rows)
        {
            if (forecasts.Count >= maxWorkflows) break;
            if (!WorkflowDefinitionDocument.TryParse(w.DefinitionJson, out var def) || def is null) continue;

            foreach (var trg in def.TriggerDescriptors)
            {
                if (!string.Equals(trg.ActivityType, "scheduleTrigger", StringComparison.Ordinal)) continue;
                if (trg.Config.ValueKind != JsonValueKind.Object
                    || !trg.Config.TryGetProperty("cronExpression", out var cronEl)
                    || cronEl.ValueKind != JsonValueKind.String) continue;
                var cron = cronEl.GetString();
                if (string.IsNullOrWhiteSpace(cron)) continue;

                CronExpression parsed;
                try { parsed = new CronExpression(cron) { TimeZone = TimeZoneInfo.Local }; }
                catch (FormatException) { continue; }

                var fires = new List<DateTime>(perWorkflow);
                DateTimeOffset? cursor = now;
                for (var i = 0; i < perWorkflow; i++)
                {
                    cursor = parsed.GetNextValidTimeAfter(cursor.Value);
                    if (cursor is null) break;
                    fires.Add(cursor.Value.UtcDateTime);
                }
                if (fires.Count == 0) continue;

                forecasts.Add(new ScheduledFireForecast(w.Id, w.Name, cron, SafeSummary(parsed), fires));
                if (forecasts.Count >= maxWorkflows) break;
            }
        }

        return forecasts;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string? SafeSummary(CronExpression cron)
    {
        try { return cron.GetExpressionSummary()?.Trim(); }
        catch (Exception) { return null; }
    }

    private static bool IsNoAccess(AccessibleFolderSet a) => !a.IsUnrestricted && a.FolderIds.Count == 0;

    private static IQueryable<Workflow> Scoped(IQueryable<Workflow> q, AccessibleFolderSet a) =>
        a.IsUnrestricted ? q : q.Where(w => a.FolderIds.Contains(w.FolderId));

    private async Task<Workflow?> ResolveWorkflowAsync(AccessibleFolderSet accessible, string idOrName, CancellationToken ct)
    {
        if (IsNoAccess(accessible) || string.IsNullOrWhiteSpace(idOrName)) return null;
        idOrName = idOrName.Trim();
        var q = Scoped(db.Workflows.AsNoTracking(), accessible);

        if (Guid.TryParse(idOrName, out var id))
            return await q.FirstOrDefaultAsync(w => w.Id == id, ct);

        // exact-case wins; a unique case-insensitive match is the fallback; ambiguous → null.
        var exact = await q.Where(w => w.Name == idOrName).Take(2).ToListAsync(ct);
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;

        var lowered = idOrName.ToLower();
        var ci = await q.Where(w => w.Name.ToLower() == lowered).Take(2).ToListAsync(ct);
        return ci.Count == 1 ? ci[0] : null;
    }

    private async Task<IReadOnlyList<ExecutionKnowledgeSummary>> ProjectExecutionsAsync(
        IQueryable<WorkflowExecution> q, CancellationToken ct)
    {
        var rows = await q
            .Select(e => new
            {
                e.Id,
                e.WorkflowId,
                WorkflowName = e.Workflow.Name,
                e.Status,
                e.StartedAt,
                e.CompletedAt,
                e.TriggeredBy,
                e.ErrorMessage,
            })
            .ToListAsync(ct);

        return rows.Select(e => new ExecutionKnowledgeSummary(
            e.Id, e.WorkflowId, e.WorkflowName, e.Status.ToString(),
            e.StartedAt, e.CompletedAt, e.TriggeredBy, redactor.Redact(e.ErrorMessage))).ToList();
    }

    private string RedactDefinition(string definitionJson)
    {
        string keyRedacted;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(definitionJson) ? "{}" : definitionJson);
            keyRedacted = WorkflowSecretRedactor.Redact(doc.RootElement).ToJsonString();
        }
        catch (JsonException)
        {
            keyRedacted = "{}";
        }
        // Pattern-based pass: catches secrets hard-coded in free-text (e.g. a runScript body) that
        // the key-based redactor can't see.
        return redactor.Redact(keyRedacted) ?? keyRedacted;
    }

    private static IReadOnlyList<string> ParseTriggerTypes(string? triggerTypesJson)
    {
        if (string.IsNullOrWhiteSpace(triggerTypesJson)) return [];
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(triggerTypesJson);
            return arr is null ? [] : arr.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

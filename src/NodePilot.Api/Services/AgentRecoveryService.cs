using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Agents;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Engine.Agents;

namespace NodePilot.Api.Services;

/// <summary>Reconciles journals after execution recovery and removes abandoned local workspaces.</summary>
public sealed class AgentRecoveryService(IServiceScopeFactory scopes, IDatabaseAvailability availability,
    ILogger<AgentRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await availability.WaitUntilServableAsync(stoppingToken);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                await ReconcileAsync(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Agent recovery sweep failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static async Task ReconcileAsync(NodePilotDbContext db, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var orphanIds = await db.AgentRuns.Where(x => x.Status == "Running"
            && (x.WorkflowExecution.Status == ExecutionStatus.Succeeded || x.WorkflowExecution.Status == ExecutionStatus.Failed
                || x.WorkflowExecution.Status == ExecutionStatus.Cancelled))
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var id in orphanIds)
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                const string reason = "Owning execution ended; the agent cannot resume.";
                var changed = await db.AgentRuns.Where(x => x.Id == id && x.Status == "Running")
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled")
                        .SetProperty(x => x.CompletedAt, now).SetProperty(x => x.Error, reason), ct);
                if (changed == 1)
                {
                    var sequence = (await db.AgentRunEvents.Where(x => x.AgentRunId == id).MaxAsync(x => (long?)x.Sequence, ct) ?? 0) + 1;
                    var entry = new AgentRunEvent { AgentRunId = id, Sequence = sequence,
                        Timestamp = now, Kind = "run_cancelled", Content = reason };
                    db.AgentRunEvents.Add(entry);
                    await db.SaveChangesAsync(ct);
                    db.Entry(entry).State = EntityState.Detached;
                }
                await transaction.CommitAsync(ct);
            });
        }
        var root = Path.GetFullPath(AgentArtifactStore.BaseDirectory);
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(full), "N", out var id)
                || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) continue;
            var active = await db.AgentRuns.AsNoTracking().AnyAsync(x => x.Id == id && x.Status == "Running", ct);
            if (!active) Directory.Delete(full, recursive: true);
        }
    }
}

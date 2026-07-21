using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Models;

namespace NodePilot.Data;

/// <summary>
/// Applies each provider's own EF migration set at startup. SQL Server and PostgreSQL behave
/// identically: <c>db.Database.Migrate()</c> brings the DB up to the current schema (creates
/// all tables on an empty DB, otherwise applies only the pending ones).
/// </summary>
public static class MigrationBootstrapper
{
    public static void Bootstrap(NodePilotDbContext db, ILogger logger)
    {
        db.Database.Migrate();
        var applied = db.Database.GetAppliedMigrations().ToList();
        var providerName = db.Database.ProviderName ?? "unknown";
        // SupportLog scope: the migration summary is one of the few system boot log lines a
        // support engineer should see (typical question: "is the DB on the right schema
        // version?"). The support-log sub-sink filter reads this property to decide what to route there.
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["SupportLog"] = true,
            ["support.event_type"] = "MIGRATION_APPLIED",
            ["db_provider"] = providerName,
            ["migration_count"] = applied.Count,
            ["support.message"] = $"{applied.Count} migration(s) applied (provider: {providerName})",
        }))
        {
            logger.LogInformation("Database bootstrap: {Provider}, {Count} migration(s) applied total.",
                providerName, applied.Count);
        }

        BackfillWorkflowComputedColumns(db, logger);
        SeedClusterLeaderRow(db, logger);
    }

    /// <summary>
    /// The HA leader-lease table holds exactly one row keyed by Resource="primary". Seeded
    /// once with an empty owner and an already-expired lease so the first node to start can
    /// immediately acquire it. Idempotent: subsequent boots skip the insert.
    /// <para>
    /// Race-safe across two simultaneously starting nodes: the SELECT/INSERT pair is not
    /// atomic, so two cold-boot nodes can both pass the existence check and both try to
    /// INSERT — the loser sees a unique-constraint violation on the <c>Resource</c> PK
    /// and we treat that as "the other node won, the row exists now, we're done".
    /// </para>
    /// </summary>
    private static void SeedClusterLeaderRow(NodePilotDbContext db, ILogger logger)
    {
        if (db.ClusterLeaders.Any(x => x.Resource == "primary")) return;

        db.ClusterLeaders.Add(new ClusterLeader
        {
            Resource = "primary",
            OwnerNodeId = string.Empty,
            AcquiredAt = DateTime.MinValue.ToUniversalTime(),
            ExpiresAt = DateTime.MinValue.ToUniversalTime(),
            LastRenewedAt = DateTime.MinValue.ToUniversalTime(),
            LeaseEpoch = 0
        });
        try
        {
            db.SaveChanges();
            logger.LogInformation("Seeded ClusterLeader row (primary).");
        }
        catch (DbUpdateException ex)
        {
            // Two nodes booting at once both passed the existence check above and both
            // tried to INSERT. The DB would reject one with a PK / unique-constraint
            // violation on Resource — that's the only DbUpdateException we want to swallow
            // here. Permission errors, connection drops, schema drift, NOT NULL violations
            // all also surface as DbUpdateException, and silently swallowing those would
            // hide real problems behind a "lost the seed race" log line.
            //
            // Disambiguation strategy: detach our pending insert, re-query the table for
            // the canonical row. If it now exists, the other node really did win the race
            // — log info and return. If it still doesn't exist, something else broke;
            // rethrow so the boot fails loudly.
            db.ChangeTracker.Entries<ClusterLeader>()
                .Where(e => e.Entity.Resource == "primary" && e.State == EntityState.Added)
                .ToList()
                .ForEach(e => e.State = EntityState.Detached);

            var otherNodeWon = db.ClusterLeaders.AsNoTracking().Any(x => x.Resource == "primary");
            if (!otherNodeWon)
            {
                logger.LogError(ex,
                    "ClusterLeader seed failed and the row still does not exist — likely a real " +
                    "DB error (permissions, schema drift, connection drop). Rethrowing so the boot " +
                    "fails loudly rather than starting an instance that will misbehave at lease time.");
                throw;
            }

            logger.LogInformation(ex,
                "ClusterLeader seed lost the race against another booting node — that's fine, the row exists.");
        }
    }

    /// <summary>
    /// One-time backfill for the <c>TriggerTypesJson</c> + <c>ActivityCount</c> columns added
    /// in migration <c>AddTriggerTypesJson</c>. Workflows written before that migration have
    /// <c>TriggerTypesJson = NULL</c>, which would make them appear as "0 activities" / no
    /// triggers in the list and dashboard endpoints. Runs on every startup but is a no-op
    /// once all rows are populated (cheap WHERE TriggerTypesJson IS NULL probe).
    /// </summary>
    private static void BackfillWorkflowComputedColumns(NodePilotDbContext db, ILogger logger)
    {
        var pending = db.Workflows.Where(w => w.TriggerTypesJson == null).ToList();
        if (pending.Count == 0) return;

        foreach (var wf in pending)
            WorkflowMetadata.PopulateComputedColumns(wf);
        db.SaveChanges();
        logger.LogInformation("Backfilled TriggerTypesJson + ActivityCount for {Count} workflow(s).",
            pending.Count);
    }
}

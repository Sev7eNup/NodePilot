using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;
using Npgsql;

namespace NodePilot.Engine.Execution;

internal static class WorkflowDbWriteMetrics
{
    internal static async Task<int> ExecuteMeasuredAsync(
        string operation,
        Func<Task<int>> write)
    {
        if (!HasDbSaveMetricsListener())
            return await write();

        var startTimestamp = Stopwatch.GetTimestamp();
        var operationTag = new KeyValuePair<string, object?>("operation", operation);
        try
        {
            var rows = await write();
            var statusTag = new KeyValuePair<string, object?>("status", "success");
            EngineMetrics.DbSaveChanges.Add(1, operationTag, statusTag);
            EngineMetrics.DbSaveChangesDuration.Record(ElapsedMilliseconds(startTimestamp), operationTag, statusTag);
            EngineMetrics.DbSaveChangesRows.Record(rows, operationTag);
            return rows;
        }
        catch (OperationCanceledException)
        {
            var statusTag = new KeyValuePair<string, object?>("status", "cancelled");
            EngineMetrics.DbSaveChanges.Add(1, operationTag, statusTag);
            EngineMetrics.DbSaveChangesDuration.Record(ElapsedMilliseconds(startTimestamp), operationTag, statusTag);
            throw;
        }
        catch
        {
            var statusTag = new KeyValuePair<string, object?>("status", "failure");
            EngineMetrics.DbSaveChanges.Add(1, operationTag, statusTag);
            EngineMetrics.DbSaveChangesDuration.Record(ElapsedMilliseconds(startTimestamp), operationTag, statusTag);
            throw;
        }
    }

    internal static async Task<int> SaveChangesMeasuredAsync(
        this NodePilotDbContext db,
        string operation,
        CancellationToken ct)
    {
        if (!HasDbSaveMetricsListener())
            return await SaveChangesIdempotentAsync(db, ct);

        var startTimestamp = Stopwatch.GetTimestamp();
        var operationTag = new KeyValuePair<string, object?>("operation", operation);

        try
        {
            var rows = await SaveChangesIdempotentAsync(db, ct);

            var statusTag = new KeyValuePair<string, object?>("status", "success");
            EngineMetrics.DbSaveChanges.Add(1, operationTag, statusTag);
            EngineMetrics.DbSaveChangesDuration.Record(ElapsedMilliseconds(startTimestamp), operationTag, statusTag);
            EngineMetrics.DbSaveChangesRows.Record(rows, operationTag);
            return rows;
        }
        catch (OperationCanceledException)
        {
            var statusTag = new KeyValuePair<string, object?>("status", "cancelled");
            EngineMetrics.DbSaveChanges.Add(1, operationTag, statusTag);
            EngineMetrics.DbSaveChangesDuration.Record(ElapsedMilliseconds(startTimestamp), operationTag, statusTag);
            throw;
        }
        catch
        {
            var statusTag = new KeyValuePair<string, object?>("status", "failure");
            EngineMetrics.DbSaveChanges.Add(1, operationTag, statusTag);
            EngineMetrics.DbSaveChangesDuration.Record(ElapsedMilliseconds(startTimestamp), operationTag, statusTag);
            throw;
        }
    }

    /// <summary>
    /// Idempotent SaveChanges wrapper: if EF Core's ExecutionStrategy retried after a
    /// transient failure and the original INSERT actually committed before the network blip,
    /// the retry hits a unique-key violation. The row is already in the DB — not a real error.
    /// We reset Added entities to Unchanged so subsequent UPDATE operations on the same
    /// context succeed, and return 0 rows-affected.
    ///   * SQL Server surfaces this as SqlException 2627 (PK) or 2601 (UNIQUE).
    ///   * Postgres surfaces this as PostgresException SQLSTATE 23505 (unique_violation).
    /// SQLite never sees this code path (no transient retries configured).
    /// </summary>
    private static async Task<int> SaveChangesIdempotentAsync(NodePilotDbContext db, CancellationToken ct)
    {
        try
        {
            return await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is SqlException { Number: 2627 or 2601 }
            || ex.InnerException is PostgresException { SqlState: "23505" })
        {
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added))
                entry.State = EntityState.Unchanged;
            return 0;
        }
    }

    private static bool HasDbSaveMetricsListener()
        => EngineMetrics.DbSaveChanges.Enabled
           || EngineMetrics.DbSaveChangesDuration.Enabled
           || EngineMetrics.DbSaveChangesRows.Enabled;

    private static double ElapsedMilliseconds(long startTimestamp)
        => Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}

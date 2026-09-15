using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace NodePilot.Data;

/// <summary>Reserves one available outbox row in a single database statement.</summary>
public static class ExecutionDispatchOutboxClaimer
{
    public static async Task<Guid?> TryClaimAsync(
        NodePilotDbContext db, DateTime now, DateTime leaseUntil, string leaseOwner,
        IReadOnlyCollection<Guid> blockedWorkflowIds, CancellationToken ct)
    {
        var parameters = new List<object> { now, leaseUntil, leaseOwner };
        string sql;
        if (db.Database.IsNpgsql())
        {
            var blockedFilter = blockedWorkflowIds.Count == 0 ? "" : "AND q.\"WorkflowId\" <> ALL ({3})";
            if (blockedWorkflowIds.Count > 0)
                parameters.Add(new NpgsqlParameter("blocked", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
                {
                    Value = blockedWorkflowIds.ToArray(),
                });
            sql = $$"""
                -- NodePilot:ExecutionDispatchClaim
                WITH candidate AS (
                    SELECT q."ExecutionId"
                    FROM "ExecutionDispatchOutbox" AS q
                    WHERE q."AvailableAt" <= {0}
                      AND (q."LeaseExpiresAt" IS NULL OR q."LeaseExpiresAt" <= {0})
                      {{blockedFilter}}
                    ORDER BY q."Priority" DESC, q."CreatedAt", q."ExecutionId"
                    LIMIT 1 FOR UPDATE SKIP LOCKED
                )
                UPDATE "ExecutionDispatchOutbox" AS item
                SET "LeaseOwner" = {2}, "LeaseExpiresAt" = {1},
                    "AttemptCount" = item."AttemptCount" + 1
                FROM candidate
                WHERE item."ExecutionId" = candidate."ExecutionId"
                RETURNING item."ExecutionId";
                """;
        }
        else if (db.Database.IsSqlServer())
        {
            var blockedFilter = blockedWorkflowIds.Count == 0 ? "" : """
                AND NOT EXISTS (
                    SELECT 1 FROM OPENJSON({3}) WITH ([WorkflowId] uniqueidentifier '$') AS blocked
                    WHERE blocked.[WorkflowId] = q.[WorkflowId])
                """;
            if (blockedWorkflowIds.Count > 0) parameters.Add(JsonSerializer.Serialize(blockedWorkflowIds));
            // READCOMMITTEDLOCK permits READPAST with RCSI. ROWLOCK cannot be combined with it.
            sql = $$"""
                -- NodePilot:ExecutionDispatchClaim
                ;WITH candidate AS (
                    SELECT TOP (1) q.[ExecutionId], q.[LeaseOwner], q.[LeaseExpiresAt], q.[AttemptCount]
                    FROM [ExecutionDispatchOutbox] AS q WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                    WHERE q.[AvailableAt] <= {0}
                      AND (q.[LeaseExpiresAt] IS NULL OR q.[LeaseExpiresAt] <= {0})
                      {{blockedFilter}}
                    ORDER BY q.[Priority] DESC, q.[CreatedAt], q.[ExecutionId]
                )
                UPDATE candidate
                SET [LeaseOwner] = {2}, [LeaseExpiresAt] = {1}, [AttemptCount] = [AttemptCount] + 1
                OUTPUT inserted.[ExecutionId];
                """;
        }
        else if (db.Database.IsSqlite())
        {
            var blockedFilter = blockedWorkflowIds.Count == 0 ? "" : """
                AND q."WorkflowId" NOT IN (SELECT value FROM json_each({3}))
                """;
            if (blockedWorkflowIds.Count > 0)
                parameters.Add(JsonSerializer.Serialize(blockedWorkflowIds.Select(id => id.ToString("D").ToUpperInvariant())));
            sql = $$"""
                -- NodePilot:ExecutionDispatchClaim
                UPDATE "ExecutionDispatchOutbox"
                SET "LeaseOwner" = {2}, "LeaseExpiresAt" = {1}, "AttemptCount" = "AttemptCount" + 1
                WHERE "ExecutionId" = (
                    SELECT q."ExecutionId" FROM "ExecutionDispatchOutbox" AS q
                    WHERE q."AvailableAt" <= {0}
                      AND (q."LeaseExpiresAt" IS NULL OR q."LeaseExpiresAt" <= {0})
                      {{blockedFilter}}
                    ORDER BY q."Priority" DESC, q."CreatedAt", q."ExecutionId"
                    LIMIT 1
                )
                RETURNING "ExecutionId";
                """;
        }
        else
        {
            throw new NotSupportedException($"Outbox claims do not support provider '{db.Database.ProviderName}'.");
        }

        // Use EF's command/connection interceptors and timeout, but never replay a dequeue after
        // an uncertain commit. A lost response leaves its row recoverable at lease expiry.
        using var criticalSection = db.GetService<IConcurrencyDetector>().EnterCriticalSection();
        var command = db.GetService<IRawSqlCommandBuilder>().Build(sql, parameters, db.Model);
        var result = await command.RelationalCommand.ExecuteScalarAsync(
            new RelationalCommandParameterObject(
                db.GetService<IRelationalConnection>(), command.ParameterValues, null, db,
                db.GetService<IRelationalCommandDiagnosticsLogger>(), CommandSource.ExecuteSqlRaw), ct);
        return result switch
        {
            null or DBNull => null,
            Guid id => id,
            string id => Guid.Parse(id),
            _ => throw new InvalidOperationException("The outbox claim returned an invalid execution id."),
        };
    }
}

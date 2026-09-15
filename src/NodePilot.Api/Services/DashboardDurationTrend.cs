using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Services;

public sealed class DashboardDurationTrend(NodePilotDbContext db)
{
    public async Task<DurationTrendResponse> ReadAsync(
        AccessibleFolderSet accessible, int windowHours, Guid? workflowId, CancellationToken ct,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var since = now.AddHours(-windowHours);
        var bucketCount = windowHours == 1 ? 12 : 24;
        var bucketMs = TimeSpan.FromHours(windowHours).TotalMilliseconds / bucketCount;
        var buckets = Enumerable.Range(0, bucketCount)
            .Select(i => new DurationBucket(since.AddMilliseconds(i * bucketMs), 0, null, null)).ToList();
        var workflows = db.Workflows.AsNoTracking().ScopeToAccessibleFolders(accessible);
        if (workflows is null) return new(buckets, []);

        var options = await workflows.OrderBy(w => w.Name).ThenBy(w => w.Id)
            .Select(w => new DurationWorkflow(w.Id, w.Name)).ToListAsync(ct);
        // An unknown or inaccessible selection must never fall back to the global series.
        if (workflowId.HasValue && options.All(w => w.Id != workflowId)) return new(buckets, options);

        var postgres = db.Database.IsNpgsql();
        var sqlServer = db.Database.IsSqlServer();
        string Elapsed(string end, string start) => postgres
            ? $"EXTRACT(EPOCH FROM ({end} - {start})) * 1000.0"
            : sqlServer ? $"DATEDIFF_BIG(microsecond, {start}, {end}) / 1000.0"
            : $"ROUND((julianday({end}) - julianday({start})) * 86400000.0)";
        var duration = Elapsed("e.\"CompletedAt\"", "e.\"StartedAt\"");
        var offset = Elapsed("e.\"StartedAt\"", "{0}");
        var bucket = postgres ? $"CAST(FLOOR(({offset}) / {{2}}) AS int)" : $"CAST(({offset}) / {{2}} AS int)";
        var folderSource = postgres
            ? "SELECT CAST(value AS uuid) FROM jsonb_array_elements_text(CAST({3} AS jsonb))"
            : sqlServer ? "SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON({3})"
            : "SELECT value FROM json_each({3})";
        var scope = accessible.IsUnrestricted ? "" : $"AND w.\"FolderId\" IN ({folderSource})";
        var selection = workflowId.HasValue ? "AND e.\"WorkflowId\" = {4}" : "";
        // Rank in the database: only 24 aggregate rows cross the wire, even for large windows.
        // Median averages the middle pair; P95 is the nearest-rank 95th percentile.
        var sql = $$"""
            WITH samples AS (
                SELECT {{bucket}} AS "Bucket", {{duration}} AS "Duration"
                FROM "WorkflowExecutions" e
                JOIN "Workflows" w ON w."Id" = e."WorkflowId"
                WHERE e."StartedAt" >= {0} AND e."StartedAt" < {1}
                  AND e."CompletedAt" >= e."StartedAt" AND e."CompletedAt" <= {1}
                  AND e."Status" IN ('Succeeded', 'Failed')
                  {{scope}} {{selection}}
            ), ranked AS (
                SELECT "Bucket", "Duration",
                    ROW_NUMBER() OVER (PARTITION BY "Bucket" ORDER BY "Duration") AS "Rank",
                    COUNT(*) OVER (PARTITION BY "Bucket") AS "Samples"
                FROM samples
            )
            SELECT "Bucket", CAST(MAX("Samples") AS int) AS "Count",
                CAST(AVG(CASE WHEN "Rank" IN (("Samples" + 1) / 2, ("Samples" + 2) / 2)
                    THEN "Duration" END) AS {{(postgres ? "double precision" : "float")}}) AS "MedianMs",
                CAST(MAX(CASE WHEN "Rank" = ("Samples" * 95 + 99) / 100
                    THEN "Duration" END) AS {{(postgres ? "double precision" : "float")}}) AS "P95Ms"
            FROM ranked GROUP BY "Bucket"
            """;
        var folderIds = accessible.FolderIds.Select(id => sqlServer || postgres ? id.ToString() : id.ToString().ToUpperInvariant());
        var rows = await db.Database.SqlQueryRaw<DurationRow>(sql,
            since, now, bucketMs, JsonSerializer.Serialize(folderIds), workflowId ?? Guid.Empty).ToListAsync(ct);
        foreach (var row in rows)
            if (row.Bucket >= 0 && row.Bucket < buckets.Count)
                buckets[row.Bucket] = buckets[row.Bucket] with { Count = row.Count, MedianMs = row.MedianMs, P95Ms = row.P95Ms };
        return new(buckets, options);
    }

    private sealed record DurationRow(int Bucket, int Count, double MedianMs, double P95Ms);
}

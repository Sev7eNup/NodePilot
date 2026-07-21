using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler.Options;

namespace NodePilot.Scheduler;

/// <summary>
/// Periodically deletes old terminal <see cref="NodePilot.Core.Models.WorkflowExecution"/>
/// rows (and their <c>StepExecution</c> children via FK cascade) so a continuously
/// scheduled workflow does not grow the DB unbounded.
///
/// Config (appsettings.json):
///   "Retention": {
///     "Executions": {
///       "Enabled": true,
///       "MaxAgeDays": 30,
///       "IntervalMinutes": 60,
///       "BatchSize": 500
///     }
///   }
///
/// Safety rails:
///   - Enabled by default (appsettings.json). Set <c>Retention:Executions:Enabled=false</c>
///     to opt out (e.g. deployments that archive externally and rely on the DB as source of
///     truth for N years).
///   - Only rows in a terminal status (Succeeded / Failed / Cancelled) are candidates —
///     an in-flight execution that happens to be older than the cutoff is NEVER deleted.
///   - Deletes in bounded batches (<c>BatchSize</c>, default 500) so one pass never holds
///     a long-running transaction on SQLite.
/// </summary>
public class ExecutionRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    // Hot-reload: hold the live monitor (not a cached snapshot) so a config edit of
    // Retention:Executions:* takes effect on the next sweep pass without a restart.
    private readonly IOptionsMonitor<RetentionOptions> _opts;
    private readonly IClusterStateProvider _cluster;
    private readonly ILogger<ExecutionRetentionService> _logger;

    // Resolved per pass from the live monitor — never cached across passes.
    private ExecutionsRetentionOptions Opts => _opts.CurrentValue.Executions;

    public ExecutionRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> opts,
        IClusterStateProvider cluster,
        ILogger<ExecutionRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts;
        _cluster = cluster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so the patcher + trigger orchestrator have time to settle on a
        // cold start before we start issuing DELETEs.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("ExecutionRetentionService started (hot-reload: per-pass config).");

        while (!stoppingToken.IsCancellationRequested)
        {
            // HA gate: only the leader may run retention sweeps. Otherwise a follower would
            // contend on the same DELETEs and double the IO cost on the shared DB.
            if (!_cluster.IsLeader)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await RunIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            var interval = TimeSpan.FromMinutes(Math.Max(1, Opts.IntervalMinutes));
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("ExecutionRetentionService stopped.");
    }

    /// <summary>
    /// Exactly one sweep iteration: reads the live config, skips when disabled, else runs one
    /// <see cref="PurgeOnceAsync"/> pass + heartbeat. No <c>Task.Delay</c> — the loop in
    /// <see cref="ExecuteAsync"/> owns the inter-pass spacing. Internal so unit tests can drive
    /// a single pass (incl. the hot-reload Enabled-toggle path) without the 30-second warm-up.
    /// </summary>
    internal async Task RunIterationAsync(CancellationToken ct)
    {
        // Hot-reload: a live toggle to Enabled=false parks the sweep instead of killing the
        // service, so flipping back to true later takes effect without a restart. The outer
        // loop's Task.Delay still runs, so a disabled sweep polls cheaply once per interval.
        var o = Opts;
        if (!o.Enabled)
        {
            _logger.LogDebug("ExecutionRetentionService pass skipped (Retention:Executions:Enabled=false).");
            return;
        }

        var maxAgeDays = Math.Max(1, o.MaxAgeDays);
        var intervalMinutes = Math.Max(1, o.IntervalMinutes);
        var batchSize = Math.Max(10, o.BatchSize);

        try
        {
            var sw = Stopwatch.StartNew();
            var deleted = await PurgeOnceAsync(maxAgeDays, batchSize, ct);
            sw.Stop();
            if (deleted > 0)
                _logger.LogInformation("Retention pass deleted {Count} old executions (and cascaded step rows).", deleted);
            var tags = new TagList { new("nodepilot.retention.service", "execution") };
            SchedulerMetrics.RetentionRowsDeleted.Add(deleted, tags);
            SchedulerMetrics.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            await HeartbeatAsync(intervalMinutes, $"ok: {deleted} deleted", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var errTags = new TagList { new("nodepilot.retention.service", "execution") };
            SchedulerMetrics.RetentionSweepErrors.Add(1, errTags);
            _logger.LogError(ex, "Retention pass failed — will retry on next interval.");
        }
    }

    private async Task HeartbeatAsync(int intervalMinutes, string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        await SystemHealthWriter.BeatAsync(db, "ExecutionRetentionService",
            expectedIntervalSeconds: intervalMinutes * 60, status: status, ct: ct);
    }

    // Exposed as internal so unit tests can drive a single pass without waiting the 30-second
    // warm-up or a full interval.
    internal async Task<int> PurgeOnceAsync(int maxAgeDays, int batchSize, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        var archivePath = ValidateArchivePath(Opts.ArchivePath);
        int totalDeleted = 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        while (!ct.IsCancellationRequested)
        {
            // Only purge terminal-status rows. StartedAt is the retention clock because
            // CompletedAt can be null for rows that crashed mid-run without updating.
            var batchIds = await db.WorkflowExecutions
                .Where(e => e.StartedAt < cutoff
                    && (e.Status == ExecutionStatus.Succeeded
                        || e.Status == ExecutionStatus.Failed
                        || e.Status == ExecutionStatus.Cancelled))
                .OrderBy(e => e.StartedAt)
                .Take(batchSize)
                .Select(e => e.Id)
                .ToListAsync(ct);

            if (batchIds.Count == 0) break;

            var rows = await db.WorkflowExecutions
                .Where(e => batchIds.Contains(e.Id))
                .ToListAsync(ct);

            if (!string.IsNullOrWhiteSpace(archivePath))
                await ArchiveAsync(archivePath, rows, ct);

            db.WorkflowExecutions.RemoveRange(rows);
            totalDeleted += await db.SaveChangesAsync(ct);

            if (batchIds.Count < batchSize) break;
        }

        return totalDeleted;
    }

    // L-16: probe — caches whether the archive path is write-good, bad, or unset.
    // null = not yet probed, "" = disabled (probe failed or unset), otherwise the validated
    // absolute path. Lives on the instance so we don't re-probe the disk every hour.
    //
    // Hot-reload: when the configured path changes (Admin-Settings-UI save or runtime config
    // overlay), we drop the cache so the new path is re-validated (and its directory created)
    // on the next sweep pass — without a service restart.
    private string? _validatedArchivePath;
    private string? _lastProbedArchivePath;

    /// <summary>
    /// L-16: validate the configured archive path: normalize it via
    /// <see cref="Path.GetFullPath"/>, ensure the directory exists (create if needed), and
    /// probe-write a sentinel file to confirm we have write permission. If the path is
    /// missing, malformed, or read-only, we log loudly and return empty-string so the
    /// caller treats archival as disabled — retention STILL deletes rows, because the
    /// alternative (infinitely-growing DB because the archive directory is broken) is
    /// worse. Operators who care about the archive are expected to monitor the log.
    /// </summary>
    private string ValidateArchivePath(string? configured)
    {
        // Hot-reload invalidation: a changed configured path forces a fresh probe.
        if (!string.Equals(_lastProbedArchivePath, configured, StringComparison.Ordinal))
        {
            _lastProbedArchivePath = configured;
            _validatedArchivePath = null;
        }
        if (_validatedArchivePath is not null) return _validatedArchivePath;
        if (string.IsNullOrWhiteSpace(configured)) { _validatedArchivePath = ""; return ""; }

        try
        {
            // Path.GetFullPath normalizes "../", catches invalid chars, and produces an
            // absolute path we can hand to File operations without surprises from the
            // process CWD.
            var full = Path.GetFullPath(configured);
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".nodepilot-archive-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            _validatedArchivePath = full;
            return full;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Retention archive path '{Path}' is not writable. Retention will still delete " +
                "rows but will NOT archive them. Fix Retention:Executions:ArchivePath or clear " +
                "it to silence this message.", configured);
            _validatedArchivePath = "";
            return "";
        }
    }

    /// <summary>
    /// Appends a batch of soon-to-be-deleted rows to an NDJSON (newline-delimited JSON)
    /// archive file on disk, one row per line. Intentionally append-only and simple — cold
    /// storage integrations (S3 / Azure Blob / glacier) can watch the directory and ship
    /// files out of band. NDJSON is the lowest-common-denominator ETL format and streams
    /// naturally into Splunk, Elastic, BigQuery, DuckDB, etc. without a schema definition.
    /// </summary>
    private static async Task ArchiveAsync(string archivePath, IEnumerable<WorkflowExecution> rows, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(archivePath);
            // One file per UTC-day so a seven-year retention window doesn't produce a single
            // multi-gigabyte file. Roll at midnight — matches Serilog's rolling convention.
            var path = Path.Combine(archivePath, $"executions-{DateTime.UtcNow:yyyyMMdd}.ndjson");
            await using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var sw = new StreamWriter(fs);
            foreach (var row in rows)
            {
                var line = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = row.Id,
                    workflowId = row.WorkflowId,
                    status = row.Status.ToString(),
                    startedAt = row.StartedAt,
                    completedAt = row.CompletedAt,
                    triggeredBy = row.TriggeredBy,
                    errorMessage = row.ErrorMessage,
                    traceId = row.TraceId,
                    spanId = row.SpanId,
                    returnData = row.ReturnData,
                    inputParametersJson = row.InputParametersJson,
                });
                await sw.WriteLineAsync(line.AsMemory(), ct);
            }
        }
        catch
        {
            // Best-effort: a disk-full or permission error on the archive path must not block
            // deletion. The alternative is an indefinitely-growing DB, which is worse. Operators
            // who care about the archive should monitor the file-size / mtime and alert if it
            // stops advancing.
        }
    }
}

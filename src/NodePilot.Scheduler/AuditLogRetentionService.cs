using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodePilot.Data;
using NodePilot.Scheduler.Options;

namespace NodePilot.Scheduler;

/// <summary>
/// Periodically prunes old <c>AuditLog</c> rows to keep the compliance table from growing
/// unbounded. Default retention is 365 days — much longer than
/// <see cref="ExecutionRetentionService"/> because audit is typically bound by regulatory
/// requirements (SOC2 / ISO 27001 usually expect 1 year of access-trail online).
///
/// Config (appsettings.json):
///   "Retention": {
///     "AuditLog": {
///       "Enabled": true,
///       "MaxAgeDays": 365,
///       "IntervalMinutes": 720,       // twice a day by default
///       "BatchSize": 1000,
///       "ArchivePath": "C:/ProgramData/NodePilot/audit-archive",
///       "VerifyIntervalMinutes": 1440 // daily archive-integrity check
///     }
///   }
///
/// Enabled by default with a conservative 365-day window that satisfies typical SOC2/ISO
/// expectations. Set <c>Retention:AuditLog:Enabled=false</c> for deployments with an
/// external audit archive that treat the DB as the hot tier only.
///
/// <para>
/// Archive layout (Phase 3): each batch is written as <c>audit-{date}-{ticks}-{rand}.ndjson.gz</c>
/// — gzipped NDJSON. The SHA-256 of the gzip file is dropped next to it as
/// <c>{name}.sha256</c> in standard <c>sha256sum</c> format (<c>{hex}  {basename}</c>).
/// A periodic verify pass recomputes the hash to detect silent corruption (bit-rot,
/// partial overwrite, accidental edits). Files predating Phase 3 (no <c>.sha256</c>
/// sidecar) are inspected but only count toward the "sidecar missing" metric — they
/// can't drift-check without their original hash.
/// </para>
/// </summary>
public class AuditLogRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    // Hot-reload: hold the live monitor (not a cached snapshot) so a config edit of
    // Retention:AuditLog:* takes effect on the next sweep pass without a restart.
    private readonly IOptionsMonitor<RetentionOptions> _opts;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider _cluster;
    private readonly ILogger<AuditLogRetentionService> _logger;

    // Resolved per pass from the live monitor — never cached across passes.
    private AuditLogRetentionOptions Opts => _opts.CurrentValue.AuditLog;

    private DateTime _lastVerifyUtc = DateTime.MinValue;

    public AuditLogRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> opts,
        NodePilot.Core.Interfaces.IClusterStateProvider cluster,
        ILogger<AuditLogRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _opts = opts;
        _cluster = cluster;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("AuditLogRetentionService started (hot-reload: per-pass config).");

        while (!stoppingToken.IsCancellationRequested)
        {
            // HA gate: only the leader sweeps audit log so two nodes don't race on DELETEs.
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

            var interval = TimeSpan.FromMinutes(Math.Max(5, Opts.IntervalMinutes));
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("AuditLogRetentionService stopped.");
    }

    /// <summary>
    /// Exactly one sweep iteration: reads the live config, skips when disabled, else runs one
    /// <see cref="PurgeOnceAsync"/> pass + heartbeat + (cadence-gated) archive-integrity verify.
    /// No <c>Task.Delay</c> — the loop in <see cref="ExecuteAsync"/> owns the inter-pass spacing.
    /// Internal so unit tests can drive a single pass (incl. the hot-reload Enabled-toggle path)
    /// without the 60-second warm-up.
    /// </summary>
    internal async Task RunIterationAsync(CancellationToken ct)
    {
        // Hot-reload: a live toggle to Enabled=false parks the sweep instead of killing the
        // service, so flipping back to true later takes effect without a restart.
        var o = Opts;
        if (!o.Enabled)
        {
            _logger.LogDebug("AuditLogRetentionService pass skipped (Retention:AuditLog:Enabled=false).");
            return;
        }

        var maxAgeDays = Math.Max(30, o.MaxAgeDays);
        var intervalMinutes = Math.Max(5, o.IntervalMinutes);
        var batchSize = Math.Max(10, o.BatchSize);

        try
        {
            var sw = Stopwatch.StartNew();
            var deleted = await PurgeOnceAsync(maxAgeDays, batchSize, ct);
            sw.Stop();
            if (deleted > 0)
                _logger.LogInformation("AuditLog retention pass deleted {Count} old entries.", deleted);

            var tags = new TagList { new("nodepilot.retention.service", "audit_log") };
            SchedulerMetrics.RetentionRowsDeleted.Add(deleted, tags);
            SchedulerMetrics.RetentionSweepDuration.Record(sw.Elapsed.TotalMilliseconds, tags);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            await SystemHealthWriter.BeatAsync(db, "AuditLogRetentionService",
                expectedIntervalSeconds: intervalMinutes * 60, status: $"ok: {deleted} deleted", ct: ct);

            // Archive integrity verify pass — independent cadence (default daily) so a
            // 12h-interval retention doesn't pay the SHA-256 walk twice per day. The
            // first pass always runs (so a freshly-started service immediately checks
            // historic archives once); subsequent passes gate on VerifyIntervalMinutes.
            if (o.VerifyIntervalMinutes > 0
                && !string.IsNullOrWhiteSpace(o.ArchivePath)
                && DateTime.UtcNow - _lastVerifyUtc >= TimeSpan.FromMinutes(o.VerifyIntervalMinutes))
            {
                try
                {
                    await VerifyArchiveIntegrityAsync(o.ArchivePath!, o.VerifyMaxFilesPerPass, ct);
                }
                catch (Exception verifyEx)
                {
                    _logger.LogError(verifyEx, "Audit archive verify pass failed");
                }
                _lastVerifyUtc = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var errTags = new TagList { new("nodepilot.retention.service", "audit_log") };
            SchedulerMetrics.RetentionSweepErrors.Add(1, errTags);
            _logger.LogError(ex, "AuditLog retention pass failed — will retry on next interval.");
        }
    }

    internal async Task<int> PurgeOnceAsync(int maxAgeDays, int batchSize, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        var archivePath = ValidateArchivePath(Opts.ArchivePath);
        int totalDeleted = 0;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        while (!ct.IsCancellationRequested)
        {
            var batchIds = await db.AuditLog
                .Where(a => a.Timestamp < cutoff)
                .OrderBy(a => a.Timestamp)
                .Take(batchSize)
                .Select(a => a.Id)
                .ToListAsync(ct);

            if (batchIds.Count == 0) break;

            var rows = await db.AuditLog.Where(a => batchIds.Contains(a.Id)).ToListAsync(ct);

            // Archive-then-delete ordering is load-bearing for audit compliance: a disk-full
            // or permission error on the archive volume must NOT cause rows to disappear.
            string? archivedFilePath = null;
            if (!string.IsNullOrWhiteSpace(archivePath))
            {
                archivedFilePath = await ArchiveAsync(archivePath, rows, ct);
                if (archivedFilePath is null) break;   // bail out of this pass; next interval retries.
            }

            try
            {
                db.AuditLog.RemoveRange(rows);
                totalDeleted += await db.SaveChangesAsync(ct);
            }
            catch
            {
                // F-4: archive succeeded but the DB delete failed (lock, constraint violation,
                // connection drop). Without the rollback below, the next retention pass would
                // re-select the same rows and append a second archive copy — duplicated audit
                // events break SIEM frequency analytics and forensics deduplication. Rolling
                // back the per-batch archive file keeps the file/DB invariant clean. If this
                // delete itself fails we live with the duplicate — recoverable, vs. silent loss.
                if (archivedFilePath is not null)
                {
                    var sidecarPath = archivedFilePath + ".sha256";
                    TryDeleteFile(archivedFilePath, "archive-rollback-gz");
                    TryDeleteFile(sidecarPath, "archive-rollback-sidecar");
                }
                throw;
            }

            if (batchIds.Count < batchSize) break;
        }

        return totalDeleted;
    }

    // L-16: validation of the audit archive path. Unlike ExecutionRetention, we must NOT
    // coerce a broken path to "archive-disabled" — if an admin intentionally configured an
    // archive path, audit rows must be retained in-DB until the archive works. So on probe
    // failure we keep the configured path (downstream ArchiveAsync will fail, retention
    // keeps rows, log alerts admin via DB growth) but emit a loud one-time error so ops
    // sees the cause rather than just the symptom.
    //
    // Hot-reload: when the configured path changes (Admin-Settings-UI save or runtime config
    // overlay), drop the cache so the new path is re-probed on the next sweep pass — without
    // a restart. The failure-return-configured compliance invariant is preserved (only the
    // cache flags reset; the broken-path return logic below is untouched).
    private string? _validatedArchivePath;
    private bool _archivePathValidated;
    private string? _lastProbedArchivePath;

    private string ValidateArchivePath(string? configured)
    {
        // Hot-reload invalidation: a changed configured path forces a fresh probe.
        if (!string.Equals(_lastProbedArchivePath, configured, StringComparison.Ordinal))
        {
            _lastProbedArchivePath = configured;
            _archivePathValidated = false;
            _validatedArchivePath = null;
        }
        if (_archivePathValidated) return _validatedArchivePath ?? "";
        _archivePathValidated = true;

        if (string.IsNullOrWhiteSpace(configured))
        {
            _validatedArchivePath = "";
            return "";
        }

        try
        {
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
                "AuditLog archive path '{Path}' is not writable. Retention will NOT delete " +
                "rows until this is fixed (audit rows must be archived before deletion). " +
                "Fix Retention:AuditLog:ArchivePath or clear it to switch to archive-free mode.",
                configured);
            // Return the original (broken) path so ArchiveAsync fails and PurgeOnceAsync
            // breaks out of its batch loop. We keep rows in-DB on purpose — better a big
            // DB than silently-lost audit rows.
            _validatedArchivePath = configured;
            return configured;
        }
    }

    /// <summary>
    /// Writes the audit batch as gzipped NDJSON to a per-batch file (one file per Purge batch,
    /// not one per day) plus a SHA-256 sidecar at <c>{name}.sha256</c>. Per-batch granularity
    /// makes the (archive, delete) pair idempotent across retries — the rollback in
    /// PurgeOnceAsync simply deletes the per-batch file pair.
    /// Critical properties:
    ///   - tmp-write + atomic rename: a crash between write and rename leaves a .tmp file
    ///     that ops can clean up without affecting durability of committed batches.
    ///   - SHA-256 sidecar is written AFTER the gzip rename, so a sidecar without a .gz
    ///     never happens; a .gz without a sidecar only happens on sidecar-write failure
    ///     (logged loudly; the file still archives correctly, just without integrity proof).
    ///   - returns the final .gz path (or null on failure) so PurgeOnceAsync can roll back
    ///     both files when the matching DB delete throws — this is what closes F-4.
    /// </summary>
    internal async Task<string?> ArchiveAsync(string archivePath, IEnumerable<NodePilot.Core.Models.AuditLogEntry> rows, CancellationToken ct)
    {
        var ticks = DateTime.UtcNow.Ticks;
        var randomSuffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var finalPath = Path.Combine(archivePath, $"audit-{DateTime.UtcNow:yyyyMMdd}-{ticks}-{randomSuffix}.ndjson.gz");
        var tmpPath = finalPath + ".tmp";

        try
        {
            Directory.CreateDirectory(archivePath);
            await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var gz = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: false))
            await using (var sw = new StreamWriter(gz, new UTF8Encoding(false)))
            {
                foreach (var row in rows)
                {
                    var line = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id = row.Id,
                        timestamp = row.Timestamp,
                        userId = row.UserId,
                        username = row.Username,
                        action = row.Action,
                        resourceType = row.ResourceType,
                        resourceId = row.ResourceId,
                        details = row.Details,
                        ipAddress = row.IpAddress,
                    });
                    await sw.WriteLineAsync(line.AsMemory(), ct);
                }
            }
            File.Move(tmpPath, finalPath);

            await WriteSidecarAsync(finalPath, ct);
            return finalPath;
        }
        catch (Exception ex)
        {
            TryDeleteFile(tmpPath, "archive-tmp");
            // If the gz was renamed but the sidecar write later threw, the gz is still
            // valid — clean up only the failed-state .tmp + return null so the retention
            // pass retries on next interval.
            _logger.LogError(ex, "Audit archive write failed — will NOT delete rows this pass. Retention pass retries on next interval.");
            return null;
        }
    }

    /// <summary>
    /// Computes the SHA-256 of <paramref name="archiveFile"/> and writes the standard
    /// <c>sha256sum</c>-format sidecar (<c>{hex}  {basename}\n</c>). Two spaces between
    /// hash and name matches GNU coreutils binary-mode output so operators can verify
    /// with <c>sha256sum -c audit-*.sha256</c> on Linux or PowerShell's <c>Get-FileHash</c>
    /// for ad-hoc checks.
    /// </summary>
    private static async Task WriteSidecarAsync(string archiveFile, CancellationToken ct)
    {
        var hashHex = await ComputeSha256HexAsync(archiveFile, ct);
        var sidecarPath = archiveFile + ".sha256";
        var sidecarLine = $"{hashHex}  {Path.GetFileName(archiveFile)}\n";
        await File.WriteAllTextAsync(sidecarPath, sidecarLine, new UTF8Encoding(false), ct);
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void TryDeleteFile(string path, string contextTag)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Audit archive cleanup failed ({Context}): {Path}. Operators may need to dedupe.",
                contextTag, path);
        }
    }

    /// <summary>
    /// Walks <paramref name="archivePath"/> for <c>audit-*.ndjson.gz</c> files (oldest
    /// first), recomputes the SHA-256 of each, and compares it to the <c>.sha256</c>
    /// sidecar. Files without a sidecar are counted separately — they predate Phase 3,
    /// which is fine for legacy NDJSON but warrants a metric so operators notice if
    /// brand-new files start coming through without integrity proofs.
    ///
    /// Per-pass cap (<paramref name="maxFiles"/>) keeps the walk bounded on big
    /// archives; subsequent passes pick up remaining files in oldest-first order.
    /// </summary>
    internal async Task VerifyArchiveIntegrityAsync(string archivePath, int maxFiles, CancellationToken ct)
    {
        if (!Directory.Exists(archivePath)) return;

        var files = new DirectoryInfo(archivePath)
            .EnumerateFiles("audit-*.ndjson.gz", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.LastWriteTimeUtc)
            .Take(Math.Max(1, maxFiles))
            .ToList();

        if (files.Count == 0) return;

        var verified = 0;
        var drifted = 0;
        var missing = 0;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            var sidecarPath = file.FullName + ".sha256";
            if (!File.Exists(sidecarPath))
            {
                missing++;
                SchedulerMetrics.AuditArchiveSidecarMissing.Add(1);
                continue;
            }

            string expectedHex;
            try
            {
                var sidecarText = await File.ReadAllTextAsync(sidecarPath, ct);
                // sha256sum format: "<hex>  <name>\n" — first whitespace-delimited token
                // is the hash. Tolerates leading whitespace and BOM-less UTF-8.
                expectedHex = sidecarText.TrimStart().Split(new[] { ' ', '\t', '\r', '\n' }, 2)[0]
                    .ToLowerInvariant();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit archive sidecar unreadable: {Path}", sidecarPath);
                drifted++;
                SchedulerMetrics.AuditArchiveHashDrift.Add(1);
                continue;
            }

            string actualHex;
            try
            {
                actualHex = await ComputeSha256HexAsync(file.FullName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Audit archive file unreadable during verify: {Path}", file.FullName);
                drifted++;
                SchedulerMetrics.AuditArchiveHashDrift.Add(1);
                continue;
            }

            if (string.Equals(expectedHex, actualHex, StringComparison.OrdinalIgnoreCase))
            {
                verified++;
                SchedulerMetrics.AuditArchiveVerified.Add(1);
            }
            else
            {
                drifted++;
                SchedulerMetrics.AuditArchiveHashDrift.Add(1);
                _logger.LogWarning(
                    "Audit archive HASH DRIFT: {File} expected={Expected} actual={Actual} — silent corruption or tampering",
                    file.Name, expectedHex, actualHex);
            }
        }

        if (drifted > 0 || missing > 0)
        {
            _logger.LogWarning(
                "Audit archive verify pass: {Verified} ok / {Drifted} drifted / {Missing} sidecar-missing (of {Total} inspected)",
                verified, drifted, missing, files.Count);
        }
        else
        {
            _logger.LogInformation(
                "Audit archive verify pass: {Verified} files ok",
                verified);
        }
    }
}

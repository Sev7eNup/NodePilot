using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler;
using NodePilot.Scheduler.Options;
using Xunit;
using NodePilot.TestCommons;

namespace NodePilot.Engine.Tests.Retention;

public class AuditLogRetentionServiceTests
{
    private static (NodePilotDbContext db, IServiceScopeFactory factory, SqliteConnection conn) CreateEnv(
        IInterceptor? scopeInterceptor = null)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o =>
        {
            o.UseSqlite(conn);
            if (scopeInterceptor is not null) o.AddInterceptors(scopeInterceptor);
        });
        var sp = services.BuildServiceProvider();

        // outerDb deliberately has no interceptor — test setup (Add + SaveChanges) must
        // succeed even when scope-resolved DbContexts inside PurgeOnceAsync are wired to fail.
        var outerDb = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(conn).Options);
        outerDb.Database.EnsureCreated();
        return (outerDb, sp.GetRequiredService<IServiceScopeFactory>(), conn);
    }

    /// <summary>
    /// Throws a DbUpdateException when a SaveChanges call has any AuditLogEntry in the
    /// Deleted state — i.e. when the retention service is about to commit its purge.
    /// Inserts and unrelated mutations pass through untouched.
    /// </summary>
    private sealed class FailOnAuditDeleteInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var deletingAudit = eventData.Context?.ChangeTracker.Entries()
                .Any(e => e.State == EntityState.Deleted && e.Entity is AuditLogEntry) == true;
            if (deletingAudit)
                throw new DbUpdateException("simulated DB failure during audit log delete",
                    new Exception("forced by FailOnAuditDeleteInterceptor"));
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task PurgeOnceAsync_DeletesEntriesPastCutoff_KeepsRecentOnes()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            db.AuditLog.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(), Action = "TEST",
                Timestamp = DateTime.UtcNow.AddDays(-400)
            });
            db.AuditLog.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(), Action = "TEST",
                Timestamp = DateTime.UtcNow.AddDays(-10)
            });
            await db.SaveChangesAsync();

            var svc = new AuditLogRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<AuditLogRetentionService>.Instance);

            var deleted = await svc.PurgeOnceAsync(maxAgeDays: 365, batchSize: 100, CancellationToken.None);
            deleted.Should().Be(1);
            (await db.AuditLog.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task PurgeOnceAsync_BatchesWhenManyOldEntries()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            for (int i = 0; i < 8; i++)
                db.AuditLog.Add(new AuditLogEntry
                {
                    Id = Guid.NewGuid(), Action = $"T{i}",
                    Timestamp = DateTime.UtcNow.AddDays(-400 - i)
                });
            await db.SaveChangesAsync();

            var svc = new AuditLogRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(new NodePilot.Scheduler.Options.RetentionOptions()),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<AuditLogRetentionService>.Instance);

            var deleted = await svc.PurgeOnceAsync(maxAgeDays: 365, batchSize: 3, CancellationToken.None);
            deleted.Should().Be(8);
            (await db.AuditLog.CountAsync()).Should().Be(0);
        }
        finally { conn.Dispose(); }
    }

    /// <summary>
    /// Covers audit finding F-4's happy path: archive enabled, both archive and DB delete succeed →
    /// gzipped archive file plus its SHA-256 sidecar exist on disk and the rows are gone from
    /// the DB. Pins the per-batch filename shape ("audit-{date}-{ticks}-{rand}.ndjson.gz")
    /// + sidecar pairing so a future refactor that breaks the idempotent layout or drops
    /// the integrity proof is caught.
    /// </summary>
    [Fact]
    public async Task PurgeOnceAsync_WithArchive_WritesGzipPlusSidecarAndDeletes()
    {
        var archiveDir = Path.Combine(Path.GetTempPath(), $"audit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(archiveDir);

        var (db, factory, conn) = CreateEnv();
        try
        {
            for (int i = 0; i < 3; i++)
                db.AuditLog.Add(new AuditLogEntry
                {
                    Id = Guid.NewGuid(), Action = $"OLD{i}",
                    Timestamp = DateTime.UtcNow.AddDays(-400 - i)
                });
            await db.SaveChangesAsync();

            var opts = new NodePilot.Scheduler.Options.RetentionOptions();
            opts.AuditLog.ArchivePath = archiveDir;
            var svc = new AuditLogRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(opts),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<AuditLogRetentionService>.Instance);

            var deleted = await svc.PurgeOnceAsync(maxAgeDays: 365, batchSize: 100, CancellationToken.None);

            deleted.Should().Be(3);
            (await db.AuditLog.CountAsync()).Should().Be(0);

            var gzFiles = Directory.GetFiles(archiveDir, "audit-*.ndjson.gz");
            gzFiles.Should().HaveCount(1, "one batch → one per-batch gzip (no daily aggregation)");
            var sidecars = Directory.GetFiles(archiveDir, "audit-*.ndjson.gz.sha256");
            sidecars.Should().HaveCount(1, "each gzip must have its SHA-256 sidecar for the verify pass");

            // The gzip must decode back to the three NDJSON rows we archived.
            await using var fs = File.OpenRead(gzFiles[0]);
            await using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz, Encoding.UTF8);
            var content = await sr.ReadToEndAsync();
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Should().HaveCount(3);
            lines.Should().AllSatisfy(l => l.Should().Contain("\"action\""));

            // Sidecar format pins: "<hex>  <basename>\n" — matches GNU sha256sum binary mode
            // so operators can `sha256sum -c audit-*.sha256` on Linux without re-encoding.
            var sidecarText = await File.ReadAllTextAsync(sidecars[0]);
            sidecarText.Should().MatchRegex(@"^[a-f0-9]{64}  audit-.*\.ndjson\.gz\s*$");

            // The sidecar hash must match a fresh recomputation of the gzip — round-trip
            // proof that the verify pass can validate this archive.
            var expectedHex = sidecarText.TrimStart().Split(' ', 2)[0];
            using var sha = SHA256.Create();
            await using var fs2 = File.OpenRead(gzFiles[0]);
            var actualHex = Convert.ToHexString(await sha.ComputeHashAsync(fs2)).ToLowerInvariant();
            actualHex.Should().Be(expectedHex);

            Directory.GetFiles(archiveDir, "*.tmp").Should().BeEmpty(
                "the temp file should have been atomically renamed to its final name");
        }
        finally
        {
            conn.Dispose();
            try { Directory.Delete(archiveDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Audit finding F-4's regression case: archive succeeds, DB delete throws → the archive file MUST be
    /// rolled back so the next retention pass doesn't double-archive the same rows.
    /// Without this rollback, audit archives accumulate duplicate entries on every retry,
    /// breaking compliance dedup and SIEM frequency analytics.
    /// </summary>
    [Fact]
    public async Task PurgeOnceAsync_ArchiveSucceedsButDeleteFails_RollsBackArchiveFile()
    {
        var archiveDir = Path.Combine(Path.GetTempPath(), $"audit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(archiveDir);

        var (db, factory, conn) = CreateEnv(scopeInterceptor: new FailOnAuditDeleteInterceptor());
        try
        {
            db.AuditLog.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(), Action = "OLD",
                Timestamp = DateTime.UtcNow.AddDays(-400)
            });
            await db.SaveChangesAsync();   // outerDb → no interceptor → succeeds

            var opts = new NodePilot.Scheduler.Options.RetentionOptions();
            opts.AuditLog.ArchivePath = archiveDir;
            var svc = new AuditLogRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(opts),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<AuditLogRetentionService>.Instance);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                svc.PurgeOnceAsync(maxAgeDays: 365, batchSize: 100, CancellationToken.None));

            // The row must still be there — the DB delete was rolled back by the interceptor.
            (await db.AuditLog.CountAsync()).Should().Be(1,
                "the simulated delete failure must leave the row in place");

            // Audit finding F-4's invariant: the per-batch archive file MUST have been deleted — both
            // the gzip and the .sha256 sidecar, so the next retention pass writes a fresh pair
            // instead of leaving an orphaned sidecar pointing at a vanished archive.
            Directory.GetFiles(archiveDir, "audit-*.ndjson.gz").Should().BeEmpty(
                "F-4: archive write succeeded but DB delete failed → the per-batch gzip must " +
                "be removed so the next retention pass writes a fresh copy instead of " +
                "appending duplicates to the audit archive");
            Directory.GetFiles(archiveDir, "audit-*.sha256").Should().BeEmpty(
                "F-4: the SHA-256 sidecar must also be removed — leaving it points the verify " +
                "pass at a missing file and produces a spurious drift alert");
            Directory.GetFiles(archiveDir, "*.tmp").Should().BeEmpty(
                "the .tmp file should also be cleaned up — either by the atomic rename or " +
                "by the rollback path");
        }
        finally
        {
            conn.Dispose();
            try { Directory.Delete(archiveDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Verify pass happy path: a freshly-written archive verifies cleanly against its
    /// sidecar (no log warning, no drift metric). This is the V1 contract — the same
    /// hash the writer computed must round-trip through the verifier.
    /// </summary>
    [Fact]
    public async Task VerifyArchiveIntegrityAsync_FreshArchive_VerifiesCleanly()
    {
        var archiveDir = Path.Combine(Path.GetTempPath(), $"audit-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(archiveDir);

        var (db, factory, conn) = CreateEnv();
        try
        {
            db.AuditLog.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(), Action = "OLD",
                Timestamp = DateTime.UtcNow.AddDays(-400)
            });
            await db.SaveChangesAsync();

            var opts = new NodePilot.Scheduler.Options.RetentionOptions();
            opts.AuditLog.ArchivePath = archiveDir;
            var svc = new AuditLogRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(opts),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<AuditLogRetentionService>.Instance);

            await svc.PurgeOnceAsync(maxAgeDays: 365, batchSize: 100, CancellationToken.None);
            var act = async () => await svc.VerifyArchiveIntegrityAsync(archiveDir, maxFiles: 100, CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
        finally
        {
            conn.Dispose();
            try { Directory.Delete(archiveDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Drift detection: the verify pass must notice when an archive file's bytes change
    /// after the sidecar was written. The whole point of the SHA-256 sidecar is to detect
    /// silent corruption (bit-rot, partial overwrite, accidental edit). We simulate that
    /// by overwriting one byte in the gzip post-write — the verifier must NOT throw, but
    /// MUST log a warning that operators can alert on.
    /// </summary>
    [Fact]
    public async Task VerifyArchiveIntegrityAsync_TamperedArchive_LogsWarningWithoutThrowing()
    {
        var archiveDir = Path.Combine(Path.GetTempPath(), $"audit-tamper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(archiveDir);

        var (db, factory, conn) = CreateEnv();
        try
        {
            db.AuditLog.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(), Action = "OLD",
                Timestamp = DateTime.UtcNow.AddDays(-400)
            });
            await db.SaveChangesAsync();

            var opts = new NodePilot.Scheduler.Options.RetentionOptions();
            opts.AuditLog.ArchivePath = archiveDir;
            var capturingLogger = new ListLogger<AuditLogRetentionService>();
            var svc = new AuditLogRetentionService(factory,
                new StaticOptionsMonitor<NodePilot.Scheduler.Options.RetentionOptions>(opts),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                capturingLogger);

            await svc.PurgeOnceAsync(maxAgeDays: 365, batchSize: 100, CancellationToken.None);
            var gzFile = Directory.GetFiles(archiveDir, "audit-*.ndjson.gz").Single();

            // Tamper: flip one byte deep enough that gzip CRC may also fail, but the SHA
            // mismatch must be the path that triggers the warning — that's what proves the
            // sidecar is the authoritative integrity check, not gzip's own CRC.
            var bytes = await File.ReadAllBytesAsync(gzFile);
            bytes[bytes.Length / 2] ^= 0xFF;
            await File.WriteAllBytesAsync(gzFile, bytes);

            await svc.VerifyArchiveIntegrityAsync(archiveDir, maxFiles: 100, CancellationToken.None);

            capturingLogger.Entries.Should().Contain(e =>
                e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                && e.Message.Contains("HASH DRIFT"));
        }
        finally
        {
            conn.Dispose();
            try { Directory.Delete(archiveDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Hot-reload + audit-compliance invariant: RunIterationAsync reads
    /// IOptionsMonitor&lt;RetentionOptions&gt;.CurrentValue per pass, and a changed ArchivePath is
    /// re-probed live. Start with a broken archive path (child of a file → CreateDirectory throws)
    /// — per the AuditLog compliance rule, rows MUST stay in-DB (no archive = no delete). Then
    /// mutate ArchivePath to a valid directory via config reload and assert the next iteration
    /// re-probes, archives, and deletes. Proves the re-probe invalidation preserves the
    /// "broken path keeps rows" asymmetry while still flipping live on a real fix.
    /// </summary>
    [Fact]
    public async Task RunIterationAsync_ArchivePathChange_ReprobesLive_PreservesComplianceInvariant()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), $"audit-block-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFile, "x");
        var validDir = Path.Combine(Path.GetTempPath(), $"audit-archive-{Guid.NewGuid():N}");
        var (db, factory, conn) = CreateEnv();
        try
        {
            db.AuditLog.Add(new AuditLogEntry
            {
                Id = Guid.NewGuid(), Action = "OLD",
                Timestamp = DateTime.UtcNow.AddDays(-400)
            });
            await db.SaveChangesAsync();

            var monitor = new MutableOptionsMonitor<RetentionOptions>(new RetentionOptions
            {
                AuditLog = new AuditLogRetentionOptions
                {
                    Enabled = true, MaxAgeDays = 365, ArchivePath = Path.Combine(blockingFile, "sub"),
                    VerifyIntervalMinutes = 0
                }
            });
            var svc = new AuditLogRetentionService(factory, monitor,
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<AuditLogRetentionService>.Instance);

            // First pass: broken archive path → ArchiveAsync fails → PurgeOnceAsync breaks out
            // WITHOUT deleting. Compliance: audit rows stay in-DB until the archive works.
            await svc.RunIterationAsync(CancellationToken.None);
            (await db.AuditLog.CountAsync()).Should().Be(1, "a broken archive path must retain audit rows in-DB");
            Directory.Exists(validDir).Should().BeFalse();

            // Operator fixes Retention:AuditLog:ArchivePath in the Settings UI → config reload.
            monitor.Set(new RetentionOptions
            {
                AuditLog = new AuditLogRetentionOptions
                {
                    Enabled = true, MaxAgeDays = 365, ArchivePath = validDir, VerifyIntervalMinutes = 0
                }
            });

            // Same service instance: the path change invalidates the cached "broken" verdict →
            // re-probe succeeds → archive written + rows deleted.
            await svc.RunIterationAsync(CancellationToken.None);
            (await db.AuditLog.CountAsync()).Should().Be(0);
            Directory.GetFiles(validDir, "audit-*.ndjson.gz").Should().HaveCount(1);
        }
        finally
        {
            conn.Dispose();
            try { File.Delete(blockingFile); } catch { }
            try { Directory.Delete(validDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Minimal capturing logger — pinned in this test file rather than added to TestCommons
    /// because the verify-pass log-shape is specific to audit archive drift and would
    /// invite generic re-use that obscures the test intent. Two consumers max, keep it local.
    /// </summary>
    private sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

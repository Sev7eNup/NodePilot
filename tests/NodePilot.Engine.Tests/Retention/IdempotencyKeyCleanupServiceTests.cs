using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Scheduler;
using Xunit;

namespace NodePilot.Engine.Tests.Retention;

/// <summary>
/// Coverage for the lone interesting path in IdempotencyKeyCleanupService — the
/// "delete rows whose ExpiresAt is in the past" sweep, driven through the internal
/// <c>PurgeOnceAsync</c> seam (no 30-second warm-up / lifecycle teardown needed).
/// </summary>
public class IdempotencyKeyCleanupServiceTests
{
    [Fact]
    public async Task ServiceCanBeConstructedAndStarted_ThenStoppedCleanly()
    {
        // Smoke test: the BackgroundService loop entry-point delays 30 s before the
        // first sweep — we only need to start it, immediately request stop, and
        // confirm no exception escapes (validates DI wiring + cancellation handling).
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(conn));
            var sp = services.BuildServiceProvider();

            using (var seedDb = new NodePilotDbContext(
                new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(conn).Options))
            {
                seedDb.Database.EnsureCreated();
            }

            var svc = new IdempotencyKeyCleanupService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<IdempotencyKeyCleanupService>.Instance);

            using var cts = new CancellationTokenSource();
            var startTask = svc.StartAsync(cts.Token);
            cts.Cancel();
            await startTask;
            await svc.StopAsync(CancellationToken.None);
        }
        finally
        {
            conn.Dispose();
        }
    }

    [Fact]
    public async Task PurgeOnce_RemovesExpiredKeysOnly()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(conn));
            var sp = services.BuildServiceProvider();

            using var db = new NodePilotDbContext(
                new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(conn).Options);
            db.Database.EnsureCreated();

            var now = DateTime.UtcNow;
            db.IdempotencyKeys.AddRange(
                new IdempotencyKey
                {
                    Id = Guid.NewGuid(), Key = "expired-1", WorkflowId = Guid.NewGuid(),
                    ExecutionId = Guid.NewGuid(),
                    FirstSeenAt = now.AddHours(-30), ExpiresAt = now.AddHours(-6),
                },
                new IdempotencyKey
                {
                    Id = Guid.NewGuid(), Key = "expired-2", WorkflowId = Guid.NewGuid(),
                    ExecutionId = Guid.NewGuid(),
                    FirstSeenAt = now.AddHours(-25), ExpiresAt = now.AddSeconds(-1),
                },
                new IdempotencyKey
                {
                    Id = Guid.NewGuid(), Key = "active-1", WorkflowId = Guid.NewGuid(),
                    ExecutionId = Guid.NewGuid(),
                    FirstSeenAt = now, ExpiresAt = now.AddHours(2),
                },
                new IdempotencyKey
                {
                    Id = Guid.NewGuid(), Key = "active-2", WorkflowId = Guid.NewGuid(),
                    ExecutionId = Guid.NewGuid(),
                    FirstSeenAt = now, ExpiresAt = now.AddHours(24),
                });
            await db.SaveChangesAsync();

            var svc = new IdempotencyKeyCleanupService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
                NullLogger<IdempotencyKeyCleanupService>.Instance);

            var deleted = await svc.PurgeOnceAsync(CancellationToken.None);

            deleted.Should().Be(2);
            var remaining = await db.IdempotencyKeys.Select(k => k.Key).ToListAsync();
            remaining.Should().BeEquivalentTo(new[] { "active-1", "active-2" });
        }
        finally
        {
            conn.Dispose();
        }
    }
}

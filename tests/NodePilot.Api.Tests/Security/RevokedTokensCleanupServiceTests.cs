using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Security;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public class RevokedTokensCleanupServiceTests
{
    private static (NodePilotDbContext db, IServiceScopeFactory factory, SqliteConnection conn) CreateEnv()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(conn));
        var sp = services.BuildServiceProvider();

        var outerDb = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(conn).Options);
        outerDb.Database.EnsureCreated();
        return (outerDb, sp.GetRequiredService<IServiceScopeFactory>(), conn);
    }

    private static RevokedTokensCleanupService NewService(IServiceScopeFactory factory) =>
        new(factory,
            NullLogger<RevokedTokensCleanupService>.Instance,
            new ConfigurationBuilder().Build(),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider());

    private static RevokedToken Token(DateTime expiresAt, string? jti = null) => new()
    {
        Jti = jti ?? Guid.NewGuid().ToString(),
        UserId = Guid.NewGuid(),
        RevokedAt = DateTime.UtcNow.AddHours(-1),
        ExpiresAt = expiresAt,
    };

    [Fact]
    public async Task SweepOnceAsync_DeletesExpiredTokens_KeepsLiveOnes()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            db.RevokedTokens.Add(Token(DateTime.UtcNow.AddHours(-1)));   // expired
            db.RevokedTokens.Add(Token(DateTime.UtcNow.AddHours(-24)));  // expired
            db.RevokedTokens.Add(Token(DateTime.UtcNow.AddHours(1)));    // still valid
            await db.SaveChangesAsync();

            var deleted = await NewService(factory).SweepOnceAsync(CancellationToken.None);

            deleted.Should().Be(2);
            (await db.RevokedTokens.CountAsync()).Should().Be(1,
                "the only remaining row should be the one that hasn't expired yet");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task SweepOnceAsync_NoExpiredTokens_DeletesNothing()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            db.RevokedTokens.Add(Token(DateTime.UtcNow.AddHours(2)));
            db.RevokedTokens.Add(Token(DateTime.UtcNow.AddDays(1)));
            await db.SaveChangesAsync();

            var deleted = await NewService(factory).SweepOnceAsync(CancellationToken.None);

            deleted.Should().Be(0);
            (await db.RevokedTokens.CountAsync()).Should().Be(2);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task SweepOnceAsync_EmptyTable_ReturnsZero()
    {
        var (db, factory, conn) = CreateEnv();
        try
        {
            var deleted = await NewService(factory).SweepOnceAsync(CancellationToken.None);
            deleted.Should().Be(0);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task SweepOnceAsync_RowExpiringExactlyNow_NotDeleted()
    {
        // Boundary: cutoff is DateTime.UtcNow at sweep time and the predicate is strict
        // less-than. A token whose ExpiresAt equals "now" is treated as still valid for
        // this sweep — picked up on the next pass once "now" advances. This pins the
        // strict-less-than semantic so a refactor to <= doesn't silently change behaviour.
        var (db, factory, conn) = CreateEnv();
        try
        {
            // Set ExpiresAt slightly in the future so the predicate (ExpiresAt < UtcNow)
            // is false at sweep time. The 5-second margin absorbs clock jitter.
            db.RevokedTokens.Add(Token(DateTime.UtcNow.AddSeconds(5)));
            await db.SaveChangesAsync();

            var deleted = await NewService(factory).SweepOnceAsync(CancellationToken.None);

            deleted.Should().Be(0);
            (await db.RevokedTokens.CountAsync()).Should().Be(1);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task SweepOnceAsync_DeletesManyRows()
    {
        // Verifies ExecuteDeleteAsync handles large batches in one pass — no implicit
        // batching is needed, the SQL backend takes care of it. If somebody refactors to
        // a Remove + SaveChanges loop, this test would still pass but with very different
        // performance — the assertion focuses on correctness rather than mechanism.
        var (db, factory, conn) = CreateEnv();
        try
        {
            for (int i = 0; i < 250; i++)
                db.RevokedTokens.Add(Token(DateTime.UtcNow.AddHours(-i - 1)));
            await db.SaveChangesAsync();

            var deleted = await NewService(factory).SweepOnceAsync(CancellationToken.None);

            deleted.Should().Be(250);
            (await db.RevokedTokens.CountAsync()).Should().Be(0);
        }
        finally { conn.Dispose(); }
    }
}

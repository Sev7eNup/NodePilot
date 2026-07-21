using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Execution;
using Npgsql;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

public class WorkflowDbWriteMetricsTests
{
    [Fact]
    public async Task SaveChangesMeasuredAsync_PostgresUniqueViolation_AbsorbsAndResetsAddedState()
    {
        var pgException = new PostgresException(
            "duplicate key value violates unique constraint",
            "ERROR", "ERROR", "23505");
        var (connection, ctx) = BuildThrowingContext(new DbUpdateException("retry replay", pgException));
        await using var _ = connection;
        await using var __ = ctx;

        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "test" };
        ctx.Workflows.Add(workflow);
        ctx.Entry(workflow).State.Should().Be(EntityState.Added);

        var rows = await ctx.SaveChangesMeasuredAsync("step.terminal", CancellationToken.None);

        rows.Should().Be(0);
        ctx.Entry(workflow).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task SaveChangesMeasuredAsync_PostgresNonUniqueViolation_Propagates()
    {
        // 23503 = foreign_key_violation — not idempotency-safe, must surface.
        var pgException = new PostgresException(
            "insert or update violates foreign key constraint",
            "ERROR", "ERROR", "23503");
        var (connection, ctx) = BuildThrowingContext(new DbUpdateException("fk fail", pgException));
        await using var _ = connection;
        await using var __ = ctx;

        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "test" };
        ctx.Workflows.Add(workflow);

        var act = async () => await ctx.SaveChangesMeasuredAsync("step.terminal", CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    private static (SqliteConnection conn, ThrowingDbContext ctx) BuildThrowingContext(Exception toThrow)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection)
            .Options;
        var ctx = new ThrowingDbContext(options, toThrow);
        ctx.Database.EnsureCreated();
        return (connection, ctx);
    }

    private sealed class ThrowingDbContext : NodePilotDbContext
    {
        private readonly Exception _toThrow;

        public ThrowingDbContext(DbContextOptions<NodePilotDbContext> options, Exception toThrow)
            : base(options) => _toThrow = toThrow;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw _toThrow;
    }
}

using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Core.Telemetry;
using NodePilot.Data;
using NodePilot.Engine;
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

    [Fact]
    public async Task ExecuteMeasuredAsync_Success_RecordsSuccessStatusAndRowCount()
    {
        var operation = UniqueOperation();

        var measurements = await MeasureAsync(operation, async () =>
        {
            var rows = await WorkflowDbWriteMetrics.ExecuteMeasuredAsync(operation, () => Task.FromResult(3));
            rows.Should().Be(3);
        });

        measurements.Should().Contain(m => m.Instrument == "nodepilot.db.save_changes" && m.Status == "success");
        measurements.Should().Contain(m => m.Instrument == "nodepilot.db.save_changes.duration" && m.Status == "success");
        measurements.Should().ContainSingle(m => m.Instrument == "nodepilot.db.save_changes.rows")
            .Which.Value.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteMeasuredAsync_Cancellation_RecordsCancelledStatus()
    {
        var operation = UniqueOperation();

        var measurements = await MeasureAsync(operation, async () =>
        {
            var act = async () => await WorkflowDbWriteMetrics.ExecuteMeasuredAsync(
                operation, () => Task.FromException<int>(new OperationCanceledException()));
            await act.Should().ThrowAsync<OperationCanceledException>();
        });

        measurements.Should().OnlyContain(m => m.Status == "cancelled");
        measurements.Should().Contain(m => m.Instrument == "nodepilot.db.save_changes");
        measurements.Should().Contain(m => m.Instrument == "nodepilot.db.save_changes.duration");
        measurements.Should().NotContain(m => m.Instrument == "nodepilot.db.save_changes.rows");
    }

    [Fact]
    public async Task ExecuteMeasuredAsync_Failure_RecordsFailureStatus()
    {
        var operation = UniqueOperation();

        var measurements = await MeasureAsync(operation, async () =>
        {
            var act = async () => await WorkflowDbWriteMetrics.ExecuteMeasuredAsync(
                operation, () => Task.FromException<int>(new InvalidOperationException("boom")));
            await act.Should().ThrowAsync<InvalidOperationException>();
        });

        measurements.Should().OnlyContain(m => m.Status == "failure");
        measurements.Should().Contain(m => m.Instrument == "nodepilot.db.save_changes");
        measurements.Should().Contain(m => m.Instrument == "nodepilot.db.save_changes.duration");
        measurements.Should().NotContain(m => m.Instrument == "nodepilot.db.save_changes.rows");
    }

    [Fact]
    public async Task SaveChangesMeasuredAsync_AbsorbedUniqueViolation_RecordsSuccessWithZeroRows()
    {
        var operation = UniqueOperation();
        var pgException = new PostgresException(
            "duplicate key value violates unique constraint",
            "ERROR", "ERROR", "23505");
        var (connection, ctx) = BuildThrowingContext(new DbUpdateException("retry replay", pgException));
        await using var _ = connection;
        await using var __ = ctx;
        ctx.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "test" });

        var measurements = await MeasureAsync(operation, async () =>
        {
            var rows = await ctx.SaveChangesMeasuredAsync(operation, CancellationToken.None);
            rows.Should().Be(0);
        });

        measurements.Should().OnlyContain(m => m.Status == null || m.Status == "success");
        measurements.Should().ContainSingle(m => m.Instrument == "nodepilot.db.save_changes.rows")
            .Which.Value.Should().Be(0);
    }

    [Fact]
    public async Task SaveChangesMeasuredAsync_Cancellation_RecordsCancelledStatus()
    {
        var operation = UniqueOperation();
        var (connection, ctx) = BuildThrowingContext(new OperationCanceledException());
        await using var _ = connection;
        await using var __ = ctx;

        var measurements = await MeasureAsync(operation, async () =>
        {
            var act = async () => await ctx.SaveChangesMeasuredAsync(operation, CancellationToken.None);
            await act.Should().ThrowAsync<OperationCanceledException>();
        });

        measurements.Should().OnlyContain(m => m.Status == "cancelled");
        measurements.Should().Contain(m => m.Instrument == "nodepilot.db.save_changes");
    }

    private static string UniqueOperation() => $"test.{Guid.NewGuid():N}";

    /// <summary>
    /// Runs <paramref name="action"/> with a live <see cref="MeterListener"/> attached to the
    /// DB-write instruments and returns everything that was emitted for <paramref
    /// name="operation"/>.
    /// The operation tag is unique per test, so measurements from tests running in parallel are
    /// filtered out.
    /// </summary>
    private static async Task<IReadOnlyList<CapturedMeasurement>> MeasureAsync(string operation, Func<Task> action)
    {
        _ = EngineMetrics.DbSaveChanges; // force instrument creation before the listener starts
        var captured = new List<CapturedMeasurement>();
        var gate = new object();

        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? taggedOperation = null;
            string? status = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "operation")
                    taggedOperation = tag.Value as string;
                else if (tag.Key == "status")
                    status = tag.Value as string;
            }

            if (taggedOperation != operation)
                return;

            lock (gate)
                captured.Add(new CapturedMeasurement(instrument.Name, status, value));
        }

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == TelemetryConstants.Meters.Engine
                    && instrument.Name.StartsWith("nodepilot.db.save_changes", StringComparison.Ordinal))
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();

        await action();

        lock (gate)
            return captured.ToList();
    }

    private sealed record CapturedMeasurement(string Instrument, string? Status, double Value);

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

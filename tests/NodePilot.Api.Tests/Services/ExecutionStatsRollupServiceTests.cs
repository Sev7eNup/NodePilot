using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Services;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Api.Tests.Services;

/// <summary>
/// Covers <see cref="ExecutionStatsRollupService"/> and <see cref="DashboardRollupReader"/>.
///
/// <para>
/// The decisive property is not speed but agreement: whatever the buckets report must match what
/// the live computation over raw rows reports. A faster dashboard showing different numbers would
/// be a regression, not a fix. Several tests therefore compare both paths directly.
/// </para>
/// </summary>
public sealed class ExecutionStatsRollupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly NodePilotDbContext _db;
    private readonly OutputRedactor _redactor;

    private static readonly DateTime Hour = new(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc);

    public ExecutionStatsRollupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(_connection));
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        _redactor = new OutputRedactor(config);
        services.AddSingleton(_redactor);
        _provider = services.BuildServiceProvider();

        _db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    private ExecutionStatsRollupService NewService()
    {
        var availability = new Mock<IDatabaseAvailability>();
        availability.Setup(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return new ExecutionStatsRollupService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            availability.Object,
            new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
            NullLogger<ExecutionStatsRollupService>.Instance);
    }

    private Guid AddWorkflow(string name = "W")
    {
        var id = Guid.NewGuid();
        _db.Workflows.Add(new Workflow
        {
            Id = id, Name = name, DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow,
            FolderId = SharedWorkflowFolder.RootFolderId,
        });
        return id;
    }

    private Guid AddExecution(Guid workflowId, ExecutionStatus status, DateTime startedAt,
        DateTime? completedAt = null, string? error = null)
    {
        var id = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = id, WorkflowId = workflowId, Status = status,
            StartedAt = startedAt, CompletedAt = completedAt, ErrorMessage = error,
        });
        return id;
    }

    private async Task RollUpAsync(DateTime hour)
        => await NewService().RollUpHourAsync(_db, _redactor, hour, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RollUpHour_CountsEachStatus()
    {
        var wf = AddWorkflow();
        AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddMinutes(5), Hour.AddMinutes(6));
        AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddMinutes(7), Hour.AddMinutes(9));
        AddExecution(wf, ExecutionStatus.Failed, Hour.AddMinutes(10), Hour.AddMinutes(11));
        AddExecution(wf, ExecutionStatus.Cancelled, Hour.AddMinutes(12), Hour.AddMinutes(13));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(Hour);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await _db.ExecutionHourlyStats.SingleAsync(TestContext.Current.CancellationToken);
        row.TotalCount.Should().Be(4);
        row.SucceededCount.Should().Be(2);
        row.FailedCount.Should().Be(1);
        row.CancelledCount.Should().Be(1);
        row.IsFinal.Should().BeTrue("every run in the hour reached a terminal state");
    }

    [Fact]
    public async Task RollUpHour_DurationHistogram_HoldsOnlyFinishedSuccessesAndFailures()
    {
        // The same runs the raw duration trend reads; cancelled, open and invalid runs stay out.
        var wf = AddWorkflow();
        AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddMinutes(1), Hour.AddMinutes(1).AddSeconds(10));
        AddExecution(wf, ExecutionStatus.Failed, Hour.AddMinutes(2), Hour.AddMinutes(2).AddSeconds(30), "boom");
        AddExecution(wf, ExecutionStatus.Cancelled, Hour.AddMinutes(3), Hour.AddMinutes(3).AddSeconds(50));
        AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddMinutes(4), Hour.AddMinutes(3));
        AddExecution(wf, ExecutionStatus.Running, Hour.AddMinutes(5));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(Hour);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await _db.ExecutionHourlyStats.SingleAsync(TestContext.Current.CancellationToken);
        row.DurationHistogram.Should().Be(DurationHistogram.Encode([10_000, 30_000]));
    }

    [Fact]
    public async Task RollUpHour_WithAnOpenRun_StaysProvisional()
    {
        // The correctness trap: a run started at 10:55 may only finish at 11:30, so the 10:00 hour
        // is not settled just because the clock moved on.
        var wf = AddWorkflow();
        AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddMinutes(5), Hour.AddMinutes(6));
        AddExecution(wf, ExecutionStatus.Running, Hour.AddMinutes(55));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(Hour);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await _db.ExecutionHourlyStats.SingleAsync(TestContext.Current.CancellationToken);
        row.IsFinal.Should().BeFalse();
        row.RunningCount.Should().Be(1);
    }

    [Fact]
    public async Task RollUpHour_RerunAfterRunFinished_CorrectsTheBucket()
    {
        var wf = AddWorkflow();
        var openId = AddExecution(wf, ExecutionStatus.Running, Hour.AddMinutes(55));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RollUpAsync(Hour);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The run finishes in the NEXT hour — its bucket must follow.
        var open = await _db.WorkflowExecutions.FirstAsync(e => e.Id == openId, TestContext.Current.CancellationToken);
        open.Status = ExecutionStatus.Failed;
        open.CompletedAt = Hour.AddMinutes(95);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(Hour);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await _db.ExecutionHourlyStats.SingleAsync(TestContext.Current.CancellationToken);
        row.FailedCount.Should().Be(1);
        row.RunningCount.Should().Be(0);
        row.IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task RollUpHour_CountsRetriedRuns()
    {
        var wf = AddWorkflow();
        var retried = AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddMinutes(5), Hour.AddMinutes(6));
        var plain = AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddMinutes(7), Hour.AddMinutes(8));
        _db.StepExecutions.AddRange(
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = retried, StepId = "a", AttemptCount = 3, Status = ExecutionStatus.Succeeded, StartedAt = Hour },
            new StepExecution { Id = Guid.NewGuid(), WorkflowExecutionId = plain, StepId = "a", AttemptCount = 1, Status = ExecutionStatus.Succeeded, StartedAt = Hour });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(Hour);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var row = await _db.ExecutionHourlyStats.SingleAsync(TestContext.Current.CancellationToken);
        row.RetriedCount.Should().Be(1);
        row.FinishedCount.Should().Be(2);
    }

    [Fact]
    public async Task RollUpHour_GroupsFailureCausesLikeTheLivePath()
    {
        var wf = AddWorkflow();
        AddExecution(wf, ExecutionStatus.Failed, Hour.AddMinutes(1), Hour.AddMinutes(2), "disk full on host A");
        AddExecution(wf, ExecutionStatus.Failed, Hour.AddMinutes(3), Hour.AddMinutes(4), "disk full on host A");
        AddExecution(wf, ExecutionStatus.Failed, Hour.AddMinutes(5), Hour.AddMinutes(6), "network unreachable");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(Hour);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var buckets = await _db.FailureCauseHourlyStats.ToListAsync(TestContext.Current.CancellationToken);
        buckets.Should().HaveCount(2);
        buckets.Single(b => b.Message == "disk full on host A").Count.Should().Be(2);
        buckets.Single(b => b.Message == "network unreachable").Count.Should().Be(1);
    }

    [Fact]
    public async Task RollUpHour_NormalisesVolatilePartsBeforeGrouping()
    {
        // Two failures differing only by a GUID are one cause, exactly as on the live path.
        var wf = AddWorkflow();
        AddExecution(wf, ExecutionStatus.Failed, Hour.AddMinutes(1), Hour.AddMinutes(2),
            $"job {Guid.NewGuid()} failed");
        AddExecution(wf, ExecutionStatus.Failed, Hour.AddMinutes(3), Hour.AddMinutes(4),
            $"job {Guid.NewGuid()} failed");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(Hour);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var bucket = await _db.FailureCauseHourlyStats.SingleAsync(TestContext.Current.CancellationToken);
        bucket.Count.Should().Be(2);
    }

    [Fact]
    public async Task RollUpRange_SplitsRowsIntoTheirOwnHours()
    {
        // The backfill processes whole days in one go; hours must still come out separated, and
        // each one must judge its own finality.
        var wf = AddWorkflow();
        AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddMinutes(5), Hour.AddMinutes(6));
        AddExecution(wf, ExecutionStatus.Failed, Hour.AddHours(1).AddMinutes(5), Hour.AddHours(1).AddMinutes(6));
        AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddHours(2).AddMinutes(5), Hour.AddHours(2).AddMinutes(6));
        // Still open, in the second hour only.
        AddExecution(wf, ExecutionStatus.Running, Hour.AddHours(1).AddMinutes(50));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await NewService().RollUpRangeAsync(
            _db, _redactor, Hour, Hour.AddHours(3), TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var rows = await _db.ExecutionHourlyStats.OrderBy(s => s.HourUtc)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().HaveCount(3);
        rows[0].SucceededCount.Should().Be(1);
        rows[0].IsFinal.Should().BeTrue();
        rows[1].FailedCount.Should().Be(1);
        rows[1].RunningCount.Should().Be(1);
        rows[1].IsFinal.Should().BeFalse("only this hour still has an open run");
        rows[2].SucceededCount.Should().Be(1);
        rows[2].IsFinal.Should().BeTrue();
    }

    [Fact]
    public async Task RollUpRange_MatchesHourByHourProcessing()
    {
        // Range and per-hour processing must be interchangeable — the backfill uses one, the
        // catch-up pass the other.
        var wf = AddWorkflow();
        for (var h = 0; h < 5; h++)
        {
            AddExecution(wf, ExecutionStatus.Succeeded, Hour.AddHours(h).AddMinutes(5), Hour.AddHours(h).AddMinutes(6));
            AddExecution(wf, ExecutionStatus.Failed, Hour.AddHours(h).AddMinutes(7), Hour.AddHours(h).AddMinutes(8), "boom");
        }
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = NewService();
        for (var h = 0; h < 5; h++)
            await svc.RollUpHourAsync(_db, _redactor, Hour.AddHours(h), TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var hourly = await _db.ExecutionHourlyStats.AsNoTracking().OrderBy(s => s.HourUtc)
            .Select(s => new { s.HourUtc, s.TotalCount, s.SucceededCount, s.FailedCount })
            .ToListAsync(TestContext.Current.CancellationToken);
        var hourlyCauses = await _db.FailureCauseHourlyStats.AsNoTracking()
            .OrderBy(s => s.HourUtc).Select(s => new { s.HourUtc, s.Count }).ToListAsync(TestContext.Current.CancellationToken);

        await svc.RollUpRangeAsync(_db, _redactor, Hour, Hour.AddHours(5), TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var ranged = await _db.ExecutionHourlyStats.AsNoTracking().OrderBy(s => s.HourUtc)
            .Select(s => new { s.HourUtc, s.TotalCount, s.SucceededCount, s.FailedCount })
            .ToListAsync(TestContext.Current.CancellationToken);
        var rangedCauses = await _db.FailureCauseHourlyStats.AsNoTracking()
            .OrderBy(s => s.HourUtc).Select(s => new { s.HourUtc, s.Count }).ToListAsync(TestContext.Current.CancellationToken);

        ranged.Should().BeEquivalentTo(hourly);
        rangedCauses.Should().BeEquivalentTo(hourlyCauses);
    }

    [Fact]
    public async Task Reader_WithoutCoverage_ReturnsNullSoTheCallerComputesLive()
    {
        var wf = AddWorkflow();
        AddExecution(wf, ExecutionStatus.Succeeded, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // No rollup state row at all: nothing may be served from buckets.
        var reader = new DashboardRollupReader(_db);
        (await reader.ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, 24, TestContext.Current.CancellationToken))
            .Should().BeNull();
        (await reader.ReadFailureCausesAsync(AccessibleFolderSet.Unrestricted, 24, TestContext.Current.CancellationToken))
            .Should().BeNull();
        (await reader.ReadDurationBucketsAsync(AccessibleFolderSet.Unrestricted, 24, null, DateTime.UtcNow, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task Reader_StaleState_ReturnsNullSoTheCallerComputesLive()
    {
        // A disabled rollup or a pass that keeps failing leaves coverage that looks complete while
        // every hour since the last pass is missing.
        var wf = AddWorkflow();
        var hour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow).AddHours(-1);
        AddExecution(wf, ExecutionStatus.Succeeded, hour.AddMinutes(5), hour.AddMinutes(6));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RollUpAsync(hour);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = hour.AddHours(-48),
            CoverageEndUtc = hour,
            BackfillComplete = true,
            UpdatedAt = DateTime.UtcNow - ExecutionStatsRollupService.StaleAfter - TimeSpan.FromMinutes(1),
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reader = new DashboardRollupReader(_db);
        (await reader.ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, 24, TestContext.Current.CancellationToken))
            .Should().BeNull();
        (await reader.ReadFailureCausesAsync(AccessibleFolderSet.Unrestricted, 24, TestContext.Current.CancellationToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task RunPass_DeletesBucketsOlderThanTheRetention()
    {
        var wf = AddWorkflow();
        var currentHour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow);
        var cutoff = currentHour - ExecutionStatsRollupService.BucketRetention;
        foreach (var hour in new[] { cutoff.AddHours(-1), cutoff.AddHours(5) })
        {
            _db.ExecutionHourlyStats.Add(new ExecutionHourlyStat { HourUtc = hour, WorkflowId = wf, TotalCount = 1, IsFinal = true });
            _db.FailureCauseHourlyStats.Add(new FailureCauseHourlyStat
            {
                HourUtc = hour, WorkflowId = wf, MessageHash = ExecutionStatsRollupService.HashMessage("boom"),
                Message = "boom", Count = 1, LatestExecutionId = Guid.NewGuid(), LatestStartedAt = hour, IsFinal = true,
            });
        }
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = cutoff.AddHours(-100),
            CoverageEndUtc = currentHour,
            BackfillComplete = true,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await NewService().RunPassAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        (await _db.ExecutionHourlyStats.AsNoTracking().Select(s => s.HourUtc).ToListAsync(ct))
            .Should().Equal(cutoff.AddHours(5));
        (await _db.FailureCauseHourlyStats.AsNoTracking().Select(s => s.HourUtc).ToListAsync(ct))
            .Should().Equal(cutoff.AddHours(5));
        (await _db.ExecutionStatsRollupStates.AsNoTracking().SingleAsync(ct)).CoverageStartUtc
            .Should().Be(cutoff, "the state must not claim hours whose buckets were deleted");
    }

    [Fact]
    public async Task RunPass_BackfillStopsAtTheRetention()
    {
        var wf = AddWorkflow();
        var currentHour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow);
        var retentionStart = currentHour - ExecutionStatsRollupService.BucketRetention;
        AddExecution(wf, ExecutionStatus.Succeeded, retentionStart.AddHours(-200), retentionStart.AddHours(-200).AddMinutes(1));
        AddExecution(wf, ExecutionStatus.Succeeded, currentHour.AddHours(-2), currentHour.AddHours(-2).AddMinutes(1));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await NewService().RunPassAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var state = await _db.ExecutionStatsRollupStates.AsNoTracking().SingleAsync(ct);
        state.BackfillComplete.Should().BeTrue();
        state.CoverageStartUtc.Should().Be(retentionStart);
        (await _db.ExecutionHourlyStats.AsNoTracking().Select(s => s.HourUtc).ToListAsync(ct))
            .Should().Equal(currentHour.AddHours(-2));
        (await new DashboardRollupReader(_db).ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, 720, ct))
            .Should().NotBeNull("the longest dashboard window is covered");
    }

    [Fact]
    public async Task RunPass_HistoryYoungerThanTheLongestWindow_CoversIt()
    {
        // The backfill stops at the oldest execution. Nothing older exists, so windows that reach
        // further back must still be served from the buckets instead of the raw rows.
        var wf = AddWorkflow();
        var currentHour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow);
        AddExecution(wf, ExecutionStatus.Succeeded, currentHour.AddDays(-3), currentHour.AddDays(-3).AddMinutes(1));
        AddExecution(wf, ExecutionStatus.Failed, currentHour.AddHours(-2), currentHour.AddHours(-2).AddMinutes(1), "boom");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await NewService().RunPassAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var state = await _db.ExecutionStatsRollupStates.AsNoTracking().SingleAsync(ct);
        state.BackfillComplete.Should().BeTrue();
        state.CoverageStartUtc.Should().Be(currentHour.AddDays(-3));
        var reader = new DashboardRollupReader(_db);
        foreach (var windowHours in new[] { 168, 720 })
        {
            var aggregates = await reader.ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, windowHours, ct);
            aggregates.Should().NotBeNull($"a finished backfill covers the {windowHours} h window");
            aggregates!.Slots.Sum(s => s.Total).Should().Be(2);
            (await reader.ReadFailureCausesAsync(AccessibleFolderSet.Unrestricted, windowHours, ct))!
                .TotalFailed.Should().Be(1);
        }
    }

    [Fact]
    public async Task RunPass_HistoryOnlyInTheCurrentHour_RecordsWhereCoverageStarts()
    {
        // A fresh install whose first runs fall into the hour of the first pass: the backfill has no
        // chunk to roll up, but must still record its start, or no window is ever served from buckets.
        var wf = AddWorkflow();
        var currentHour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow);
        AddExecution(wf, ExecutionStatus.Succeeded, currentHour, currentHour.AddSeconds(1));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await NewService().RunPassAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var state = await _db.ExecutionStatsRollupStates.AsNoTracking().SingleAsync(ct);
        state.BackfillComplete.Should().BeTrue();
        state.CoverageStartUtc.Should().Be(currentHour);
        (await new DashboardRollupReader(_db).ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, 720, ct))!
            .Slots.Sum(s => s.Total).Should().Be(1);
    }

    /// <summary>Fails the backfill's first query, the lookup of the oldest execution.</summary>
    private sealed class FailingBackfillInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var sql = command.CommandText;
            if (sql.Contains("ORDER BY", StringComparison.Ordinal)
                && sql.Contains("FROM \"WorkflowExecutions\"", StringComparison.Ordinal)
                && !sql.Contains("\"StepExecutions\"", StringComparison.Ordinal))
                throw new InvalidOperationException("simulated backfill failure");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task RunPass_BackfillFails_KeepsTheCurrentHoursAndFreshness()
    {
        // A failing backfill must not take the already covered windows down with it.
        var wf = AddWorkflow();
        var currentHour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow);
        AddExecution(wf, ExecutionStatus.Succeeded, currentHour, currentHour.AddSeconds(1));
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = currentHour.AddHours(-48),
            CoverageEndUtc = currentHour.AddHours(-1),
            BackfillComplete = false,
            UpdatedAt = DateTime.UtcNow - ExecutionStatsRollupService.StaleAfter + TimeSpan.FromMinutes(1),
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(_connection).AddInterceptors(new FailingBackfillInterceptor()));
        services.AddSingleton(_redactor);
        await using var failing = services.BuildServiceProvider();
        var availability = new Mock<IDatabaseAvailability>();
        availability.Setup(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new ExecutionStatsRollupService(
            failing.GetRequiredService<IServiceScopeFactory>(), availability.Object,
            new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
            NullLogger<ExecutionStatsRollupService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunPassAsync(TestContext.Current.CancellationToken));

        var ct = TestContext.Current.CancellationToken;
        var state = await _db.ExecutionStatsRollupStates.AsNoTracking().SingleAsync(ct);
        state.CoverageEndUtc.Should().Be(currentHour);
        state.UpdatedAt.Should().BeAfter(DateTime.UtcNow - TimeSpan.FromMinutes(1));
        var aggregates = await new DashboardRollupReader(_db).ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, 24, ct);
        aggregates.Should().NotBeNull();
        aggregates!.Slots.Sum(s => s.Total).Should().Be(1, "the current hour was rolled up before the backfill failed");
    }

    [Fact]
    public async Task Reader_BackfillIncomplete_StillRequiresCoverageStart()
    {
        var hour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = hour.AddHours(-48),
            CoverageEndUtc = hour,
            BackfillComplete = false,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reader = new DashboardRollupReader(_db);
        var ct = TestContext.Current.CancellationToken;
        (await reader.ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, 24, ct))
            .Should().NotBeNull("the covered range already reaches the window start");
        (await reader.ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, 168, ct))
            .Should().BeNull("older hours may still be missing while the backfill runs");
    }

    [Fact]
    public async Task Reader_MatchesTheLiveComputation()
    {
        // The property that matters: both paths must agree.
        var wf = AddWorkflow();
        var now = DateTime.UtcNow;
        var baseHour = ExecutionStatsRollupService.Truncate(now).AddHours(-3);
        AddExecution(wf, ExecutionStatus.Succeeded, baseHour.AddMinutes(5), baseHour.AddMinutes(6));
        AddExecution(wf, ExecutionStatus.Succeeded, baseHour.AddMinutes(7), baseHour.AddMinutes(8));
        AddExecution(wf, ExecutionStatus.Failed, baseHour.AddHours(1).AddMinutes(5), baseHour.AddHours(1).AddMinutes(6), "boom");
        AddExecution(wf, ExecutionStatus.Cancelled, baseHour.AddHours(2).AddMinutes(5), baseHour.AddHours(2).AddMinutes(6));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var live = await DashboardHistoricalAggregates.ComputeAsync(
            _db, AccessibleFolderSet.Unrestricted, 24, TestContext.Current.CancellationToken);

        for (var h = baseHour; h <= ExecutionStatsRollupService.Truncate(now); h = h.AddHours(1))
            await RollUpAsync(h);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = baseHour.AddHours(-24),
            CoverageEndUtc = ExecutionStatsRollupService.Truncate(now),
            BackfillComplete = true,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var fromBuckets = await new DashboardRollupReader(_db)
            .ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, 24, TestContext.Current.CancellationToken);

        fromBuckets.Should().NotBeNull();
        fromBuckets!.ExecutionsTotal.Should().Be(live.ExecutionsTotal);
        fromBuckets.WindowStartUtc.Should().BeCloseTo(live.WindowStartUtc, TimeSpan.FromMinutes(1));
        fromBuckets.RetryStats.FinishedCount.Should().Be(live.RetryStats.FinishedCount);
        fromBuckets.Slots.Sum(h => h.Total).Should().Be(live.Slots.Sum(h => h.Total));
        fromBuckets.Slots.Sum(h => h.Succeeded).Should().Be(live.Slots.Sum(h => h.Succeeded));
        fromBuckets.Slots.Sum(h => h.Failed).Should().Be(live.Slots.Sum(h => h.Failed));
        fromBuckets.Slots.Sum(h => h.Cancelled).Should().Be(live.Slots.Sum(h => h.Cancelled));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(23)]
    public async Task Reader_WindowShorterThanADay_ReturnsNullEvenWhenCovered(int windowHours)
    {
        var wf = AddWorkflow();
        var now = DateTime.UtcNow;
        var recentHour = ExecutionStatsRollupService.Truncate(now);
        AddExecution(wf, ExecutionStatus.Failed, recentHour, recentHour, "boom");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RollUpAsync(recentHour);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = recentHour.AddHours(-48),
            CoverageEndUtc = recentHour,
            BackfillComplete = true,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reader = new DashboardRollupReader(_db);

        (await reader.ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, windowHours, TestContext.Current.CancellationToken))
            .Should().BeNull("hourly buckets would add up to an hour of runs from before the window");
        (await reader.ReadFailureCausesAsync(AccessibleFolderSet.Unrestricted, windowHours, TestContext.Current.CancellationToken))
            .Should().BeNull();
        (await reader.ReadDurationBucketsAsync(AccessibleFolderSet.Unrestricted, windowHours, null, now, TestContext.Current.CancellationToken))
            .Should().BeNull();
        (await reader.ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, DashboardRollupReader.MinimumWindowHours, TestContext.Current.CancellationToken))
            .Should().NotBeNull("the same coverage serves a full day");
    }

    [Fact]
    public async Task Reader_DurationBuckets_MatchTheRawTrendWithinTheBinTolerance()
    {
        var wf = AddWorkflow();
        var now = DateTime.UtcNow;
        var hour = ExecutionStatsRollupService.Truncate(now).AddHours(-2);
        foreach (var seconds in Enumerable.Range(1, 20))
            AddExecution(wf, ExecutionStatus.Succeeded, hour.AddMinutes(1), hour.AddMinutes(1).AddSeconds(seconds));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var trend = new DashboardDurationTrend(_db);
        var raw = await trend.ReadAsync(AccessibleFolderSet.Unrestricted, 24, null, TestContext.Current.CancellationToken, now);

        await RollUpAsync(hour);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = hour.AddHours(-24),
            CoverageEndUtc = ExecutionStatsRollupService.Truncate(now),
            BackfillComplete = true,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var fromBuckets = await trend.ReadAsync(AccessibleFolderSet.Unrestricted, 24, null, TestContext.Current.CancellationToken, now);

        var expected = raw.Buckets.Single(b => b.Count > 0);
        var actual = fromBuckets.Buckets.Single(b => b.Count > 0);
        fromBuckets.Buckets.Should().HaveCount(24);
        actual.StartedAt.Should().Be(hour, "the grid is aligned to whole hours");
        actual.Count.Should().Be(expected.Count);
        actual.MedianMs!.Value.Should().BeApproximately(expected.MedianMs!.Value, expected.MedianMs.Value * 0.045);
        actual.P95Ms!.Value.Should().BeApproximately(expected.P95Ms!.Value, expected.P95Ms.Value * 0.045);
    }

    [Fact]
    public async Task Reader_DurationBuckets_HonourFolderScopeAndWorkflowFilter()
    {
        var hiddenFolder = Guid.NewGuid();
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = hiddenFolder, ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "hidden", Path = "/hidden", Depth = 1,
        });
        var visible = AddWorkflow("visible");
        var hidden = Guid.NewGuid();
        _db.Workflows.Add(new Workflow
        {
            Id = hidden, Name = "hidden", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow, FolderId = hiddenFolder,
        });
        var now = DateTime.UtcNow;
        var hour = ExecutionStatsRollupService.Truncate(now).AddHours(-1);
        AddExecution(visible, ExecutionStatus.Succeeded, hour.AddMinutes(5), hour.AddMinutes(5).AddSeconds(1));
        AddExecution(hidden, ExecutionStatus.Succeeded, hour.AddMinutes(6), hour.AddMinutes(6).AddSeconds(9));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await RollUpAsync(hour);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = hour.AddHours(-24),
            CoverageEndUtc = ExecutionStatsRollupService.Truncate(now),
            BackfillComplete = true,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var reader = new DashboardRollupReader(_db);
        var ct = TestContext.Current.CancellationToken;
        var scoped = new AccessibleFolderSet { IsUnrestricted = false, FolderIds = [SharedWorkflowFolder.RootFolderId] };

        var visibleOnly = await reader.ReadDurationBucketsAsync(scoped, 24, null, now, ct);
        visibleOnly!.Sum(b => b.Count).Should().Be(1);
        visibleOnly.Single(b => b.Count > 0).MedianMs.Should().Be(DurationHistogram.ValueOf(DurationHistogram.BinOf(1000)));

        var selected = await reader.ReadDurationBucketsAsync(AccessibleFolderSet.Unrestricted, 24, hidden, now, ct);
        selected!.Single(b => b.Count > 0).MedianMs.Should().Be(DurationHistogram.ValueOf(DurationHistogram.BinOf(9000)));

        (await reader.ReadDurationBucketsAsync(AccessibleFolderSet.Unrestricted, 24, null, now, ct))!
            .Sum(b => b.Count).Should().Be(2);
        (await reader.ReadDurationBucketsAsync(AccessibleFolderSet.None, 24, null, now, ct))!
            .Should().OnlyContain(b => b.Count == 0 && b.MedianMs == null && b.P95Ms == null);
    }

    [Fact]
    public async Task Reader_FailureCauses_NeverLeakAcrossFolderScopes()
    {
        // Failure buckets carry an execution id and start time that the UI turns into a drill-down
        // link. A caller who cannot read the folder must see neither the count nor the id.
        var hiddenFolder = Guid.NewGuid();
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = hiddenFolder, ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "hidden", Path = "/hidden", Depth = 1,
        });
        var hiddenWorkflow = Guid.NewGuid();
        _db.Workflows.Add(new Workflow
        {
            Id = hiddenWorkflow, Name = "hidden", DefinitionJson = "{}",
            UpdatedAt = DateTime.UtcNow, FolderId = hiddenFolder,
        });

        var baseHour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow).AddHours(-1);
        AddExecution(hiddenWorkflow, ExecutionStatus.Failed, baseHour.AddMinutes(5),
            baseHour.AddMinutes(6), "secret failure");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(baseHour);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = baseHour.AddHours(-24),
            CoverageEndUtc = baseHour,
            BackfillComplete = true,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var reader = new DashboardRollupReader(_db);
        var scoped = new AccessibleFolderSet
        {
            IsUnrestricted = false, FolderIds = [SharedWorkflowFolder.RootFolderId],
        };

        var restricted = await reader.ReadFailureCausesAsync(scoped, 24, TestContext.Current.CancellationToken);
        restricted.Should().NotBeNull();
        restricted!.TotalFailed.Should().Be(0);
        restricted.Groups.Should().BeEmpty();

        // Sanity: an unrestricted caller does see it, so the test proves scoping, not emptiness.
        var unrestricted = await reader.ReadFailureCausesAsync(
            AccessibleFolderSet.Unrestricted, 24, TestContext.Current.CancellationToken);
        unrestricted!.TotalFailed.Should().Be(1);
    }

    [Fact]
    public async Task Reader_FailureCauses_KeepTheFullMessage()
    {
        // The message is returned verbatim in the API response, so the bucket must not shorten it.
        var wf = AddWorkflow();
        var longMessage = new string('x', 9000);
        var baseHour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow).AddHours(-1);
        AddExecution(wf, ExecutionStatus.Failed, baseHour.AddMinutes(5), baseHour.AddMinutes(6), longMessage);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(baseHour);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var bucket = await _db.FailureCauseHourlyStats.SingleAsync(TestContext.Current.CancellationToken);
        bucket.Message.Should().HaveLength(9000);
    }

    [Fact]
    public async Task Reader_ExecutionsTotal_CountsBeyondTheCoveredWindow()
    {
        // Regression guard: summing buckets would report only the covered range, so history older
        // than the window (or not yet backfilled) would vanish from the all-time counter.
        var wf = AddWorkflow();
        var now = DateTime.UtcNow;
        var recentHour = ExecutionStatsRollupService.Truncate(now).AddHours(-1);
        AddExecution(wf, ExecutionStatus.Succeeded, recentHour.AddMinutes(5), recentHour.AddMinutes(6));
        // Far outside the 24 h window and outside the covered range.
        AddExecution(wf, ExecutionStatus.Succeeded, now.AddDays(-20), now.AddDays(-20).AddMinutes(1));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(recentHour);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = ExecutionStatsRollupService.Truncate(now).AddHours(-24),
            CoverageEndUtc = recentHour,
            BackfillComplete = false,
            UpdatedAt = now,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new DashboardRollupReader(_db)
            .ReadWindowAggregatesAsync(AccessibleFolderSet.Unrestricted, 24, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.ExecutionsTotal.Should().Be(2, "the all-time counter covers everything, not just the window");
        result.Slots.Sum(h => h.Total).Should().Be(1, "the chart still only covers the window");
    }

    [Fact]
    public async Task Reader_ScopesByFolderPermissions()
    {
        var visibleFolder = SharedWorkflowFolder.RootFolderId;
        var hiddenFolder = Guid.NewGuid();
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = hiddenFolder, ParentFolderId = visibleFolder, Name = "hidden", Path = "/hidden", Depth = 1,
        });
        var visible = AddWorkflow("visible");
        var hiddenId = Guid.NewGuid();
        _db.Workflows.Add(new Workflow
        {
            Id = hiddenId, Name = "hidden", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow,
            FolderId = hiddenFolder,
        });

        var baseHour = ExecutionStatsRollupService.Truncate(DateTime.UtcNow).AddHours(-1);
        AddExecution(visible, ExecutionStatus.Succeeded, baseHour.AddMinutes(5), baseHour.AddMinutes(6));
        AddExecution(hiddenId, ExecutionStatus.Succeeded, baseHour.AddMinutes(7), baseHour.AddMinutes(8));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RollUpAsync(baseHour);
        _db.ExecutionStatsRollupStates.Add(new ExecutionStatsRollupState
        {
            Id = ExecutionStatsRollupService.StateRowId,
            CoverageStartUtc = baseHour.AddHours(-24),
            CoverageEndUtc = baseHour,
            BackfillComplete = true,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var scoped = new AccessibleFolderSet { IsUnrestricted = false, FolderIds = [visibleFolder] };
        var result = await new DashboardRollupReader(_db)
            .ReadWindowAggregatesAsync(scoped, 24, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Slots.Sum(h => h.Total).Should().Be(1, "the hidden folder's workflow must not be counted");
    }
}

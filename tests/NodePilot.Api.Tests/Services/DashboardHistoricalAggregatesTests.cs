using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Services;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Services;

public sealed class DashboardHistoricalAggregatesTests
{
    private static readonly DateTime WindowStart = new(2026, 9, 17, 9, 37, 42, DateTimeKind.Utc);

    private static ExecutionSlotAggregate Slot(DateTime start, int succeeded = 0, int failed = 0, int cancelled = 0)
        => new(start, succeeded + failed + cancelled, succeeded, failed, 0, cancelled);

    private static DateTime Minute(int hour, int minute) => new(2026, 9, 17, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildBuckets_OneHourWindow_Returns30TwoMinuteBucketsFromTheWholeMinute()
    {
        var buckets = DashboardHistoricalAggregates.BuildBuckets(WindowStart, 1, []);

        buckets.Should().HaveCount(30);
        buckets[0].HourStart.Should().Be(Minute(9, 37));
        buckets[0].HourStart.Kind.Should().Be(DateTimeKind.Utc);
        buckets[^1].HourStart.Should().Be(Minute(10, 35));
    }

    [Fact]
    public void BuildBuckets_OneHourWindow_SplitsAtTwoMinuteBoundaries()
    {
        var buckets = DashboardHistoricalAggregates.BuildBuckets(WindowStart, 1,
        [
            Slot(Minute(9, 37), succeeded: 1),
            Slot(Minute(9, 38), failed: 1),
            Slot(Minute(9, 39), cancelled: 1),
            Slot(Minute(10, 36), succeeded: 2),
        ]);

        buckets[0].Succeeded.Should().Be(1);
        buckets[0].Failed.Should().Be(1);
        buckets[0].Cancelled.Should().Be(0);
        buckets[1].Cancelled.Should().Be(1);
        buckets[^1].Succeeded.Should().Be(2);
    }

    [Fact]
    public void BuildBuckets_OneHourWindow_PutsTheCurrentMinuteIntoTheLastBucket()
    {
        var buckets = DashboardHistoricalAggregates.BuildBuckets(WindowStart, 1, [Slot(Minute(10, 37), failed: 3)]);

        buckets[^1].Failed.Should().Be(3);
    }

    [Fact]
    public void BuildBuckets_DayWindow_KeepsHourlyBucketsFromTheWindowStart()
    {
        var buckets = DashboardHistoricalAggregates.BuildBuckets(WindowStart, 24, [Slot(Minute(9, 0), succeeded: 4)]);

        buckets.Should().HaveCount(24);
        buckets[0].HourStart.Should().Be(WindowStart);
        buckets[1].HourStart.Should().Be(WindowStart.AddHours(1));
        buckets[0].Succeeded.Should().Be(4, "the hour the window starts in belongs to the first bucket");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(168)]
    [InlineData(720)]
    public void BuildBuckets_BucketTotals_EqualSlotTotals(int windowHours)
    {
        var slotMinutes = windowHours == 1 ? 1 : 60;
        var windowEnd = WindowStart.AddHours(windowHours);
        var first = new DateTime(WindowStart.Ticks - WindowStart.Ticks % TimeSpan.FromMinutes(slotMinutes).Ticks, DateTimeKind.Utc);
        var slots = new List<ExecutionSlotAggregate>();
        for (var t = first; t <= windowEnd; t = t.AddMinutes(slotMinutes))
            slots.Add(Slot(t, succeeded: 1, failed: 2, cancelled: 3));

        var buckets = DashboardHistoricalAggregates.BuildBuckets(WindowStart, windowHours, slots);

        buckets.Sum(b => b.Succeeded).Should().Be(slots.Count);
        buckets.Sum(b => b.Failed).Should().Be(slots.Count * 2);
        buckets.Sum(b => b.Cancelled).Should().Be(slots.Count * 3);
    }

    [Fact]
    public async Task ComputeAsync_OneHourWindow_ReturnsUtcMinuteSlotsFromRawRows()
    {
        using var db = TestDbFactory.Create();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        var minute = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute)).AddMinutes(-30);
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "W", DefinitionJson = "{}", UpdatedAt = now };
        db.Workflows.Add(workflow);
        db.WorkflowExecutions.AddRange(
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Succeeded, StartedAt = minute.AddSeconds(10) },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Failed, StartedAt = minute.AddSeconds(40) },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Succeeded, StartedAt = now.AddMinutes(-90) });
        await db.SaveChangesAsync(ct);

        var result = await DashboardHistoricalAggregates.ComputeAsync(db, AccessibleFolderSet.Unrestricted, 1, ct);

        var slot = result.Slots.Should().ContainSingle().Subject;
        slot.SlotStartUtc.Should().Be(minute);
        slot.SlotStartUtc.Kind.Should().Be(DateTimeKind.Utc);
        slot.Succeeded.Should().Be(1);
        slot.Failed.Should().Be(1);
        result.WindowStartUtc.Should().BeCloseTo(now.AddHours(-1), TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData("postgres", "AT TIME ZONE 'UTC'", "'minute'")]
    [InlineData("sqlserver", "DATEPART(minute", "DATEPART(hour")]
    public void BuildSlotQuery_ByMinute_ExtractsUtcDateParts(string provider, string expected, string alsoExpected)
    {
        var options = new DbContextOptionsBuilder<NodePilotDbContext>();
        if (provider == "postgres") options.UseNpgsql("Host=localhost;Database=translation_only;Username=test;Password=test");
        else options.UseSqlServer("Server=(local);Database=translation_only;Integrated Security=true;TrustServerCertificate=true");
        using var db = new NodePilotDbContext(options.Options);

        var sql = DashboardHistoricalAggregates
            .BuildSlotQuery(db.WorkflowExecutions, WindowStart, WindowStart.AddHours(1), byMinute: true)
            .ToQueryString();

        sql.Should().Contain(expected).And.Contain(alsoExpected).And.Contain("GROUP BY");
    }
}

using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Diagnostics;
using NodePilot.Api.Dtos;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Serilog.Events;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Regression pins for the events-query endpoint: cursor pagination, filter allowlist,
/// take-cap, sort order. Pattern borrowed from AuditControllerTests.
/// </summary>
public class DiagnosticsControllerEventsTests
{
    private static SupportEvent E(string eventType, DateTime ts,
        Guid? executionId = null, string? workflowName = null,
        int level = (int)LogEventLevel.Information,
        string? message = null,
        string? userName = null,
        string? activityType = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Timestamp = ts,
            Level = level,
            EventType = eventType,
            Message = message ?? eventType,
            ExecutionId = executionId,
            ExecutionShort = executionId?.ToString("N")[..8],
            WorkflowName = workflowName,
            UserName = userName,
            ActivityType = activityType,
        };

    private static DiagnosticsController MakeController(Data.NodePilotDbContext db)
        => new(
            resolver: new NoopResolver(),
            db: db,
            logger: NullLogger<DiagnosticsController>.Instance,
            audit: NoopAuditWriter.Instance);

    private static SupportEventPageResponse PageOf(ActionResult<SupportEventPageResponse> r)
        => (SupportEventPageResponse)((OkObjectResult)r.Result!).Value!;

    [Fact]
    public async Task Events_DefaultPage_ReturnsNewestFirst()
    {
        var db = TestDbFactory.Create();
        for (int i = 0; i < 10; i++)
            db.SupportEvents.Add(E("USER_LOG", DateTime.UtcNow.AddMinutes(-i)));
        await db.SaveChangesAsync();

        var ctrl = MakeController(db);
        var result = await ctrl.Events(null, null, null, null, null, null, null, null, null, null, null,
            afterTs: null, afterId: null, take: 5, ct: CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().HaveCount(5);
        page.Items.Should().BeInDescendingOrder(r => r.Timestamp);
    }

    [Fact]
    public async Task Events_FilterByEventType_OnlyMatching()
    {
        var db = TestDbFactory.Create();
        db.SupportEvents.Add(E("USER_LOG", DateTime.UtcNow));
        db.SupportEvents.Add(E("STEP_FAILED", DateTime.UtcNow));
        db.SupportEvents.Add(E("USER_LOG", DateTime.UtcNow));
        await db.SaveChangesAsync();

        var ctrl = MakeController(db);
        var result = await ctrl.Events(null, null, null, eventType: "STEP_FAILED",
            null, null, null, null, null, null, null, null, null, take: 100, ct: CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().ContainSingle();
        page.Items[0].EventType.Should().Be("STEP_FAILED");
    }

    [Fact]
    public async Task Events_FilterByLevel_IsLowerBound()
    {
        var db = TestDbFactory.Create();
        db.SupportEvents.Add(E("X", DateTime.UtcNow, level: (int)LogEventLevel.Information));
        db.SupportEvents.Add(E("X", DateTime.UtcNow, level: (int)LogEventLevel.Warning));
        db.SupportEvents.Add(E("X", DateTime.UtcNow, level: (int)LogEventLevel.Error));
        await db.SaveChangesAsync();

        var ctrl = MakeController(db);
        var result = await ctrl.Events(null, null, level: (int)LogEventLevel.Warning,
            null, null, null, null, null, null, null, null, null, null, take: 100, ct: CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(r => r.Level >= (int)LogEventLevel.Warning);
    }

    [Fact]
    public async Task Events_TakeIsClampedToMax500()
    {
        var db = TestDbFactory.Create();
        for (int i = 0; i < 10; i++)
            db.SupportEvents.Add(E("X", DateTime.UtcNow.AddSeconds(-i)));
        await db.SaveChangesAsync();

        var ctrl = MakeController(db);
        var result = await ctrl.Events(null, null, null, null, null, null, null, null, null, null, null,
            afterTs: null, afterId: null, take: 99_999, ct: CancellationToken.None);

        // take > 500 gets clamped to 500; with only 10 rows in the DB we return all 10 (no cursor).
        var page = PageOf(result);
        page.Items.Should().HaveCount(10);
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Events_Cursor_DeliversNextPage()
    {
        var db = TestDbFactory.Create();
        // 5 events with clearly separated timestamps
        var baseTime = DateTime.UtcNow.AddHours(-1);
        for (int i = 0; i < 5; i++)
            db.SupportEvents.Add(E("X", baseTime.AddMinutes(i)));
        await db.SaveChangesAsync();

        var ctrl = MakeController(db);
        // First page: take=2 → expect 2 events + nextCursor
        var page1 = PageOf(await ctrl.Events(null, null, null, null, null, null, null, null, null, null, null,
            afterTs: null, afterId: null, take: 2, ct: CancellationToken.None));
        page1.Items.Should().HaveCount(2);
        page1.NextCursor.Should().NotBeNull();

        // Second page: cursor from the end of page1 → expect 2 more
        var page2 = PageOf(await ctrl.Events(null, null, null, null, null, null, null, null, null, null, null,
            afterTs: page1.NextCursor!.AfterTs, afterId: page1.NextCursor.AfterId, take: 2, ct: CancellationToken.None));
        page2.Items.Should().HaveCount(2);
        page2.Items[0].Id.Should().NotBe(page1.Items[1].Id, "Cursor darf den Probe-Row nicht doppelt liefern");
    }

    [Fact]
    public async Task Events_FilterByWorkflowName_Exact()
    {
        var db = TestDbFactory.Create();
        db.SupportEvents.Add(E("X", DateTime.UtcNow, workflowName: "Daily Report"));
        db.SupportEvents.Add(E("X", DateTime.UtcNow, workflowName: "Nightly Backup"));
        await db.SaveChangesAsync();

        var ctrl = MakeController(db);
        var result = await ctrl.Events(null, null, null, null, null, workflowName: "Daily Report",
            null, null, null, null, null, null, null, take: 100, ct: CancellationToken.None);

        var page = PageOf(result);
        page.Items.Should().ContainSingle();
        page.Items[0].WorkflowName.Should().Be("Daily Report");
    }

    private sealed class NoopResolver : ISupportLogFileResolver
    {
        public string Directory => "";
        public string FileSearchPattern => "*.log";
        public string? GetCurrentDayFile() => null;
        public string? GetFileForDate(DateOnly date) => null;
    }
}

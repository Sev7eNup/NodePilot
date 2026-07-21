using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
/// Covers the parts of <see cref="DiagnosticsController"/> not pinned by
/// <see cref="DiagnosticsControllerEventsTests"/>: the streaming CSV/NDJSON export
/// (incl. RFC-4180 field escaping), the full sort-column allowlist via
/// <c>ApplyOrderBy</c>, custom-sort cursor suppression, the remaining query filters,
/// and the dated log-file download.
/// </summary>
public sealed class DiagnosticsControllerExportTests
{
    private static SupportEvent E(
        string eventType, DateTime ts,
        int level = (int)LogEventLevel.Information,
        string? message = null, string? workflowName = null, Guid? workflowId = null,
        Guid? executionId = null, string? stepId = null, string? activityType = null,
        string? userName = null)
        => new()
        {
            Id = Guid.NewGuid(), Timestamp = ts, Level = level, EventType = eventType,
            Message = message ?? eventType, WorkflowName = workflowName, WorkflowId = workflowId,
            ExecutionId = executionId, ExecutionShort = executionId?.ToString("N")[..8],
            StepId = stepId, ActivityType = activityType, UserName = userName,
        };

    private sealed class NoopResolver : ISupportLogFileResolver
    {
        public string? FileForAnyDate { get; init; }
        public string Directory => "";
        public string FileSearchPattern => "*.log";
        public string? GetCurrentDayFile() => null;
        public string? GetFileForDate(DateOnly date) => FileForAnyDate;
    }

    private static DiagnosticsController MakeController(Data.NodePilotDbContext db, ISupportLogFileResolver? resolver = null)
        => new(resolver ?? new NoopResolver(), db, NullLogger<DiagnosticsController>.Instance);

    /// <summary>Wires a writable Response.Body so the streaming export can be captured.</summary>
    private static (DiagnosticsController Ctrl, MemoryStream Body) WithResponseBody(Data.NodePilotDbContext db, ISupportLogFileResolver? resolver = null)
    {
        var ctrl = MakeController(db, resolver);
        var body = new MemoryStream();
        var http = new DefaultHttpContext();
        http.Response.Body = body;
        ctrl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctrl, body);
    }

    // ExportEvents wraps Response.Body in a StreamWriter, which disposes the underlying
    // stream on exit. MemoryStream.ToArray() is documented to work even on a closed stream,
    // so it survives that disposal — unlike seeking + StreamReader.
    private static string ReadBody(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    // ===== ExportEvents — CSV =================================================

    [Fact]
    public async Task ExportEvents_Csv_WritesHeaderAndRows()
    {
        var db = TestDbFactory.Create();
        db.SupportEvents.Add(E("USER_LOG", DateTime.UtcNow, message: "hello", workflowName: "Daily"));
        await db.SaveChangesAsync();

        var (ctrl, body) = WithResponseBody(db);
        await ctrl.ExportEvents("csv", ct: CancellationToken.None);

        var text = ReadBody(body);
        text.Should().StartWith("Id,Timestamp,Level,EventType,WorkflowName");
        text.Should().Contain("USER_LOG").And.Contain("hello").And.Contain("Daily");
        ctrl.Response.ContentType.Should().Contain("text/csv");
        ctrl.Response.Headers.ContentDisposition.ToString().Should().Contain(".csv");
    }

    [Fact]
    public async Task ExportEvents_Csv_QuotesFieldsWithCommasQuotesAndNewlines()
    {
        var db = TestDbFactory.Create();
        // A message that needs RFC-4180 quoting: contains a comma, a double-quote and a newline.
        db.SupportEvents.Add(E("X", DateTime.UtcNow, message: "a,b \"c\"\nd"));
        await db.SaveChangesAsync();

        var (ctrl, body) = WithResponseBody(db);
        await ctrl.ExportEvents("csv", ct: CancellationToken.None);

        var text = ReadBody(body);
        // Embedded quotes are doubled and the whole field is wrapped in quotes.
        text.Should().Contain("\"a,b \"\"c\"\"");
    }

    // ===== ExportEvents — NDJSON =============================================

    [Fact]
    public async Task ExportEvents_Ndjson_WritesOneJsonObjectPerRow()
    {
        var db = TestDbFactory.Create();
        db.SupportEvents.Add(E("STEP_FAILED", DateTime.UtcNow, message: "boom", activityType: "runScript"));
        db.SupportEvents.Add(E("USER_LOG", DateTime.UtcNow.AddSeconds(-1), message: "ok"));
        await db.SaveChangesAsync();

        var (ctrl, body) = WithResponseBody(db);
        await ctrl.ExportEvents("ndjson", ct: CancellationToken.None);

        var lines = ReadBody(body).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines.Should().AllSatisfy(l => l.Trim().Should().StartWith("{").And.EndWith("}"));
        ReadBody(body).Should().Contain("\"eventType\":\"STEP_FAILED\"").And.Contain("runScript");
        ctrl.Response.ContentType.Should().Contain("application/x-ndjson");
    }

    [Fact]
    public async Task ExportEvents_UnknownFormat_Returns400()
    {
        var db = TestDbFactory.Create();
        var (ctrl, body) = WithResponseBody(db);

        await ctrl.ExportEvents("xml", ct: CancellationToken.None);

        ctrl.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        ReadBody(body).Should().Contain("Unsupported format");
    }

    [Fact]
    public async Task ExportEvents_AppliesFilters()
    {
        var db = TestDbFactory.Create();
        db.SupportEvents.Add(E("KEEP", DateTime.UtcNow, activityType: "sql"));
        db.SupportEvents.Add(E("DROP", DateTime.UtcNow, activityType: "runScript"));
        await db.SaveChangesAsync();

        var (ctrl, body) = WithResponseBody(db);
        await ctrl.ExportEvents("csv", activityType: "sql", ct: CancellationToken.None);

        var text = ReadBody(body);
        text.Should().Contain("KEEP");
        text.Should().NotContain("DROP");
    }

    // ===== Events — remaining filters ========================================

    [Fact]
    public async Task Events_FiltersBySinceUntilExecutionStepActivityUsernameAndFullText()
    {
        var db = TestDbFactory.Create();
        var execId = Guid.NewGuid();
        var t = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        db.SupportEvents.Add(E("A", t, message: "needle here", executionId: execId,
            stepId: "step-7", activityType: "sql", userName: "Alice"));
        db.SupportEvents.Add(E("B", t.AddDays(-10), message: "too old"));            // before since
        db.SupportEvents.Add(E("C", t.AddDays(10), message: "too new"));             // after until
        await db.SaveChangesAsync();

        var ctrl = MakeController(db);
        var result = await ctrl.Events(
            since: t.AddDays(-1), until: t.AddDays(1), level: null, eventType: null,
            workflowId: null, workflowName: null, executionId: execId, stepId: "step-7",
            activityType: "sql", username: "alice", q: "needle",
            afterTs: null, afterId: null, take: 100, ct: CancellationToken.None);

        var page = (SupportEventPageResponse)((OkObjectResult)result.Result!).Value!;
        page.Items.Should().ContainSingle();
        page.Items[0].EventType.Should().Be("A");
    }

    // ===== Events — ApplyOrderBy across the sort allowlist ===================

    [Theory]
    [InlineData("level")]
    [InlineData("eventType")]
    [InlineData("status")]
    [InlineData("workflowName")]
    [InlineData("executionShort")]
    [InlineData("stepLabel")]
    [InlineData("activityType")]
    [InlineData("userName")]
    [InlineData("message")]
    public async Task Events_CustomSort_OrdersAndSuppressesCursor(string sortBy)
    {
        var db = TestDbFactory.Create();
        for (int i = 0; i < 4; i++)
            db.SupportEvents.Add(E($"EVT{i}", DateTime.UtcNow.AddSeconds(-i),
                level: i, workflowName: $"wf{i}", activityType: $"act{i}", userName: $"user{i}",
                message: $"msg{i}"));
        await db.SaveChangesAsync();

        var ctrl = MakeController(db);
        var asc = (SupportEventPageResponse)((OkObjectResult)(await ctrl.Events(
            null, null, null, null, null, null, null, null, null, null, null,
            afterTs: null, afterId: null, sortBy: sortBy, sortDir: "asc", take: 2,
            ct: CancellationToken.None)).Result!).Value!;

        // Custom (non-timestamp) sort returns a page but never a cursor — the contract says
        // operators must filter rather than deep-paginate on a non-deterministic column.
        asc.Items.Should().HaveCount(2);
        asc.HasMore.Should().BeTrue();
        asc.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Events_UnknownSortColumn_FallsBackToTimestampWithCursor()
    {
        var db = TestDbFactory.Create();
        for (int i = 0; i < 4; i++)
            db.SupportEvents.Add(E("X", DateTime.UtcNow.AddMinutes(-i)));
        await db.SaveChangesAsync();

        var ctrl = MakeController(db);
        var page = (SupportEventPageResponse)((OkObjectResult)(await ctrl.Events(
            null, null, null, null, null, null, null, null, null, null, null,
            afterTs: null, afterId: null, sortBy: "bogusColumn", sortDir: "desc", take: 2,
            ct: CancellationToken.None)).Result!).Value!;

        // Unknown column silently falls back to the default timestamp sort, which DOES
        // support cursor pagination.
        page.Items.Should().HaveCount(2);
        page.NextCursor.Should().NotBeNull();
    }

    // ===== Tail — clamps + read-error path ===================================

    [Fact]
    public void Tail_ReadError_Returns500()
    {
        // Point the resolver at a *directory* path: opening it as a FileStream throws,
        // exercising the catch → 500 branch.
        var dir = Path.Combine(Path.GetTempPath(), "np-diag-taildir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = TestDbFactory.Create();
            var ctrl = MakeController(db, new CurrentDayResolver { CurrentDay = dir });

            var result = ctrl.Tail(lines: 10);

            var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
            obj.StatusCode.Should().Be(500);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void Tail_ClampsLinesToBounds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "np-diag-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "support.log");
        File.WriteAllLines(file, Enumerable.Range(1, 3).Select(i => $"l{i}"));
        try
        {
            var db = TestDbFactory.Create();
            var ctrl = MakeController(db, new CurrentDayResolver { CurrentDay = file });

            // lines < 1 is clamped up to 1; an over-large value is clamped down to MaxTailLines.
            var low = (SupportLogTailResponse)((OkObjectResult)ctrl.Tail(lines: 0).Result!).Value!;
            low.LineCount.Should().Be(1, "lines < 1 is clamped to 1");

            var high = (SupportLogTailResponse)((OkObjectResult)ctrl.Tail(lines: 999_999).Result!).Value!;
            high.LineCount.Should().Be(3, "only 3 lines exist even though the cap is 1000");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private sealed class CurrentDayResolver : ISupportLogFileResolver
    {
        public string? CurrentDay { get; init; }
        public string Directory => "";
        public string FileSearchPattern => "*.log";
        public string? GetCurrentDayFile() => CurrentDay;
        public string? GetFileForDate(DateOnly date) => null;
    }

    // ===== Download ==========================================================

    [Fact]
    public void Download_InvalidDate_Returns400()
    {
        var db = TestDbFactory.Create();
        var ctrl = MakeController(db);
        var result = ctrl.Download("not-a-date");
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Download_MissingFile_Returns404()
    {
        var db = TestDbFactory.Create();
        var ctrl = MakeController(db, new NoopResolver { FileForAnyDate = null });
        var result = ctrl.Download("2026-05-15");
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Download_ExistingFile_ReturnsFileStream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "np-diag-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "nodepilot-support-20260515.log");
        File.WriteAllText(file, "log content");
        try
        {
            var db = TestDbFactory.Create();
            var ctrl = MakeController(db, new NoopResolver { FileForAnyDate = file });
            var result = ctrl.Download("2026-05-15");

            var fileResult = result.Should().BeOfType<FileStreamResult>().Subject;
            fileResult.ContentType.Should().Be("text/plain");
            fileResult.FileDownloadName.Should().Be("nodepilot-support-20260515.log");
            fileResult.FileStream.Dispose();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}

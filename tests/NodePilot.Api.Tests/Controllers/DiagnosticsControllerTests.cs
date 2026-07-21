using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Diagnostics;
using NodePilot.Api.Dtos;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class DiagnosticsControllerTests : IDisposable
{
    private readonly string _tempDir;

    public DiagnosticsControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "np-supportlog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class StubResolver : ISupportLogFileResolver
    {
        public string Directory { get; init; } = "";
        public string FileSearchPattern { get; init; } = "*.log";
        public string? CurrentDayFile { get; init; }
        public Dictionary<DateOnly, string?> ByDate { get; } = new();

        public string? GetCurrentDayFile() => CurrentDayFile;
        public string? GetFileForDate(DateOnly date) => ByDate.GetValueOrDefault(date);
    }

    private DiagnosticsController Create(StubResolver resolver)
        => new(resolver, NodePilot.TestCommons.TestDbFactory.Create(), NullLogger<DiagnosticsController>.Instance);

    [Fact]
    public void Tail_NoFile_Returns200WithEmptyLines()
    {
        var ctrl = Create(new StubResolver { CurrentDayFile = null });
        var result = ctrl.Tail(lines: 200);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = (SupportLogTailResponse)ok.Value!;
        payload.File.Should().BeNull();
        payload.LineCount.Should().Be(0);
        payload.Lines.Should().BeEmpty();
    }

    [Fact]
    public void Tail_ReturnsLastN_Lines()
    {
        var file = Path.Combine(_tempDir, "nodepilot-support-20260515.log");
        File.WriteAllLines(file, Enumerable.Range(1, 50).Select(i => $"line {i}"));

        var ctrl = Create(new StubResolver { CurrentDayFile = file });
        var result = ctrl.Tail(lines: 5);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var payload = (SupportLogTailResponse)ok.Value!;
        payload.LineCount.Should().Be(5);
        payload.Lines.Should().Equal("line 46", "line 47", "line 48", "line 49", "line 50");
    }

    [Fact]
    public void Tail_FewerLinesThanRequested_ReturnsAll()
    {
        var file = Path.Combine(_tempDir, "support.log");
        File.WriteAllLines(file, new[] { "only-line-1", "only-line-2" });

        var ctrl = Create(new StubResolver { CurrentDayFile = file });
        var result = ctrl.Tail(lines: 100);

        var payload = (SupportLogTailResponse)((OkObjectResult)result.Result!).Value!;
        payload.Lines.Should().HaveCount(2);
    }

    [Fact]
    public void Tail_NegativeLines_ClampedToOne()
    {
        var file = Path.Combine(_tempDir, "support.log");
        File.WriteAllLines(file, new[] { "a", "b", "c" });

        var ctrl = Create(new StubResolver { CurrentDayFile = file });
        var result = ctrl.Tail(lines: -5);

        var payload = (SupportLogTailResponse)((OkObjectResult)result.Result!).Value!;
        payload.Lines.Should().Equal("c");
    }

    [Fact]
    public void Tail_HugeLineRequest_CappedAt1000()
    {
        // Anti-DoS guard: the response is capped at 1000 lines even if a UI bug asks for
        // more. File has 2000 lines, request=99999 → response is still limited to 1000.
        var file = Path.Combine(_tempDir, "support.log");
        File.WriteAllLines(file, Enumerable.Range(1, 2000).Select(i => $"l{i}"));

        var ctrl = Create(new StubResolver { CurrentDayFile = file });
        var result = ctrl.Tail(lines: 99_999);

        var payload = (SupportLogTailResponse)((OkObjectResult)result.Result!).Value!;
        payload.LineCount.Should().Be(1000);
        payload.Lines[0].Should().Be("l1001");
        payload.Lines[^1].Should().Be("l2000");
    }

    [Fact]
    public void Download_InvalidDate_ReturnsBadRequest()
    {
        var ctrl = Create(new StubResolver());
        var result = ctrl.Download("not-a-date");
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Download_FileMissing_Returns404()
    {
        var ctrl = Create(new StubResolver());
        var result = ctrl.Download("2026-05-15");
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Download_FileExists_StreamsPlainTextFile()
    {
        var file = Path.Combine(_tempDir, "nodepilot-support-20260515.log");
        File.WriteAllText(file, "complete file contents\n");
        var resolver = new StubResolver();
        resolver.ByDate[new DateOnly(2026, 5, 15)] = file;

        var ctrl = Create(resolver);
        var result = ctrl.Download("2026-05-15");

        var fsr = result.Should().BeOfType<FileStreamResult>().Subject;
        fsr.ContentType.Should().Be("text/plain");
        fsr.FileDownloadName.Should().Be("nodepilot-support-20260515.log");
        fsr.FileStream.Dispose(); // controller would close it after streaming
    }

    [Fact]
    public void Tail_FileBeingWritten_ReadsWithShareReadWrite()
    {
        // FileShare.ReadWrite is required: Serilog keeps the log file open for writing
        // while we tail it. Without this share flag, opening the file here would throw IOException.
        var file = Path.Combine(_tempDir, "live.log");
        using var writeStream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        using var writer = new StreamWriter(writeStream) { AutoFlush = true };
        writer.WriteLine("first");
        writer.WriteLine("second");

        var ctrl = Create(new StubResolver { CurrentDayFile = file });
        var result = ctrl.Tail(lines: 10);

        var payload = (SupportLogTailResponse)((OkObjectResult)result.Result!).Value!;
        payload.Lines.Should().Contain("first").And.Contain("second");
    }
}

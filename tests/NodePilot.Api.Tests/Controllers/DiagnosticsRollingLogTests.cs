using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Diagnostics;
using NodePilot.Api.Dtos;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class DiagnosticsRollingLogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("np-log-rolls-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SupportLogFileResolver Resolver() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:SupportLog:Path"] = Path.Combine(_root, "support-.log"),
        }).Build(), new StubEnvironment());

    private string Segment(DateOnly date, string suffix, string text)
    {
        var path = Path.Combine(_root, $"support-{date:yyyyMMdd}{suffix}.log");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Resolver_ReturnsOnlyExactDaySegments_InNumericOrder()
    {
        var day = new DateOnly(2026, 1, 15);
        var thousand = Segment(day, "_1000", "last");
        var ten = Segment(day, "_010", "ten");
        var two = Segment(day, "_002", "two");
        var first = Segment(day, "", "first");
        Segment(day, "_invalid", "unrelated");
        Segment(day, "9", "unrelated");
        Segment(day.AddDays(1), "", "tomorrow");

        Resolver().GetFilesForDate(day).Should().Equal(first, two, ten, thousand);
    }

    [Fact]
    public async Task TailAndDownload_IncludeRolledSegments_PreservePlainText()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstText = string.Concat(Enumerable.Range(0, 2000).Select(i => $"line-{i}\n"));
        var first = Segment(today, "", firstText);
        var last = Segment(today, "_001", "rolled-one\nrolled-two\n");
        using var db = TestDbFactory.Create();
        var controller = new DiagnosticsController(Resolver(), db,
            NullLogger<DiagnosticsController>.Instance, NoopAuditWriter.Instance);

        var tail = (SupportLogTailResponse)((OkObjectResult)controller.Tail(4).Result!).Value!;
        tail.File.Should().Be(Path.GetFileName(last));
        tail.Lines.Should().Equal("line-1998", "line-1999", "rolled-one", "rolled-two");
        var capped = (SupportLogTailResponse)((OkObjectResult)controller.Tail(99999).Result!).Value!;
        capped.Lines.Should().HaveCount(1000);

        var download = (FileStreamResult)await controller.Download(today.ToString("yyyy-MM-dd"));
        download.ContentType.Should().Be("text/plain");
        download.FileDownloadName.Should().Be(Path.GetFileName(first));
        using var reader = new StreamReader(download.FileStream);
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).Should()
            .Be(firstText + "rolled-one\nrolled-two\n");
    }

    [Fact]
    public void Tail_NewerSegmentHasEnoughLines_DoesNotOpenLockedOlderSegment()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var oldest = Segment(today, "", "old\n");
        Segment(today, "_001", "one\ntwo\nthree\n");
        using var locked = new FileStream(oldest, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var db = TestDbFactory.Create();
        var controller = new DiagnosticsController(Resolver(), db,
            NullLogger<DiagnosticsController>.Instance, NoopAuditWriter.Instance);

        var tail = (SupportLogTailResponse)((OkObjectResult)controller.Tail(2).Result!).Value!;

        tail.Lines.Should().Equal("two", "three");
    }

    [Fact]
    public async Task ReadStream_CancellationAndDisposal_ReleaseFile_AndNeverOpenNextEarly()
    {
        var day = new DateOnly(2026, 1, 15);
        var first = Segment(day, "", "abc");
        var next = Segment(day, "_001", "def");
        var stream = new SupportLogReadStream([first, next]);
        try
        {
            (await stream.ReadAsync(new byte[1], TestContext.Current.CancellationToken)).Should().Be(1);
            using (new FileStream(next, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            Func<Task> read = async () => await stream.ReadAsync(new byte[1], cancelled.Token);
            await read.Should().ThrowAsync<OperationCanceledException>();
        }
        finally { await stream.DisposeAsync(); }

        using (new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Action disposedRead = () => stream.Read(new byte[1], 0, 1);
        disposedRead.Should().Throw<ObjectDisposedException>();
    }
}

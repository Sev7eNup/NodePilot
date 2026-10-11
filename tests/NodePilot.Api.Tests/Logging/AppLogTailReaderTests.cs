using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Diagnostics;
using NodePilot.Api.Tests.TestSupport;
using Serilog.Events;
using Xunit;
using static NodePilot.Api.Tests.Logging.AppLogTestLines;

namespace NodePilot.Api.Tests.Logging;

public sealed class AppLogTailReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 13, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateTempSubdirectory("np-app-log-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AppLogFileResolver Resolver() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:File:Path"] = Path.Combine(_root, "nodepilot-.log"),
        }).Build(), new StubEnvironment(_root));

    private static string Line(DateTimeOffset at, string message, LogEventLevel level = LogEventLevel.Information)
        => CmTrace(Event(level, message, at)).Single();

    private string Segment(DateOnly date, string suffix, IEnumerable<string> lines, bool finalNewline = true)
    {
        var path = Path.Combine(_root, $"nodepilot-{date:yyyyMMdd}{suffix}.log");
        var text = string.Join("\r\n", lines) + (finalNewline ? "\r\n" : "");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static AppLogScan Scan(IAppLogFileResolver files, DateTimeOffset since,
        long budget = AppLogTailReader.DefaultBudgetBytes)
        => AppLogTailReader.Scan(files, since, Now, budget, TimeZoneInfo.Utc);

    [Fact]
    public void Scan_EntryOlderThanSinceWasSeen_WindowIsComplete()
    {
        Segment(new DateOnly(2026, 10, 12), "", [
            Line(Now.AddHours(-5), "old"), Line(Now.AddHours(-1), "recent")]);

        var scan = Scan(Resolver(), Now.AddHours(-2));

        scan.Complete.Should().BeTrue();
        scan.CoverageStart.Should().Be(Now.AddHours(-2));
        scan.Entries.Select(e => e.Message).Should().Equal("recent", "old");
    }

    [Fact]
    public void Scan_FirstSegmentOfTheBoundaryDayReadFromItsStart_WindowIsComplete()
    {
        Segment(new DateOnly(2026, 10, 12), "", [Line(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero), "boot")]);

        var scan = Scan(Resolver(), new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

        scan.Complete.Should().BeTrue("nothing can precede the first segment of that day");
    }

    [Fact]
    public void Scan_BoundaryDayHasNoFile_WindowIsNotCompleteAndCoverageStartsAtTheOldestEntry()
    {
        var oldest = Now.AddHours(-3);
        Segment(new DateOnly(2026, 10, 12), "", [Line(oldest, "first"), Line(Now.AddHours(-1), "second")]);

        var scan = Scan(Resolver(), Now.AddHours(-24));

        scan.Complete.Should().BeFalse();
        scan.CoverageStart.Should().Be(oldest);
    }

    [Fact]
    public void Scan_NoFiles_IsIncompleteWithoutCoverage()
    {
        var scan = Scan(Resolver(), Now.AddHours(-24));

        scan.Complete.Should().BeFalse();
        scan.CoverageStart.Should().BeNull();
        scan.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Scan_BudgetExhausted_ReportsItAndNeverTurnsFragmentsIntoEntries()
    {
        var lines = Enumerable.Range(0, 200)
            .Select(i => Line(Now.AddMinutes(-300 + i), $"entry {i:000} with umlauts äöü €"))
            .ToArray();
        var path = Segment(new DateOnly(2026, 10, 12), "", lines);

        // Sweeping the budget moves the window start through every byte of every line, including
        // the middle of the multi-byte characters.
        for (long budget = 600; budget < 1500; budget += 7)
        {
            var scan = Scan(Resolver(), Now.AddHours(-24), budget);

            scan.BudgetExhausted.Should().BeTrue();
            scan.Complete.Should().BeFalse();
            scan.Entries.Should().NotBeEmpty();
            scan.Entries.Should().OnlyContain(e => e.Message.Contains("with umlauts äöü €") && !e.Message.Contains('�'));
            scan.BytesScanned.Should().BeLessThanOrEqualTo(budget);
            scan.Entries[0].Message.Should().Contain("entry 199");
        }
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Scan_LastLineWithoutLineBreak_IsStillBeingWrittenAndIgnored()
    {
        Segment(new DateOnly(2026, 10, 12), "", [
            Line(Now.AddMinutes(-10), "complete"),
            Line(Now.AddMinutes(-5), "half written").Substring(0, 30)], finalNewline: false);

        var scan = Scan(Resolver(), Now.AddHours(-1));

        scan.Entries.Should().ContainSingle().Which.Message.Should().Be("complete");
        scan.UnparsedLines.Should().Be(0);
    }

    [Fact]
    public void Scan_SegmentRemovedBetweenListingAndOpening_IsSkippedAndTheWindowIsNotComplete()
    {
        var resolver = new ListedButMissingResolver(Path.Combine(_root, "nodepilot-20261012.log"));

        var scan = Scan(resolver, Now.AddHours(-1));

        scan.FilesSkipped.Should().Be(1);
        scan.Complete.Should().BeFalse();
    }

    [Fact]
    public void Scan_SizeRolloverAndDayBoundary_AreMergedNewestFirst()
    {
        var yesterday = new DateOnly(2026, 10, 11);
        var today = new DateOnly(2026, 10, 12);
        var late = new DateTimeOffset(2026, 10, 11, 23, 59, 59, TimeSpan.Zero);
        Segment(yesterday, "", [Line(late.AddHours(-1), "y-first"), Line(late, "y-last")]);
        Segment(today, "", [Line(new DateTimeOffset(2026, 10, 12, 0, 0, 5, TimeSpan.Zero), "t-first")]);
        Segment(today, "_001", [Line(new DateTimeOffset(2026, 10, 12, 0, 0, 9, TimeSpan.Zero), "t-rolled")]);

        var scan = Scan(Resolver(), new DateTimeOffset(2026, 10, 11, 23, 0, 0, TimeSpan.Zero));

        scan.Entries.Select(e => e.Message).Should().Equal("t-rolled", "t-first", "y-last", "y-first");
        scan.Complete.Should().BeTrue();
    }

    [Fact]
    public void Scan_EntryRolledIntoANewSegmentMidEvent_DropsTheOrphanedContinuation()
    {
        Segment(new DateOnly(2026, 10, 12), "_001", [
            "   at NodePilot.Engine.Foo.Bar()",
            Line(Now.AddMinutes(-5), "after the roll")]);

        var scan = Scan(Resolver(), Now.AddHours(-1));

        scan.Entries.Should().ContainSingle().Which.Message.Should().Be("after the roll");
        scan.FragmentsDropped.Should().Be(1);
    }

    private sealed class ListedButMissingResolver(string path) : IAppLogFileResolver
    {
        public IReadOnlyList<string> GetFilesForDate(DateOnly date)
            => date == new DateOnly(2026, 10, 12) ? [path] : [];
    }
}

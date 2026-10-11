using System.Text;

namespace NodePilot.Api.Diagnostics;

internal sealed record AppLogScan(
    IReadOnlyList<AppLogEntry> Entries,
    DateTimeOffset? CoverageStart,
    bool Complete,
    bool BudgetExhausted,
    long BytesScanned,
    int FilesSkipped,
    int FragmentsDropped,
    int UnparsedLines);

/// <summary>
/// Reads the application log newest-first within a byte budget and reports how much of the
/// requested window it actually saw. A window that is not fully covered is never presented as complete.
/// </summary>
internal static class AppLogTailReader
{
    public const long DefaultBudgetBytes = 8L * 1024 * 1024;

    public static AppLogScan Scan(
        IAppLogFileResolver files, DateTimeOffset since, DateTimeOffset now,
        long budgetBytes, TimeZoneInfo zone)
    {
        var sinceLocal = TimeZoneInfo.ConvertTime(since, zone);
        var firstDate = DateOnly.FromDateTime(sinceLocal.DateTime);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        var entries = new List<AppLogEntry>();
        long scanned = 0;
        int skipped = 0, fragments = 0, unparsed = 0;
        var complete = false;
        var exhausted = false;

        for (; date >= firstDate && !complete && !exhausted; date = date.AddDays(-1))
        {
            var segments = files.GetFilesForDate(date);
            for (var i = segments.Count - 1; i >= 0 && !complete && !exhausted; i--)
            {
                var read = ReadTail(segments[i], budgetBytes - scanned);
                if (read is null) { skipped++; continue; }

                scanned += read.Bytes;
                exhausted = read.Truncated;
                var parsed = AppLogParser.Parse(read.Lines, zone);
                fragments += parsed.FragmentsDropped;
                unparsed += parsed.UnparsedLines;
                entries.AddRange(parsed.Entries);

                // The window is covered once an entry older than `since` was seen, or the first
                // segment of a date on or before the boundary was read from its start.
                if (parsed.Entries.Any(e => e.Timestamp < since)
                    || (!read.Truncated && i == 0 && date <= firstDate))
                    complete = true;
            }
        }

        // Serilog writes in timestamp order per file; the sort keeps day and segment seams right.
        var ordered = entries.OrderByDescending(e => e.Timestamp).ToList();
        DateTimeOffset? coverage = complete ? since : ordered.Count > 0 ? ordered[^1].Timestamp : null;
        return new AppLogScan(ordered, coverage, complete, exhausted, scanned, skipped, fragments, unparsed);
    }

    private sealed record TailRead(IReadOnlyList<string> Lines, long Bytes, bool Truncated);

    /// <summary>
    /// Reads at most <paramref name="budget"/> bytes from the end of the file as it was when opened.
    /// Cuts lines on the 0x0A byte, which never occurs inside a UTF-8 multi-byte sequence. A line cut
    /// by the budget start and an unterminated last line (still being written) are left out.
    /// Returns null when the file cannot be read, for example because retention deleted it.
    /// </summary>
    private static TailRead? ReadTail(string path, long budget)
    {
        if (budget <= 0) return new TailRead([], 0, true);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var truncated = length > budget;
            var start = truncated ? length - budget : 0;
            // One extra leading byte tells whether the window starts on a line boundary.
            var readFrom = start > 0 ? start - 1 : 0;
            var buffer = new byte[length - readFrom];
            stream.Seek(readFrom, SeekOrigin.Begin);
            stream.ReadExactly(buffer);

            var offset = 0;
            if (start > 0)
            {
                // Window starts mid-line unless the preceding byte is a line break.
                if (buffer[0] != (byte)'\n')
                {
                    var firstBreak = Array.IndexOf(buffer, (byte)'\n', 1);
                    offset = firstBreak < 0 ? buffer.Length : firstBreak + 1;
                }
                else offset = 1;
            }

            var end = buffer.Length;
            if (end > 0 && buffer[end - 1] != (byte)'\n')
            {
                var lastBreak = Array.LastIndexOf(buffer, (byte)'\n', end - 1);
                end = lastBreak < offset ? offset : lastBreak + 1;
            }

            var lines = new List<string>();
            var lineStart = offset;
            for (var i = offset; i < end; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                lines.Add(Encoding.UTF8.GetString(buffer, lineStart, i - lineStart));
                lineStart = i + 1;
            }
            return new TailRead(lines, length - start, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

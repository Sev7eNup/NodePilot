using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Diagnostics;

/// <summary>
/// Redacts a log line by line. <see cref="OutputRedactor"/> only sees one line at a time, so a PEM block
/// that spans lines (text format, raw exception text) is tracked here and replaced by one placeholder.
/// </summary>
internal sealed class LogLineRedactor(OutputRedactor redactor)
{
    private const int MaxPemLines = 2000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex PemBegin = new(
        @"-----BEGIN [A-Z ]+-----", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex PemEnd = new(
        @"-----END [A-Z ]+-----", RegexOptions.CultureInvariant, RegexTimeout);

    private bool _inPem;
    private int _pemLines;

    /// <summary>Returns the line to write, or null when the line belongs to a swallowed PEM block.</summary>
    public string? Process(string line)
    {
        if (_inPem)
        {
            if (PemEnd.Match(line) is { Success: true } end)
            {
                _inPem = false;
                var tail = line[(end.Index + end.Length)..];
                return tail.Length == 0 ? null : redactor.Redact(tail);
            }
            if (++_pemLines <= MaxPemLines) return null;
            _inPem = false;
            return redactor.Redact(line);
        }

        if (PemBegin.Match(line) is { Success: true } begin && !PemEnd.IsMatch(line[begin.Index..]))
        {
            _inPem = true;
            _pemLines = 0;
            return redactor.Redact(line[..begin.Index]) + OutputRedactor.Placeholder;
        }

        return redactor.Redact(line);
    }
}

/// <summary>Streams several log segments, in order, as one redacted text download with bounded memory.</summary>
internal sealed class RedactedLogFileResult(
    IReadOnlyList<string> paths, OutputRedactor redactor, string fileName) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        var ct = context.HttpContext.RequestAborted;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

        var state = new LogLineRedactor(redactor);
        await using var writer = new StreamWriter(response.Body, new UTF8Encoding(false), leaveOpen: true);
        foreach (var path in paths)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retention removed the segment after it was listed; the rest of the day is still exported.
                continue;
            }

            using (stream)
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (state.Process(line) is { } output)
                        await writer.WriteLineAsync(output.AsMemory(), ct);
                }
            }
        }
    }
}

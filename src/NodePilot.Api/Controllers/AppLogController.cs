using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Diagnostics;
using NodePilot.Api.Dtos;
using NodePilot.Core.Audit;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin-only view of the application log (<c>nodepilot-.log</c>): everything Serilog writes, in contrast
/// to the curated support log. The log is read from disk newest-first within a byte budget, so every
/// response says how much of the requested window it actually covers.
/// </summary>
[ApiController]
[Route("api/diagnostics/app-log")]
[Authorize(Roles = "Admin")]
public class AppLogController : ControllerBase
{
    private const int MaxTake = 1000;
    private const int MaxHours = 168;

    private readonly IAppLogFileResolver _files;
    private readonly OutputRedactor _redactor;
    private readonly IAuditWriter _audit;
    private readonly ILogger<AppLogController> _logger;
    private readonly TimeProvider _clock;

    public AppLogController(
        IAppLogFileResolver files,
        OutputRedactor redactor,
        IAuditWriter audit,
        ILogger<AppLogController> logger,
        TimeProvider? clock = null)
    {
        _files = files;
        _redactor = redactor;
        _audit = audit;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Entries of the last <paramref name="hours"/> hours, newest first. <c>minLevel</c> and <c>q</c> filter
    /// before <c>take</c> applies; <c>summary</c> always covers every scanned entry of the window.
    /// </summary>
    [HttpGet]
    public ActionResult<AppLogResponse> Get(
        [FromQuery] int hours = 24,
        [FromQuery] string? minLevel = null,
        [FromQuery] string? q = null,
        [FromQuery] int take = 500)
    {
        hours = Math.Clamp(hours, 1, MaxHours);
        take = Math.Clamp(take, 1, MaxTake);

        var threshold = AppLogLevel.Verbose;
        if (!string.IsNullOrWhiteSpace(minLevel)
            && !Enum.TryParse(minLevel, ignoreCase: true, out threshold))
        {
            return BadRequest(new { code = "INVALID_MIN_LEVEL", message = "Expected one of: verbose, debug, information, warning, error, fatal." });
        }

        var now = _clock.GetUtcNow();
        var since = now.AddHours(-hours);

        AppLogScan scan;
        try
        {
            scan = AppLogTailReader.Scan(_files, since, now, AppLogTailReader.DefaultBudgetBytes, TimeZoneInfo.Local);
        }
        catch (Exception ex)
        {
            // Security: never echo the raw I/O exception, it leaks server-side paths and ACL details.
            var correlationId = HttpContext?.TraceIdentifier ?? System.Diagnostics.Activity.Current?.Id ?? Guid.NewGuid().ToString();
            _logger.LogWarning(ex, "Application log read failed (correlationId={CorrelationId})", correlationId);
            return StatusCode(500, new { code = "APP_LOG_READ_FAILED", message = "Failed to read the application log.", correlationId });
        }

        var inWindow = scan.Entries.Where(e => e.Timestamp >= since).ToList();
        var summary = new AppLogSummary(
            Errors: inWindow.Count(e => e.Level >= AppLogLevel.Error),
            Warnings: inWindow.Count(e => e.Level == AppLogLevel.Warning),
            Total: inWindow.Count);

        // Redact before the text search so a query cannot be used to probe for secret values.
        var needle = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        var matches = new List<AppLogEntryResponse>();
        var matched = 0;
        foreach (var entry in inWindow)
        {
            if (entry.Level < threshold) continue;
            var message = _redactor.Redact(entry.Message) ?? "";
            var details = _redactor.Redact(entry.Details);
            if (needle is not null
                && !message.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !entry.Source.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && details?.Contains(needle, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            matched++;
            if (matches.Count < take)
                matches.Add(new AppLogEntryResponse(entry.Timestamp, entry.Level.ToString(), entry.Source,
                    message, entry.Template, details, entry.Truncated, entry.Format));
        }

        return Ok(new AppLogResponse(
            new AppLogScanInfo(since, scan.CoverageStart, scan.Complete, scan.BudgetExhausted, scan.BytesScanned,
                scan.FilesSkipped, scan.FragmentsDropped, scan.UnparsedLines),
            summary,
            new AppLogResults(matched, matches.Count, matched > matches.Count),
            matches));
    }

    /// <summary>
    /// One day of the application log as a single redacted text file: every segment of the date in roll
    /// order. <c>date</c> is <c>yyyy-MM-dd</c>. 404 if the day has no files.
    /// </summary>
    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string date, CancellationToken ct = default)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate))
        {
            return BadRequest(new { code = "INVALID_DATE", message = "Expected yyyy-MM-dd" });
        }

        var segments = _files.GetFilesForDate(parsedDate);
        if (segments.Count == 0) return NotFound();

        long sourceBytes = 0;
        foreach (var path in segments)
        {
            try { sourceBytes += new FileInfo(path).Length; }
            catch (IOException) { /* removed by retention while listing; the export skips it too */ }
        }

        await _audit.LogAsync(
            AuditActions.AppLogDownloaded,
            "AppLog",
            null,
            AuditDetails.Json(
                ("date", date),
                ("segments", segments.Count),
                ("sourceBytes", sourceBytes),
                ("redacted", true)),
            ct);
        return new RedactedLogFileResult(segments, _redactor, $"nodepilot-app-{date}.redacted.log");
    }
}

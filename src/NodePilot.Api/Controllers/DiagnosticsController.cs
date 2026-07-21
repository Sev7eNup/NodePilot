using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Diagnostics;
using NodePilot.Api.Dtos;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin-only support-log surface. Powers the support-log tab in the system settings:
/// live tail (polling endpoint) plus daily-file download. Deliberately kept small — the
/// log file itself is the source of truth, this controller is just a bridge for the browser.
/// </summary>
[ApiController]
[Route("api/diagnostics")]
[Authorize(Roles = "Admin")]
public class DiagnosticsController : ControllerBase
{
    // Hard cap on the tail response, so a 10 MB daily log file is never serialized all at
    // once. UI default is 200 lines.
    private const int MaxTailLines = 1000;
    // Hard cap on the structured event query — mirrors AuditController, protects the DB
    // from a `?take=999999` against a large backlog.
    private const int MaxEventsTake = 500;

    private readonly ISupportLogFileResolver _resolver;
    private readonly NodePilotDbContext _db;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        ISupportLogFileResolver resolver,
        NodePilotDbContext db,
        ILogger<DiagnosticsController> logger)
    {
        _resolver = resolver;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// The last N lines of today's support-log file. Returns an empty array if the file
    /// doesn't exist yet (nothing has been logged today) or support-log is disabled.
    /// <c>lines</c> is capped at <see cref="MaxTailLines"/>.
    /// </summary>
    [HttpGet("support-log")]
    public ActionResult<SupportLogTailResponse> Tail([FromQuery] int lines = 200)
    {
        if (lines < 1) lines = 1;
        if (lines > MaxTailLines) lines = MaxTailLines;

        var file = _resolver.GetCurrentDayFile();
        if (file is null)
        {
            return Ok(new SupportLogTailResponse(
                File: null,
                LineCount: 0,
                Lines: Array.Empty<string>()));
        }

        try
        {
            // FileShare.ReadWrite: Serilog keeps the file open for writing while we read it.
            // Without this share flag, the Open call would fail with an "in use" IOException.
            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = new List<string>(capacity: Math.Min(lines, 1024));
            string? l;
            // We pragmatically read the whole file — the 10 MB cap keeps this cheap enough.
            // Larger files would warrant a reverse scan from the end, but V1 doesn't need that.
            while ((l = reader.ReadLine()) is not null) all.Add(l);

            var tail = all.Count <= lines
                ? all
                : all.GetRange(all.Count - lines, lines);

            return Ok(new SupportLogTailResponse(
                File: Path.GetFileName(file),
                LineCount: tail.Count,
                Lines: tail));
        }
        catch (Exception ex)
        {
            // Security: never echo the raw I/O exception — it leaks the server-side support-log
            // path / ACL details. Detail stays in the server log; caller gets a correlation id.
            var correlationId = HttpContext?.TraceIdentifier ?? System.Diagnostics.Activity.Current?.Id ?? Guid.NewGuid().ToString();
            _logger.LogWarning(ex, "Support-Log tail failed for {File} (correlationId={CorrelationId})", file, correlationId);
            return StatusCode(500, new { code = "SUPPORT_LOG_READ_FAILED", message = "Failed to read the support log.", correlationId });
        }
    }

    /// <summary>
    /// Streams a complete daily log file as a download. <c>date</c> is in
    /// <c>yyyy-MM-dd</c> format (e.g. <c>2026-05-15</c>). 404 if the file doesn't exist.
    /// </summary>
    [HttpGet("support-log/download")]
    public IActionResult Download([FromQuery] string date)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate))
        {
            return BadRequest(new { code = "INVALID_DATE", message = "Expected yyyy-MM-dd" });
        }

        var file = _resolver.GetFileForDate(parsedDate);
        if (file is null) return NotFound();

        var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return File(stream, "text/plain", Path.GetFileName(file));
    }

    /// <summary>
    /// Allowlist of sortable columns. This stops a user from triggering an ad-hoc index
    /// miss by sorting on, say, "Message" (varchar 8000). Unknown values fall back to
    /// Timestamp — a silent default rather than a 400, so a UI typo never kills the
    /// whole request.
    /// </summary>
    private static readonly Dictionary<string, string> SortColumnAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        ["timestamp"] = "Timestamp",
        ["level"] = "Level",
        ["eventType"] = "EventType",
        // Status is derived client-side from EventType (deriveStatus in the React table).
        // The backend has no Status column, so we sort alphabetically on EventType instead.
        // Effect in the UI: matching statuses land together (an EXECUTION_SUCCEEDED block,
        // then an EXECUTION_FAILED block, etc.) — good enough for the "group by status" use case.
        ["status"] = "EventType",
        ["workflowName"] = "WorkflowName",
        ["executionShort"] = "ExecutionShort",
        ["stepLabel"] = "StepLabel",
        ["activityType"] = "ActivityType",
        ["userName"] = "UserName",
        // Message: varchar 8000, no index — an operator-triggered sort is O(N log N) over
        // at most 500 rows on the filtered page. Acceptable.
        ["message"] = "Message",
    };

    /// <summary>
    /// Structured query against the <c>SupportEvents</c> DB table — the backend for the
    /// enterprise-grade table in the frontend. Filters are AND-combined. Default sort order
    /// is Timestamp DESC, Id DESC; via <paramref name="sortBy"/>+<paramref name="sortDir"/>
    /// operators can re-sort by clicking a column. Cursor pagination via afterTs+afterId only
    /// works for the default sort order — for a custom sort we don't return a nextCursor
    /// (the user should refine the filters instead of paging through everything).
    /// </summary>
    [HttpGet("support-events")]
    public async Task<ActionResult<SupportEventPageResponse>> Events(
        [FromQuery] DateTime? since,
        [FromQuery] DateTime? until,
        [FromQuery] int? level,
        [FromQuery] string? eventType,
        [FromQuery] Guid? workflowId,
        [FromQuery] string? workflowName,
        [FromQuery] Guid? executionId,
        [FromQuery] string? stepId,
        [FromQuery] string? activityType,
        [FromQuery] string? username,
        [FromQuery] string? q,
        [FromQuery] DateTime? afterTs = null,
        [FromQuery] Guid? afterId = null,
        // sortBy + sortDir are placed at the end of the parameter list so that existing
        // positional callers (tests call with 11+ positional nulls) keep working unmodified.
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxEventsTake);

        var query = _db.SupportEvents.AsNoTracking().AsQueryable();
        if (since.HasValue)                       query = query.Where(e => e.Timestamp >= since.Value);
        if (until.HasValue)                       query = query.Where(e => e.Timestamp < until.Value);
        if (level.HasValue)                       query = query.Where(e => e.Level >= level.Value);
        if (!string.IsNullOrEmpty(eventType))     query = query.Where(e => e.EventType == eventType);
        if (workflowId.HasValue)                  query = query.Where(e => e.WorkflowId == workflowId);
        // Workflow name and username are human-readable and operators typically type a partial
        // word ("delay" should match "[TestSuite] delay - Various Durations"). ILike (Postgres)
        // is case-insensitive; on SQL Server EF maps this to LIKE, which is also case-insensitive
        // under the default collation.
        if (!string.IsNullOrEmpty(workflowName))
        {
            // Substring + case-insensitive: ToLower().Contains is translated by EF to
            // `LOWER(col) LIKE '%needle%'` — this works identically on Postgres and SQL Server
            // (ILike would be Postgres-only and would break SQL Server deployments).
            var needle = workflowName.ToLowerInvariant();
            query = query.Where(e => e.WorkflowName != null && e.WorkflowName.ToLower().Contains(needle));
        }
        if (executionId.HasValue)                 query = query.Where(e => e.ExecutionId == executionId);
        if (!string.IsNullOrEmpty(stepId))        query = query.Where(e => e.StepId == stepId);
        if (!string.IsNullOrEmpty(activityType))  query = query.Where(e => e.ActivityType == activityType);
        if (!string.IsNullOrEmpty(username))
        {
            var needle = username.ToLowerInvariant();
            query = query.Where(e => e.UserName != null && e.UserName.ToLower().Contains(needle));
        }
        if (!string.IsNullOrEmpty(q))
        {
            // Provider-agnostic full-text search: ToLower().Contains is translated by EF to
            // `LOWER(col) LIKE '%needle%'`. Works on both Postgres and SQL Server, neither needing
            // an extra index — for a much bigger table a functional index on LOWER(Message) would
            // be worth adding, but at today's size (roughly thousands of rows per day) a plain
            // scan is fine.
            var needle = q.ToLowerInvariant();
            query = query.Where(e => e.Message.ToLower().Contains(needle));
        }

        // Sort resolution: SortColumnAllowlist is the whitelist against ad-hoc columns. An
        // unrecognized sortBy falls back to "timestamp" (a silent default — a UI typo shouldn't
        // kill the whole request). The default sort (Timestamp DESC, Id DESC) is the only case
        // where cursor pagination is semantically correct (the Id tie-breaker guarantees a
        // deterministic order). For a custom sort we skip the cursor: the frontend's "more
        // available" hint tells the operator to filter instead.
        var resolvedSortKey = !string.IsNullOrEmpty(sortBy) && SortColumnAllowlist.ContainsKey(sortBy)
            ? sortBy.ToLowerInvariant()
            : "timestamp";
        var isDescending = !string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);
        var isDefaultSort = resolvedSortKey == "timestamp";

        if (isDefaultSort && afterTs.HasValue && afterId.HasValue)
        {
            var ts = afterTs.Value;
            var id = afterId.Value;
            if (isDescending)
                query = query.Where(e => e.Timestamp < ts || (e.Timestamp == ts && e.Id.CompareTo(id) < 0));
            else
                query = query.Where(e => e.Timestamp > ts || (e.Timestamp == ts && e.Id.CompareTo(id) > 0));
        }

        query = ApplyOrderBy(query, resolvedSortKey, isDescending);

        var rows = await query
            .Take(take + 1)
            .Select(e => new SupportEventResponse(
                e.Id, e.Timestamp, e.Level, e.EventType, e.Message,
                e.WorkflowId, e.WorkflowName, e.ExecutionId, e.ExecutionShort,
                e.StepId, e.StepLabel, e.ActivityType,
                e.UserName, e.UserId, e.TraceId, e.SpanId, e.PropertiesJson))
            .ToListAsync(ct);

        SupportEventCursor? nextCursor = null;
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
            // Cursor only for the default sort order — otherwise we'd have no deterministic
            // tie-breaker (e.g. two rows with an identical Level). For a custom sort we instead
            // signal "more available" via the `HasMore` flag, and the operator has to filter.
            if (isDefaultSort)
            {
                var last = rows[^1];
                nextCursor = new SupportEventCursor(last.Timestamp, last.Id);
            }
        }

        return Ok(new SupportEventPageResponse(rows, nextCursor) { HasMore = hasMore });
    }

    /// <summary>
    /// Applies an ORDER BY against the allowlisted columns. Adding a secondary sort on Id
    /// (descending, matching the primary direction) guarantees a deterministic order even
    /// when values in the primary column are equal — otherwise two consecutive calls could
    /// return rows in a different order and cursor pagination would break.
    /// </summary>
    private static IQueryable<NodePilot.Core.Models.SupportEvent> ApplyOrderBy(
        IQueryable<NodePilot.Core.Models.SupportEvent> query, string sortKey, bool descending)
    {
        IOrderedQueryable<NodePilot.Core.Models.SupportEvent> ordered = sortKey switch
        {
            "level"          => descending ? query.OrderByDescending(e => e.Level)          : query.OrderBy(e => e.Level),
            "eventtype"      => descending ? query.OrderByDescending(e => e.EventType)      : query.OrderBy(e => e.EventType),
            // Status is derived client-side from EventType — we sort server-side on EventType
            // instead, which brings matching statuses into adjacent rows (all Succeeded grouped
            // together, all Failed grouped together) without persisting an extra column.
            "status"         => descending ? query.OrderByDescending(e => e.EventType)      : query.OrderBy(e => e.EventType),
            "workflowname"   => descending ? query.OrderByDescending(e => e.WorkflowName)   : query.OrderBy(e => e.WorkflowName),
            "executionshort" => descending ? query.OrderByDescending(e => e.ExecutionShort) : query.OrderBy(e => e.ExecutionShort),
            "steplabel"      => descending ? query.OrderByDescending(e => e.StepLabel)      : query.OrderBy(e => e.StepLabel),
            "activitytype"   => descending ? query.OrderByDescending(e => e.ActivityType)   : query.OrderBy(e => e.ActivityType),
            "username"       => descending ? query.OrderByDescending(e => e.UserName)       : query.OrderBy(e => e.UserName),
            // Message: varchar 8000, no index — sorting is O(N log N) over the filtered page
            // (at most 500 rows). The practical use case is "sort alphabetically by wording to
            // find duplicates".
            "message"        => descending ? query.OrderByDescending(e => e.Message)        : query.OrderBy(e => e.Message),
            _                => descending ? query.OrderByDescending(e => e.Timestamp)      : query.OrderBy(e => e.Timestamp),
        };
        return descending ? ordered.ThenByDescending(e => e.Id) : ordered.ThenBy(e => e.Id);
    }

    /// <summary>
    /// Streaming export of the same query as CSV or NDJSON. No take-cap; since/until are
    /// the paging mechanism here (mirrors AuditController).
    /// </summary>
    [HttpGet("support-events/export")]
    public async Task ExportEvents(
        [FromQuery] string format = "csv",
        [FromQuery] DateTime? since = null,
        [FromQuery] DateTime? until = null,
        [FromQuery] int? level = null,
        [FromQuery] string? eventType = null,
        [FromQuery] Guid? workflowId = null,
        [FromQuery] string? workflowName = null,
        [FromQuery] Guid? executionId = null,
        [FromQuery] string? stepId = null,
        [FromQuery] string? activityType = null,
        [FromQuery] string? username = null,
        [FromQuery] string? q = null,
        CancellationToken ct = default)
    {
        var isNdjson = string.Equals(format, "ndjson", StringComparison.OrdinalIgnoreCase);
        if (!isNdjson && !string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync($"Unsupported format '{format}'. Use 'csv' or 'ndjson'.", ct);
            return;
        }

        var query = _db.SupportEvents.AsNoTracking().AsQueryable();
        if (since.HasValue)                       query = query.Where(e => e.Timestamp >= since.Value);
        if (until.HasValue)                       query = query.Where(e => e.Timestamp < until.Value);
        if (level.HasValue)                       query = query.Where(e => e.Level >= level.Value);
        if (!string.IsNullOrEmpty(eventType))     query = query.Where(e => e.EventType == eventType);
        if (workflowId.HasValue)                  query = query.Where(e => e.WorkflowId == workflowId);
        if (!string.IsNullOrEmpty(workflowName))
        {
            // Substring + case-insensitive: ToLower().Contains is translated by EF to
            // `LOWER(col) LIKE '%needle%'` — this works identically on Postgres and SQL Server
            // (ILike would be Postgres-only and would break SQL Server deployments).
            var needle = workflowName.ToLowerInvariant();
            query = query.Where(e => e.WorkflowName != null && e.WorkflowName.ToLower().Contains(needle));
        }
        if (executionId.HasValue)                 query = query.Where(e => e.ExecutionId == executionId);
        if (!string.IsNullOrEmpty(stepId))        query = query.Where(e => e.StepId == stepId);
        if (!string.IsNullOrEmpty(activityType))  query = query.Where(e => e.ActivityType == activityType);
        if (!string.IsNullOrEmpty(username))
        {
            var needle = username.ToLowerInvariant();
            query = query.Where(e => e.UserName != null && e.UserName.ToLower().Contains(needle));
        }
        if (!string.IsNullOrEmpty(q))
        {
            var needle = q.ToLowerInvariant();
            query = query.Where(e => e.Message.ToLower().Contains(needle));
        }
        query = query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id);

        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var filename = $"nodepilot-support-events-{ts}.{(isNdjson ? "ndjson" : "csv")}";
        Response.ContentType = isNdjson ? "application/x-ndjson" : "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!isNdjson)
        {
            await writer.WriteLineAsync("Id,Timestamp,Level,EventType,WorkflowName,ExecutionShort,StepLabel,ActivityType,UserName,Message,PropertiesJson");
        }

        var batch = 0;
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (isNdjson)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    id = row.Id,
                    timestamp = row.Timestamp,
                    level = row.Level,
                    eventType = row.EventType,
                    workflowId = row.WorkflowId,
                    workflowName = row.WorkflowName,
                    executionId = row.ExecutionId,
                    executionShort = row.ExecutionShort,
                    stepId = row.StepId,
                    stepLabel = row.StepLabel,
                    activityType = row.ActivityType,
                    userName = row.UserName,
                    userId = row.UserId,
                    traceId = row.TraceId,
                    spanId = row.SpanId,
                    message = row.Message,
                    properties = row.PropertiesJson,
                }));
            }
            else
            {
                var sb = new StringBuilder(256);
                sb.Append(row.Id).Append(',')
                  .Append(row.Timestamp.ToString("O")).Append(',')
                  .Append(row.Level).Append(',');
                CsvField(sb, row.EventType); sb.Append(',');
                CsvField(sb, row.WorkflowName); sb.Append(',');
                CsvField(sb, row.ExecutionShort); sb.Append(',');
                CsvField(sb, row.StepLabel); sb.Append(',');
                CsvField(sb, row.ActivityType); sb.Append(',');
                CsvField(sb, row.UserName); sb.Append(',');
                CsvField(sb, row.Message); sb.Append(',');
                CsvField(sb, row.PropertiesJson);
                await writer.WriteLineAsync(sb);
            }

            if (++batch % 500 == 0)
                await writer.FlushAsync(ct);
        }

        await writer.FlushAsync(ct);
    }

    private static void CsvField(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var needsQuoting = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuoting) { sb.Append(value); return; }
        sb.Append('"');
        foreach (var c in value)
        {
            if (c == '"') sb.Append("\"\"");
            else sb.Append(c);
        }
        sb.Append('"');
    }
}


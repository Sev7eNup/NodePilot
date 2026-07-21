using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Dtos;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Read-only query endpoint for the compliance audit log. Admin-only — the whole purpose
/// of the log is to hold people accountable, so broader read-access would defeat the
/// deterrence effect (anyone could spot their own forbidden action and delete it before
/// review … except there's no DELETE endpoint, and there never will be).
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
[EnableRateLimiting("audit")]
public class AuditController : ControllerBase
{
    // Hard cap so a forgotten `?take=999999` cannot degrade the DB. Operators paginate
    // with `since`/`until` filters when they need to reach further back.
    private const int MaxTake = 500;

    private readonly NodePilotDbContext _db;

    public AuditController(NodePilotDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns audit entries newest-first. All filters are optional and AND-combined.
    /// Cursor pagination via <paramref name="afterTs"/> + <paramref name="afterId"/>:
    /// the response's <c>nextCursor</c> object can be passed back verbatim to fetch the
    /// next page. The cursor pins both the timestamp AND id so rows that share a
    /// timestamp (common at ingest bursts) paginate deterministically — a pure
    /// timestamp cursor would skip or duplicate ties.
    /// </summary>
    /// <param name="action">Exact action code (e.g. <c>WORKFLOW_UPDATED</c>). Case-sensitive.</param>
    /// <param name="resourceType">Exact resource label (<c>Workflow</c>, <c>Machine</c>, ...).</param>
    /// <param name="resourceId">Exact resource id.</param>
    /// <param name="userId">Exact actor id.</param>
    /// <param name="ipAddress">Exact remote IP (string). Useful for forensic timelines.</param>
    /// <param name="since">Only entries with Timestamp &gt;= since.</param>
    /// <param name="until">Only entries with Timestamp &lt; until.</param>
    /// <param name="afterTs">Cursor: Timestamp of the last row from the previous page.</param>
    /// <param name="afterId">Cursor: Id of the last row from the previous page. Required when afterTs is set.</param>
    /// <param name="take">Page size (default 100, max 500).</param>
    /// <param name="ct">Cancellation token forwarded to the EF query.</param>
    [HttpGet]
    public async Task<ActionResult<AuditPageResponse>> GetAll(
        [FromQuery] string? action,
        [FromQuery] string? resourceType,
        [FromQuery] Guid? resourceId,
        [FromQuery] Guid? userId,
        [FromQuery] string? ipAddress,
        [FromQuery] DateTime? since,
        [FromQuery] DateTime? until,
        [FromQuery] DateTime? afterTs,
        [FromQuery] Guid? afterId,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, MaxTake);

        var query = _db.AuditLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(action))       query = query.Where(a => a.Action == action);
        if (!string.IsNullOrEmpty(resourceType)) query = query.Where(a => a.ResourceType == resourceType);
        if (resourceId.HasValue)                 query = query.Where(a => a.ResourceId == resourceId);
        if (userId.HasValue)                     query = query.Where(a => a.UserId == userId);
        if (!string.IsNullOrEmpty(ipAddress))    query = query.Where(a => a.IpAddress == ipAddress);
        if (since.HasValue)                      query = query.Where(a => a.Timestamp >= since.Value);
        if (until.HasValue)                      query = query.Where(a => a.Timestamp < until.Value);

        // Cursor: "give me rows strictly older than (afterTs, afterId)". The Id half is what
        // disambiguates rows that share a timestamp — without it the next page would skip or
        // double-count entries that landed in the same millisecond. afterId alone is invalid:
        // ids don't have an inherent order across the table, only within a timestamp tie.
        if (afterTs.HasValue && afterId.HasValue)
        {
            var ts = afterTs.Value;
            var id = afterId.Value;
            query = query.Where(a => a.Timestamp < ts || (a.Timestamp == ts && a.Id.CompareTo(id) < 0));
        }

        // Probe one row beyond `take` so we can tell "this page is exactly full AND nothing
        // remains" apart from "full page AND more rows behind it". Setting the cursor based
        // on rows.Count == take alone produces a phantom Load-More on the exact-full last
        // page that returns zero rows.
        var rows = await query
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.Id)
            .Take(take + 1)
            .Select(a => new AuditEntryResponse(
                a.Id, a.Timestamp, a.UserId, a.Username, a.Action,
                a.ResourceType, a.ResourceId, a.Details, a.IpAddress))
            .ToListAsync(ct);

        AuditCursor? nextCursor = null;
        if (rows.Count > take)
        {
            // Drop the probe row before returning so the client only sees `take` results,
            // and pin the cursor to the last returned row (not the probe — that would
            // skip the probe row on the next request).
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = new AuditCursor(last.Timestamp, last.Id);
        }

        return Ok(new AuditPageResponse(rows, nextCursor));
    }

    /// <summary>
    /// Streaming bulk export — for compliance auditors who want "all activity in date range"
    /// as a single download. Unlike <see cref="GetAll"/> there's no <c>take</c> cap; the
    /// endpoint streams rows directly from the DB cursor to the response body so even a
    /// 500k-row pull doesn't materialize in memory. <paramref name="since"/>/<paramref name="until"/>
    /// are the only paging mechanism — operators are expected to use a date range, not a
    /// "fetch everything ever" call.
    /// <para>
    /// Two formats: <c>csv</c> (one row per line, RFC4180-style escaping; Details JSON is
    /// embedded as a single CSV field) and <c>ndjson</c> (one JSON object per line, ideal
    /// for piping into <c>jq</c> or SIEM ingestion pipelines).
    /// </para>
    /// </summary>
    [HttpGet("export")]
    public async Task ExportStream(
        [FromQuery] string format = "csv",
        [FromQuery] string? action = null,
        [FromQuery] string? resourceType = null,
        [FromQuery] Guid? resourceId = null,
        [FromQuery] Guid? userId = null,
        [FromQuery] string? ipAddress = null,
        [FromQuery] DateTime? since = null,
        [FromQuery] DateTime? until = null,
        CancellationToken ct = default)
    {
        var isNdjson = string.Equals(format, "ndjson", StringComparison.OrdinalIgnoreCase);
        if (!isNdjson && !string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync($"Unsupported format '{format}'. Use 'csv' or 'ndjson'.", ct);
            return;
        }

        var query = _db.AuditLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(action))       query = query.Where(a => a.Action == action);
        if (!string.IsNullOrEmpty(resourceType)) query = query.Where(a => a.ResourceType == resourceType);
        if (resourceId.HasValue)                 query = query.Where(a => a.ResourceId == resourceId);
        if (userId.HasValue)                     query = query.Where(a => a.UserId == userId);
        if (!string.IsNullOrEmpty(ipAddress))    query = query.Where(a => a.IpAddress == ipAddress);
        if (since.HasValue)                      query = query.Where(a => a.Timestamp >= since.Value);
        if (until.HasValue)                      query = query.Where(a => a.Timestamp < until.Value);
        query = query.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id);

        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var filename = $"nodepilot-audit-{ts}.{(isNdjson ? "ndjson" : "csv")}";
        Response.ContentType = isNdjson ? "application/x-ndjson" : "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{filename}\"";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!isNdjson)
        {
            await writer.WriteLineAsync("Id,Timestamp,UserId,Username,Action,ResourceType,ResourceId,IpAddress,Details");
        }

        // AsAsyncEnumerable streams rows directly off the data reader so the response body
        // starts flushing before the DB finishes scanning — a 500k-row pull never lives in
        // memory all at once. Periodic flushes keep the client connection alive on slow links.
        var batch = 0;
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
        {
            if (isNdjson)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    id = row.Id,
                    timestamp = row.Timestamp,
                    userId = row.UserId,
                    username = row.Username,
                    action = row.Action,
                    resourceType = row.ResourceType,
                    resourceId = row.ResourceId,
                    ipAddress = row.IpAddress,
                    details = row.Details,
                }));
            }
            else
            {
                var sb = new StringBuilder(256);
                sb.Append(row.Id).Append(',')
                  .Append(row.Timestamp.ToString("O")).Append(',')
                  .Append(row.UserId?.ToString() ?? "").Append(',');
                CsvField(sb, row.Username); sb.Append(',');
                CsvField(sb, row.Action); sb.Append(',');
                CsvField(sb, row.ResourceType); sb.Append(',');
                sb.Append(row.ResourceId?.ToString() ?? "").Append(',');
                CsvField(sb, row.IpAddress); sb.Append(',');
                CsvField(sb, row.Details);
                await writer.WriteLineAsync(sb);
            }

            if (++batch % 500 == 0)
            {
                await writer.FlushAsync(ct);
            }
        }

        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// RFC 4180 minimal CSV escaping: only quote when the value contains a comma, quote,
    /// or newline; double internal quotes. NULL and empty render as empty (no quotes).
    /// </summary>
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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Dtos;
using Quartz;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Read-only utilities for trigger configuration. Used by the designer to preview
/// schedule fires before saving a workflow (or while editing the cron expression).
/// </summary>
[ApiController]
[Route("api/triggers")]
[Authorize]
public class TriggersController : ControllerBase
{
    /// <summary>
    /// Returns the next N fire times for a Quartz cron expression. Pure validation utility —
    /// does not register a job. Used by the schedule-trigger node in the designer to render
    /// "Next: in 5m · then in 1h" so the user can sanity-check the cron before saving.
    /// </summary>
    [HttpGet("schedule/next-fires")]
    public ActionResult<NextFiresResponse> GetNextFires([FromQuery] string cron, [FromQuery] int count = 5)
    {
        if (string.IsNullOrWhiteSpace(cron))
            return BadRequest(new { error = "Query parameter 'cron' is required." });

        try { return Ok(new NextFiresResponse(CalculateFires(cron, count, DateTimeOffset.UtcNow, TimeZoneInfo.Local))); }
        catch (FormatException ex) { return BadRequest(new { error = $"Invalid cron expression: {ex.Message}" }); }
    }

    // The scheduler uses the server's local zone. Keep the clock/zone explicit so DST can be
    // verified without mutating process-wide timezone state or adding another cron parser.
    internal static List<DateTime> CalculateFires(string cron, int count, DateTimeOffset after, TimeZoneInfo zone)
    {
        count = Math.Clamp(count, 1, 20);
        var parsed = new CronExpression(cron, zone);
        var fires = new List<DateTime>(count);
        DateTimeOffset? cursor = after;
        for (var i = 0; i < count; i++)
        {
            cursor = parsed.GetNextValidTimeAfter(cursor!.Value);
            if (cursor is null) break;
            fires.Add(cursor.Value.UtcDateTime);
        }

        return fires;
    }
}

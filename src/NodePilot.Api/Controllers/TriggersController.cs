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

        count = Math.Clamp(count, 1, 20);

        CronExpression parsed;
        try { parsed = new CronExpression(cron); }
        catch (FormatException ex) { return BadRequest(new { error = $"Invalid cron expression: {ex.Message}" }); }

        var fires = new List<DateTime>(count);
        DateTimeOffset? cursor = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            cursor = parsed.GetNextValidTimeAfter(cursor!.Value);
            if (cursor is null) break;
            fires.Add(cursor.Value.UtcDateTime);
        }

        return Ok(new NextFiresResponse(fires, parsed.GetExpressionSummary()));
    }
}

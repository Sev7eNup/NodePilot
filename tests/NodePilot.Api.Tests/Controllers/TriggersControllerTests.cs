using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Coverage for the read-only cron-preview endpoint used by the schedule-trigger node
/// in the designer. Pure utility — no DB, no auth nuance — so the tests focus on the
/// edges: missing/invalid cron, count clamping, and the next-fire ordering.
/// </summary>
public class TriggersControllerTests
{
    [Fact]
    public void GetNextFires_MissingCron_ReturnsBadRequest()
    {
        var controller = new TriggersController();

        var result = controller.GetNextFires(cron: "", count: 5);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GetNextFires_WhitespaceCron_ReturnsBadRequest()
    {
        var controller = new TriggersController();

        var result = controller.GetNextFires(cron: "   ", count: 5);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void GetNextFires_InvalidCron_ReturnsBadRequest_WithExplanation()
    {
        var controller = new TriggersController();

        var result = controller.GetNextFires(cron: "not-a-cron", count: 5);

        var bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        // Body should mention the input is invalid so the UI can surface the parser message.
        var body = bad.Value!.ToString();
        body.Should().Contain("Invalid cron");
    }

    [Fact]
    public void GetNextFires_ValidCron_ReturnsRequestedCount()
    {
        // Every 5 minutes — Quartz can always produce more fires, so count=5 is filled.
        var controller = new TriggersController();

        var result = controller.GetNextFires(cron: "0 */5 * * * ?", count: 5);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeOfType<NextFiresResponse>().Subject;
        resp.Fires.Should().HaveCount(5);
        resp.Summary.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetNextFires_FiresAreStrictlyAscending()
    {
        var controller = new TriggersController();

        var result = controller.GetNextFires(cron: "0 */5 * * * ?", count: 5);

        var resp = (result.Result as OkObjectResult)!.Value as NextFiresResponse;
        var fires = resp!.Fires;
        for (var i = 1; i < fires.Count; i++)
            fires[i].Should().BeAfter(fires[i - 1], $"fire times must increase monotonically (index {i})");
    }

    [Fact]
    public void GetNextFires_CountAboveMax_IsClampedToTwenty()
    {
        // Defensive cap so a UI bug or hostile caller can't request 10_000 fires and stall the
        // request thread on Quartz cron iteration. Pin the clamp value at 20.
        var controller = new TriggersController();

        var result = controller.GetNextFires(cron: "0 */5 * * * ?", count: 9999);

        var resp = (result.Result as OkObjectResult)!.Value as NextFiresResponse;
        resp!.Fires.Should().HaveCountLessThanOrEqualTo(20);
        resp.Fires.Should().HaveCount(20, "count is clamped to the upper bound, not silently reduced to 1");
    }

    [Fact]
    public void GetNextFires_CountBelowOne_IsClampedToOne()
    {
        var controller = new TriggersController();

        var result = controller.GetNextFires(cron: "0 0 12 * * ?", count: 0);

        var resp = (result.Result as OkObjectResult)!.Value as NextFiresResponse;
        resp!.Fires.Should().HaveCount(1, "the lower bound is 1, not 0 — callers always get at least one preview");
    }

    [Fact]
    public void GetNextFires_DailyCron_FiresAreOneDayApart()
    {
        // Sanity check that the cron actually drives the fire schedule rather than the
        // controller doing something clever. Daily-at-noon → consecutive fires are 24h apart.
        var controller = new TriggersController();

        var result = controller.GetNextFires(cron: "0 0 12 * * ?", count: 3);

        var fires = ((result.Result as OkObjectResult)!.Value as NextFiresResponse)!.Fires;
        for (var i = 1; i < fires.Count; i++)
        {
            var delta = fires[i] - fires[i - 1];
            delta.Should().BeCloseTo(TimeSpan.FromDays(1), TimeSpan.FromHours(2),
                $"daily cron should produce ~24h gaps (DST windows allow 2h slack); got {delta} between {fires[i - 1]:o} and {fires[i]:o}");
        }
    }
}

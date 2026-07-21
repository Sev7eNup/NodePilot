using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// <see cref="ApiProblems"/> is the single place that shapes RFC7807 error bodies for the API.
/// Every controller relies on the code/title/traceId contract here, and the legacy-payload
/// adapter is what the result filter uses to upgrade older string/object error returns. Pin
/// the builders, the legacy adapter's accept/reject rules, and the status→code/title tables.
/// </summary>
public class ApiProblemsTests
{
    private sealed class TestController : ControllerBase { }

    private static ControllerBase Controller()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/things/42";
        return new TestController { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    [Fact]
    public void NotFound_ProducesProblemResultWithCodeAndStatus()
    {
        var result = ApiProblems.NotFound(Controller(), "THING_MISSING", "no such thing");
        result.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(404);
        problem.Detail.Should().Be("no such thing");
        problem.Extensions["code"].Should().Be("THING_MISSING");
        problem.Instance.Should().Be("/api/things/42");
    }

    [Fact]
    public void Conflict_ProducesConflictProblem()
    {
        var result = ApiProblems.Conflict(Controller(), "DUP", "already exists");
        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        result.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should().Be("DUP");
    }

    [Fact]
    public void Unauthorized_ProducesUnauthorizedProblem()
    {
        var result = ApiProblems.Unauthorized(Controller(), "NO_TOKEN", "missing token");
        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        result.Value.Should().BeOfType<ProblemDetails>().Which.Status.Should().Be(401);
    }

    [Fact]
    public void BadRequest_ProducesBadRequestProblem()
    {
        var result = ApiProblems.BadRequest(Controller(), "BAD", "nope");
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.Value.Should().BeOfType<ProblemDetails>().Which.Extensions["code"].Should().Be("BAD");
    }

    // ---- legacy payload adapter --------------------------------------------

    [Fact]
    public void TryCreateFromLegacyPayload_Null_ReturnsFalse()
    {
        ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), 400, null, out _).Should().BeFalse();
    }

    [Fact]
    public void TryCreateFromLegacyPayload_ExistingProblemDetails_ReturnsFalse()
    {
        var existing = new ProblemDetails { Status = 400 };
        ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), 400, existing, out _).Should().BeFalse();
    }

    [Fact]
    public void TryCreateFromLegacyPayload_BlankString_ReturnsFalse()
    {
        ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), 400, "   ", out _).Should().BeFalse();
    }

    [Fact]
    public void TryCreateFromLegacyPayload_NonEmptyString_BuildsProblem()
    {
        var ok = ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), 404, "gone", out var problem);
        ok.Should().BeTrue();
        problem.Detail.Should().Be("gone");
        problem.Extensions["code"].Should().Be("NOT_FOUND");
        problem.Title.Should().Be("Not found");
    }

    [Fact]
    public void TryCreateFromLegacyPayload_AnonymousObject_MapsDetailCodeAndExtensions()
    {
        var payload = new { detail = "boom", code = "X_ERR", extra = "keep-me" };
        var ok = ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), 400, payload, out var problem);
        ok.Should().BeTrue();
        problem.Detail.Should().Be("boom");
        problem.Extensions["code"].Should().Be("X_ERR");
        problem.Extensions.Should().ContainKey("extra");
    }

    [Fact]
    public void TryCreateFromLegacyPayload_ReadOnlyDictionary_IsRead()
    {
        IReadOnlyDictionary<string, object?> payload =
            new Dictionary<string, object?> { ["message"] = "from dict" };
        var ok = ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), 409, payload, out var problem);
        ok.Should().BeTrue();
        problem.Detail.Should().Be("from dict");
    }

    [Fact]
    public void TryCreateFromLegacyPayload_MutableDictionary_IsRead()
    {
        IDictionary<string, object?> payload =
            new Dictionary<string, object?> { ["error"] = "from mutable dict" };
        var ok = ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), 500, payload, out var problem);
        ok.Should().BeTrue();
        problem.Detail.Should().Be("from mutable dict");
    }

    [Fact]
    public void TryCreateFromLegacyPayload_OnlyCode_UsesCodeAsDetail()
    {
        var payload = new { code = "ONLY_CODE" };
        var ok = ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), 400, payload, out var problem);
        ok.Should().BeTrue();
        problem.Detail.Should().Be("ONLY_CODE");
        problem.Extensions["code"].Should().Be("ONLY_CODE");
    }

    [Fact]
    public void TryCreateFromLegacyPayload_NoDetailNoCode_ReturnsFalse()
    {
        var payload = new { unrelated = 5 };
        ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), 400, payload, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(400, "BAD_REQUEST", "Invalid request")]
    [InlineData(401, "UNAUTHORIZED", "Unauthorized")]
    [InlineData(403, "FORBIDDEN", "Forbidden")]
    [InlineData(404, "NOT_FOUND", "Not found")]
    [InlineData(405, "METHOD_NOT_ALLOWED", "Method not allowed")]
    [InlineData(409, "CONFLICT", "Conflict")]
    [InlineData(412, "PRECONDITION_FAILED", "Precondition failed")]
    [InlineData(423, "LOCKED", "Locked")]
    [InlineData(428, "PRECONDITION_REQUIRED", "Precondition required")]
    [InlineData(500, "INTERNAL_SERVER_ERROR", "Internal server error")]
    [InlineData(502, "BAD_GATEWAY", "Bad gateway")]
    [InlineData(503, "SERVICE_UNAVAILABLE", "Service unavailable")]
    [InlineData(418, "HTTP_418", "HTTP error")]
    public void TryCreateFromLegacyPayload_StatusMapsToDefaultCodeAndTitle(int status, string code, string title)
    {
        var ok = ApiProblems.TryCreateFromLegacyPayload(new DefaultHttpContext(), status, "some detail", out var problem);
        ok.Should().BeTrue();
        problem.Extensions["code"].Should().Be(code);
        problem.Title.Should().Be(title);
    }
}

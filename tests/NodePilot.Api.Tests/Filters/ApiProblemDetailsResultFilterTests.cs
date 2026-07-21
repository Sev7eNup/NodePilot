using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NodePilot.Api.Filters;
using Xunit;

namespace NodePilot.Api.Tests.Filters;

public sealed class ApiProblemDetailsResultFilterTests
{
    [Fact]
    public void OnResultExecuting_NormalizesLegacyCodeMessagePayload()
    {
        var result = new BadRequestObjectResult(new
        {
            code = "SETTINGS_BODY_INVALID",
            message = "Settings body is invalid.",
            section = "smtp",
        });
        var context = CreateContext(result);

        new ApiProblemDetailsResultFilter().OnResultExecuting(context);

        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Title.Should().Be("Invalid request");
        problem.Detail.Should().Be("Settings body is invalid.");
        problem.Extensions["code"].Should().Be("SETTINGS_BODY_INVALID");
        problem.Extensions["section"].Should().Be("smtp");
        result.ContentTypes.Should().Contain("application/problem+json");
    }

    [Fact]
    public void OnResultExecuting_NormalizesStringPayloads()
    {
        var result = new BadRequestObjectResult("mode must be one of: continue, stepOver, stop");
        var context = CreateContext(result);

        new ApiProblemDetailsResultFilter().OnResultExecuting(context);

        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Extensions["code"].Should().Be("BAD_REQUEST");
        problem.Detail.Should().Be("mode must be one of: continue, stepOver, stop");
    }

    [Fact]
    public void OnResultExecuting_NormalizesLegacyCodeOnlyPayload()
    {
        var result = new NotFoundObjectResult(new { code = "SETTINGS_SECTION_UNKNOWN", section = "runtime" });
        var context = CreateContext(result);

        new ApiProblemDetailsResultFilter().OnResultExecuting(context);

        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status404NotFound);
        problem.Detail.Should().Be("SETTINGS_SECTION_UNKNOWN");
        problem.Extensions["code"].Should().Be("SETTINGS_SECTION_UNKNOWN");
        problem.Extensions["section"].Should().Be("runtime");
    }

    [Fact]
    public void OnResultExecuting_NormalizesServerErrorPayloads()
    {
        var result = new ObjectResult(new { code = "LLM_UNREACHABLE", message = "LLM endpoint is down." })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
        var context = CreateContext(result);

        new ApiProblemDetailsResultFilter().OnResultExecuting(context);

        var problem = result.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);
        problem.Title.Should().Be("Service unavailable");
        problem.Detail.Should().Be("LLM endpoint is down.");
        problem.Extensions["code"].Should().Be("LLM_UNREACHABLE");
    }

    [Fact]
    public void OnResultExecuting_LeavesUnrecognizedErrorPayloadsUntouched()
    {
        var payload = new { rows = Array.Empty<object>() };
        var result = new ObjectResult(payload) { StatusCode = StatusCodes.Status400BadRequest };
        var context = CreateContext(result);

        new ApiProblemDetailsResultFilter().OnResultExecuting(context);

        result.Value.Should().BeSameAs(payload);
    }

    [Fact]
    public void OnResultExecuting_LeavesExistingProblemDetailsUntouched()
    {
        var original = new ProblemDetails { Status = 409, Title = "Conflict", Detail = "Already locked." };
        original.Extensions["code"] = "WORKFLOW_LOCKED";
        var result = new ConflictObjectResult(original);
        var context = CreateContext(result);

        new ApiProblemDetailsResultFilter().OnResultExecuting(context);

        result.Value.Should().BeSameAs(original);
    }

    private static ResultExecutingContext CreateContext(ObjectResult result)
    {
        var http = new DefaultHttpContext();
        http.TraceIdentifier = "trace-1";
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ResultExecutingContext(actionContext, new List<IFilterMetadata>(), result, controller: new object());
    }
}

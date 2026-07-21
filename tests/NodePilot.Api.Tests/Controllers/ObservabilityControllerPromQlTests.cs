using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Telemetry;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Coverage for the H8/M-20 PromQL allow-list — the only thing standing between an
/// authenticated operator token and "query the entire shared Prometheus TSDB". Every
/// test here is a security regression guard, not a happy-path.
/// </summary>
public class ObservabilityControllerPromQlTests
{
    /// <summary>
    /// Returns a 200 OK with a minimal PromQL response shape regardless of input.
    /// Lets us exercise the Query / QueryRange path without depending on a real
    /// Prometheus instance, while still letting validator-rejection failures show
    /// up as BadRequestObjectResult before the handler runs.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"success","data":{"resultType":"vector","result":[]}}"""),
            });
        }
    }

    private static ObservabilityController CreateController(string[]? allowedPrefixes = null)
    {
        var options = new NodePilotTelemetryOptions
        {
            Enabled = true,
            Prometheus = new NodePilotTelemetryOptions.PrometheusOptions
            {
                QueryEndpoint = "http://prom.local",
            },
            AllowedMetricPrefixes = allowedPrefixes ?? Array.Empty<string>(),
        };
        var prom = new PrometheusClient(new HttpClient(new StubHandler()), options);
        return new ObservabilityController(options, prom, NullLogger<ObservabilityController>.Instance);
    }

    [Fact]
    public async Task Query_NoConfig_Returns503()
    {
        var options = new NodePilotTelemetryOptions { Prometheus = new() }; // no endpoint
        var prom = new PrometheusClient(new HttpClient(), options);
        var controller = new ObservabilityController(options, prom, NullLogger<ObservabilityController>.Instance);

        var result = await controller.Query("nodepilot_executions_started", null, CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Query_EmptyQueryString_Returns400()
    {
        var controller = CreateController();
        var result = await controller.Query("", null, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Query_TooLong_Returns400()
    {
        var controller = CreateController();
        var huge = new string('a', 8 * 1024 + 1);
        var result = await controller.Query(huge, null, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Query_NameLabelSelectorTrick_Rejected()
    {
        // H8 regression: this query bypasses the prefix allow-list (zero metric tokens)
        // and would dump every series in Prometheus including co-tenant data.
        var controller = CreateController();
        var result = await controller.Query("""{__name__=~".+"}""", null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Query_LabelSelectorOnlyNoMetric_Rejected()
    {
        // Pure label-selector queries bypass the metric allow-list and must be rejected.
        var controller = CreateController();
        var result = await controller.Query("""{job="api"}""", null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Query_DisallowedMetricPrefix_Rejected()
    {
        // "redis_commands_processed_total" doesn't match any default-allowed prefix.
        var controller = CreateController();
        var result = await controller.Query("redis_commands_processed_total", null, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData("nodepilot_executions_started")]
    [InlineData("http_server_request_duration_count")]
    [InlineData("process_cpu_seconds_total")]
    [InlineData("dotnet_gc_collections_total")]
    [InlineData("up")]
    public async Task Query_AllowedPrefix_PassesValidation(string metric)
    {
        // The validation lets the metric through; the request to a fake Prometheus host
        // will then fail at the HTTP layer — but with a non-400 status code (the validator
        // didn't reject the metric). That's the assertion: the response shape is *not* a
        // BadRequestObjectResult from the validator.
        var controller = CreateController();

        var result = await controller.Query(metric, null, CancellationToken.None);

        // Whatever the prom client returns (likely a wrapped ContentResult with the upstream
        // status), it must NOT be the validator's BadRequest. If validation failed, the test
        // would short-circuit to BadRequestObjectResult before any I/O happens.
        result.Should().NotBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Query_AggregationOnAllowedMetric_PassesValidation()
    {
        // sum/rate/clamp_min are reserved keywords → not metrics → must pass validation
        // because the underlying metric is allowed.
        var controller = CreateController();
        var result = await controller.Query(
            "sum(rate(nodepilot_executions_started[5m]))", null, CancellationToken.None);

        result.Should().NotBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Query_CustomAllowedPrefix_PermitsCustomMetric()
    {
        var controller = CreateController(new[] { "myorg_" });
        var result = await controller.Query("myorg_orders_processed_total", null, CancellationToken.None);

        // Validator passes; downstream HTTP fails harmlessly.
        result.Should().NotBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task QueryRange_SameValidationAsQuery()
    {
        var controller = CreateController();
        var result = await controller.QueryRange(
            """{__name__=~".+"}""", 0, 1000, "15s", CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task QueryRange_NoConfig_Returns503()
    {
        var options = new NodePilotTelemetryOptions { Prometheus = new() };
        var prom = new PrometheusClient(new HttpClient(), options);
        var controller = new ObservabilityController(options, prom, NullLogger<ObservabilityController>.Instance);

        var result = await controller.QueryRange("nodepilot_x", 0, 1000, "15s", CancellationToken.None);

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Summary_NoPrometheus_ReturnsAvailableFalseEmptyPanels()
    {
        var options = new NodePilotTelemetryOptions { Prometheus = new() };
        var prom = new PrometheusClient(new HttpClient(), options);
        var controller = new ObservabilityController(options, prom, NullLogger<ObservabilityController>.Instance);

        var result = await controller.Summary(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeOfType<NodePilot.Api.Dtos.TelemetrySummaryResponse>().Subject;
        resp.Available.Should().BeFalse();
        resp.Panels.Should().BeEmpty();
    }
}

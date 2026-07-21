using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Telemetry;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class ObservabilityControllerTests
{
    private static ObservabilityController CreateController(bool authenticated)
    {
        // Populate every field that anonymous responses must NOT leak.
        var options = new NodePilotTelemetryOptions
        {
            Enabled = true,
            Environment = "production",
            Otlp = new NodePilotTelemetryOptions.OtlpOptions { BrowserEndpoint = "http://internal-collector:4318/v1/traces" },
            TraceUi = new NodePilotTelemetryOptions.TraceUiOptions { UrlTemplate = "http://internal-tempo/trace/{traceId}", BackendName = "Tempo" },
            Prometheus = new NodePilotTelemetryOptions.PrometheusOptions { QueryEndpoint = "http://internal-prometheus:9090" },
        };
        var prom = new PrometheusClient(new HttpClient(), options);

        var controller = new ObservabilityController(options, prom, NullLogger<ObservabilityController>.Instance);

        var identity = authenticated
            ? new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "test")
            : new ClaimsIdentity();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    [Fact]
    public void GetConfig_Unauthenticated_ReturnsMinimalShell()
    {
        var controller = CreateController(authenticated: false);

        var result = controller.GetConfig();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeOfType<ObservabilityConfigResponse>().Subject;

        // No internal URLs or availability flags may leak to anonymous callers.
        resp.Enabled.Should().BeFalse();
        resp.BrowserOtlpEndpoint.Should().BeNull();
        resp.TraceUiUrlTemplate.Should().BeNull();
        resp.TraceBackendName.Should().BeNull();
        resp.PrometheusAvailable.Should().BeFalse();
        resp.Environment.Should().BeNull();
    }

    [Fact]
    public void GetConfig_Authenticated_ReturnsFullConfig()
    {
        var controller = CreateController(authenticated: true);

        var result = controller.GetConfig();

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeOfType<ObservabilityConfigResponse>().Subject;

        resp.Enabled.Should().BeTrue();
        resp.BrowserOtlpEndpoint.Should().Be("http://internal-collector:4318/v1/traces");
        resp.TraceUiUrlTemplate.Should().Be("http://internal-tempo/trace/{traceId}");
        resp.PrometheusAvailable.Should().BeTrue();
        resp.Environment.Should().Be("production");
    }

    // Captures the PromQL each summary panel sends so we can assert it targets the REAL
    // exported metric names. The OTel Prometheus exporter appends the unit (`_milliseconds`)
    // to histogram families and `_seconds` to the HTTP histogram — the previously-shipped
    // queries omitted those suffixes and silently returned no data.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public ConcurrentBag<string> Queries { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[0,\"1\"]}]}}"),
            });
        }
    }

    [Fact]
    public async Task Summary_ComposesQueriesAgainstRealExportedMetricNames()
    {
        var handler = new CapturingHandler();
        var options = new NodePilotTelemetryOptions
        {
            Enabled = true,
            Prometheus = new NodePilotTelemetryOptions.PrometheusOptions { QueryEndpoint = "http://prometheus:9090" },
        };
        var prom = new PrometheusClient(new HttpClient(handler), options);
        var controller = new ObservabilityController(options, prom, NullLogger<ObservabilityController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) }, "test")),
                },
            },
        };

        await controller.Summary(CancellationToken.None);

        var allQueries = string.Join("\n", handler.Queries);

        // Correct, real exported names.
        allQueries.Should().Contain("nodepilot_execution_duration_milliseconds_bucket");
        allQueries.Should().Contain("nodepilot_winrm_session_open_duration_milliseconds_bucket");
        allQueries.Should().Contain("http_server_request_duration_seconds_count");

        // The previously-shipped wrong names (no unit suffix) must not reappear — they bound
        // to metric series that do not exist, so the panels rendered "No Data".
        allQueries.Should().NotContain("nodepilot_execution_duration_bucket");
        allQueries.Should().NotContain("nodepilot_winrm_session_open_duration_bucket");
        allQueries.Should().NotContain("http_server_request_duration_count");
    }
}

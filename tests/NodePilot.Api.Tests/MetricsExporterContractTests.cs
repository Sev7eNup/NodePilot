using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Services.Observability;
using NodePilot.Core.Telemetry;
using NodePilot.Engine;
using NodePilot.Telemetry;
using OpenTelemetry.Metrics;
using Xunit;

namespace NodePilot.Api.Tests;

public sealed class MetricsExporterContractTests
{
    [Fact]
    public async Task ShippedExecutionAndCpuQueries_ReferenceActualExporterSeries()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
            .AddMeter(TelemetryConstants.Meters.Engine)
            .AddRuntimeInstrumentation().AddProcessInstrumentation().AddPrometheusExporter());
        await using var app = builder.Build();
        app.MapPrometheusScrapingEndpoint();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var workflow = Guid.NewGuid().ToString("N");
        EngineMetrics.ExecutionsStarted.Add(1, new KeyValuePair<string, object?>("workflow_name", workflow));
        EngineMetrics.ExecutionsCompleted.Add(1,
            new KeyValuePair<string, object?>("workflow_name", workflow),
            new KeyValuePair<string, object?>("status", "Failed"));
        using var scrapeClient = app.GetTestClient();
        var scrape = await scrapeClient.GetStringAsync("/metrics", TestContext.Current.CancellationToken);
        var metricNames = scrape.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => Regex.Match(line, "^[a-zA-Z_:][a-zA-Z0-9_:]*").Value).ToHashSet(StringComparer.Ordinal);

        var handler = new QueryCapture();
        using var http = new HttpClient(handler);
        var options = new NodePilotTelemetryOptions
        {
            Enabled = true,
            Prometheus = new NodePilotTelemetryOptions.PrometheusOptions { QueryEndpoint = "http://fixture.invalid" },
        };
        var prometheus = new PrometheusClient(http, options);
        await new ObservabilityController(options, prometheus, NullLogger<ObservabilityController>.Instance)
            .Summary(TestContext.Current.CancellationToken);
        foreach (var query in handler.Queries.Where(query => query.Contains("nodepilot_executions_started") || query.Contains("nodepilot_executions_completed")))
            AssertExported(query, "nodepilot_executions_(?:started|completed)[a-zA-Z0-9_]*", metricNames);
        handler.Queries.Count(query => query.Contains("nodepilot_executions_started") || query.Contains("nodepilot_executions_completed")).Should().Be(3);

        handler.Queries.Clear();
        await MetricsDashboardCatalog.ExecuteAsync("mission-control", 24, prometheus, NullLogger.Instance, TestContext.Current.CancellationToken);
        await MetricsDashboardCatalog.ExecuteAsync("runtime", 24, prometheus, NullLogger.Instance, TestContext.Current.CancellationToken);
        var cpuQueries = handler.Queries.Where(query => query.Contains("cpu")).ToArray();
        cpuQueries.Should().HaveCount(4);
        foreach (var query in cpuQueries)
            AssertExported(query, "(?:dotnet_)?process_cpu_[a-zA-Z0-9_]+", metricNames);
        var grouping = cpuQueries.Single(query => query.Contains("sum by"));
        var label = Regex.Match(grouping, "sum by \\(([^)]+)\\)").Groups[1].Value;
        var cpuSeries = scrape.Split('\n').Where(line => line.StartsWith("process_cpu_time_seconds_total{"));
        cpuSeries.Should().NotBeEmpty().And.OnlyContain(line => line.Contains(label + "=\""));
    }

    private static void AssertExported(string query, string pattern, HashSet<string> metricNames)
    {
        var references = Regex.Matches(query, pattern).Select(match => match.Value).ToArray();
        references.Should().NotBeEmpty();
        references.Should().OnlyContain(reference => metricNames.Contains(reference), "every shipped selector must bind to the current exporter contract");
    }

    private sealed class QueryCapture : HttpMessageHandler
    {
        public ConcurrentBag<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Queries.Add(Uri.UnescapeDataString(request.RequestUri?.Query ?? ""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}"),
            });
        }
    }
}

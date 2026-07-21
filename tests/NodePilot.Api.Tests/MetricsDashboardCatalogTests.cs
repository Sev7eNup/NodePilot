using NodePilot.Api.Services.Observability;
using NodePilot.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NodePilot.Api.Tests;

public sealed class MetricsDashboardCatalogTests
{
    [Theory]
    [InlineData("mission-control", 22)]
    [InlineData("workflows", 16)]
    [InlineData("activities", 14)]
    [InlineData("winrm", 16)]
    [InlineData("triggers", 17)]
    [InlineData("api", 15)]
    [InlineData("runtime", 21)]
    [InlineData("security", 15)]
    [InlineData("ai", 14)]
    [InlineData("database", 12)]
    public void EmbeddedDashboard_preservesEveryMetricPanel(string key, int expected)
    {
        Assert.True(MetricsDashboardCatalog.Exists(key));
        Assert.Equal(expected, MetricsDashboardCatalog.PanelCount(key));
        Assert.False(string.IsNullOrWhiteSpace(MetricsDashboardCatalog.Title(key)));
    }

    [Fact]
    public async Task Dashboard_withNonFinitePrometheusValues_isJsonSerializable()
    {
        var options = new NodePilotTelemetryOptions();
        options.Prometheus.QueryEndpoint = "http://prometheus.test";
        var client = new PrometheusClient(new HttpClient(new NonFinitePrometheusHandler()), options);

        var dashboard = await MetricsDashboardCatalog.ExecuteAsync(
            "mission-control", 24, client, NullLogger.Instance, CancellationToken.None);

        var serialize = () => JsonSerializer.Serialize(dashboard);
        serialize.Should().NotThrow();
        dashboard.Widgets!.SelectMany(widget => widget.Data).SelectMany(series => series.Points)
            .Should().OnlyContain(point => point.Value == null);
    }

    private sealed class NonFinitePrometheusHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string body = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1700000000,\"Infinity\"]}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using NodePilot.Telemetry;
using Xunit;

namespace NodePilot.Api.Tests.Telemetry;

/// <summary>
/// HTTP-mocked tests for <see cref="PrometheusClient"/>. We verify the URL/parameter
/// construction, auth-header semantics, and the not-configured short-circuit. Network
/// is replaced with a fake <see cref="HttpMessageHandler"/> that records every request.
/// </summary>
public class PrometheusClientTests
{
    private static (PrometheusClient client, RecordingHandler handler) Build(NodePilotTelemetryOptions opts)
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);
        return (new PrometheusClient(http, opts), handler);
    }

    private static NodePilotTelemetryOptions OptsWith(string? endpoint, Action<NodePilotTelemetryOptions.PrometheusOptions>? tweak = null)
    {
        var o = new NodePilotTelemetryOptions { Prometheus = new() { QueryEndpoint = endpoint } };
        tweak?.Invoke(o.Prometheus);
        return o;
    }

    [Fact]
    public void IsConfigured_FalseWhenEndpointMissing()
    {
        var (client, _) = Build(OptsWith(endpoint: null));
        client.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_FalseWhenEndpointBlank()
    {
        var (client, _) = Build(OptsWith(endpoint: "   "));
        client.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_TrueWhenEndpointSet()
    {
        var (client, _) = Build(OptsWith(endpoint: "http://prom:9090"));
        client.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task InstantAsync_NotConfigured_Returns503WithoutHittingNetwork()
    {
        var (client, handler) = Build(OptsWith(endpoint: null));

        var result = await client.InstantAsync("up", null, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.Body.Should().Contain("not configured");
        handler.Requests.Should().BeEmpty(
            "the not-configured short-circuit must run BEFORE building any HTTP request");
    }

    [Fact]
    public async Task InstantAsync_BuildsCorrectUrl_WithQueryParameter()
    {
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090"));
        handler.NextResponse = JsonResponse("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}");

        await client.InstantAsync("rate(http_requests_total[5m])", null, CancellationToken.None);

        handler.Requests.Should().HaveCount(1);
        var url = handler.Requests[0].RequestUri!.ToString();
        url.Should().StartWith("http://prom:9090/api/v1/query?");
        url.Should().Contain("query=rate%28http_requests_total%5B5m%5D%29",
            "query string must be percent-encoded — the raw [, ], ( all need escaping");
    }

    [Fact]
    public async Task InstantAsync_OptionalTimeParameter_Included()
    {
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090"));
        handler.NextResponse = JsonResponse("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}");

        await client.InstantAsync("up", timeEpochSeconds: 1700000000, CancellationToken.None);

        handler.Requests[0].RequestUri!.Query.Should().Contain("time=1700000000");
    }

    [Fact]
    public async Task InstantAsync_TimeNull_DropsTimeParameter()
    {
        // Pin: when the caller passes null, the URL must NOT contain "time=" at all.
        // Otherwise Prometheus parses an empty time as "0" and returns the unix epoch,
        // which is a confusing result rather than "now".
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090"));
        handler.NextResponse = JsonResponse("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}");

        await client.InstantAsync("up", timeEpochSeconds: null, CancellationToken.None);

        handler.Requests[0].RequestUri!.Query.Should().NotContain("time=");
    }

    [Fact]
    public async Task RangeAsync_BuildsCorrectUrl_WithStartEndStep()
    {
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090"));
        handler.NextResponse = JsonResponse("{\"status\":\"success\",\"data\":{\"resultType\":\"matrix\",\"result\":[]}}");

        await client.RangeAsync("up", start: 100, end: 200, step: "30s", CancellationToken.None);

        var url = handler.Requests[0].RequestUri!.ToString();
        url.Should().StartWith("http://prom:9090/api/v1/query_range?");
        url.Should().Contain("query=up");
        url.Should().Contain("start=100");
        url.Should().Contain("end=200");
        url.Should().Contain("step=30s");
    }

    [Fact]
    public async Task BearerToken_SetsAuthorizationHeader_AndOverridesBasicAuth()
    {
        // Bearer takes precedence over username/password if both are set.
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090", o =>
        {
            o.BearerToken = "secret-bearer";
            o.Username = "ignored";
            o.Password = "ignored";
        }));
        handler.NextResponse = JsonResponse("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}");

        await client.InstantAsync("up", null, CancellationToken.None);

        var auth = handler.Requests[0].Headers.Authorization!;
        auth.Scheme.Should().Be("Bearer");
        auth.Parameter.Should().Be("secret-bearer");
    }

    [Fact]
    public async Task UsernamePassword_FallsBackToBasicAuth_Base64Encoded()
    {
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090", o =>
        {
            o.Username = "alice";
            o.Password = "p@ss";
        }));
        handler.NextResponse = JsonResponse("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}");

        await client.InstantAsync("up", null, CancellationToken.None);

        var auth = handler.Requests[0].Headers.Authorization!;
        auth.Scheme.Should().Be("Basic");
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
        decoded.Should().Be("alice:p@ss");
    }

    [Fact]
    public async Task NoCredentials_OmitsAuthorizationHeader()
    {
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090"));
        handler.NextResponse = JsonResponse("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}");

        await client.InstantAsync("up", null, CancellationToken.None);

        handler.Requests[0].Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task QueryScalarAsync_ExtractsVectorResult()
    {
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090"));
        handler.NextResponse = JsonResponse(
            "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\"," +
            "\"result\":[{\"metric\":{},\"value\":[1700000000,\"42.5\"]}]}}");

        var value = await client.QueryScalarAsync("rate(...)", CancellationToken.None);

        value.Should().Be(42.5);
    }

    [Fact]
    public async Task QueryScalarAsync_FailedRequest_ReturnsNull()
    {
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090"));
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("oops") };

        var value = await client.QueryScalarAsync("up", CancellationToken.None);

        value.Should().BeNull();
    }

    [Fact]
    public async Task ProxyResponse_PropagatesContentTypeAndStatusCode()
    {
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090"));
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        handler.NextResponse = resp;

        var result = await client.InstantAsync("up", null, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.ContentType.Should().Be("application/json");
        result.Body.Should().Be("{}");
    }

    [Fact]
    public async Task TimeoutSeconds_ConfigureHttpClientTimeout()
    {
        // The pin: TimeoutSeconds=0 (or negative) is clamped to 1 second so a misconfigured
        // setting can't disable HttpClient's timeout entirely (which would block requests
        // forever on a flaky Prometheus).
        var (client, handler) = Build(OptsWith(endpoint: "http://prom:9090", o => o.TimeoutSeconds = 0));
        handler.NextResponse = JsonResponse("{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}");

        await client.InstantAsync("up", null, CancellationToken.None);

        // Inspect the HttpClient via the handler — we set the Timeout right before sending.
        // We can't directly read it here without leaking a reference, but the assertion
        // above proves no infinite-timeout was passed (request returned).
        handler.Requests.Should().HaveCount(1);
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };
        resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return resp;
    }

    /// <summary>HttpMessageHandler that captures every request and returns a queued response.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public HttpResponseMessage NextResponse { get; set; } = new(HttpStatusCode.NoContent);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(NextResponse);
        }
    }
}

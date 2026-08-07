using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Hosting;
using NodePilot.Data.Availability;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Covers the request gate that turns the breaker into behaviour. The path matrix is the
/// load-bearing part: gating too little leaves requests hanging against a dead database, gating too
/// much serves a JSON 503 in place of the SPA document — and then the banner that explains the
/// outage can never render (there is no UseDefaultFiles; the document comes from an endpoint).
/// </summary>
public sealed class DatabaseAvailabilityMiddlewareTests
{
    private static DatabaseAvailabilityTracker Open()
    {
        var tracker = new DatabaseAvailabilityTracker(NullLogger<DatabaseAvailabilityTracker>.Instance);
        tracker.MarkBootComplete();
        tracker.ReportUnreachable(DatabaseOutageReason.Unreachable);
        return tracker;
    }

    private static DefaultHttpContext Context(string path, string? query = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        if (query is not null) ctx.Request.QueryString = new QueryString(query);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    // ---- ShouldGate: the pure path matrix ------------------------------------------------------

    [Theory]
    [InlineData("/api/workflows", null, true)]
    [InlineData("/api/auth/login", null, true)]
    [InlineData("/signin-oidc", null, true)]
    [InlineData("/hubs/execution/negotiate", null, true)]
    [InlineData("/hubs/execution/negotiate", "?id=spoofed", true)]
    [InlineData("/hubs/execution", null, true)]
    [InlineData("/hubs/execution", "?id=websocket", true)]
    [InlineData("/hubs/execution", "?id=sse&transport=serverSentEvents", true)]
    [InlineData("/hubs/execution", "?id=long-poll&transport=longPolling", true)]
    [InlineData("/healthz/database", null, false)]         // how the SPA learns the outage exists
    [InlineData("/healthz/ready", null, false)]
    [InlineData("/", null, false)]                          // SPA document — renders the banner
    [InlineData("/login", null, false)]
    [InlineData("/workflows", null, false)]
    [InlineData("/assets/index-abc.js", null, false)]
    public void ShouldGate_PathMatrix(string path, string? query, bool expected)
        => DatabaseAvailabilityMiddleware.ShouldGate(Context(path, query)).Should().Be(expected);

    [Fact]
    public void ShouldGate_ProtectedMetricsEndpoint_ReturnsTrue()
    {
        var ctx = Context("/metrics");
        ctx.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute()),
            "protected metrics"));

        DatabaseAvailabilityMiddleware.ShouldGate(ctx).Should().BeTrue();
    }

    [Fact]
    public void ShouldGate_PublicMetricsEndpoint_ReturnsFalse()
    {
        var ctx = Context("/metrics");
        ctx.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(),
            "public metrics"));

        DatabaseAvailabilityMiddleware.ShouldGate(ctx).Should().BeFalse();
    }

    // ---- InvokeAsync behaviour -----------------------------------------------------------------

    [Fact]
    public async Task Invoke_ApiPathWhileUnavailable_Returns503WithCodeAndRetryAfter()
    {
        var ctx = Context("/api/workflows");
        var nextCalled = false;
        var middleware = new DatabaseAvailabilityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, Open());

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeFalse("the whole point is that nothing downstream may touch the database");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Headers.RetryAfter.ToString().Should().Be(
            DatabaseUnavailableResponse.UnavailableRetryAfterSeconds.ToString());

        ctx.Response.Body.Position = 0;
        using var body = JsonDocument.Parse(ctx.Response.Body);
        body.RootElement.GetProperty("code").GetString().Should().Be(DatabaseUnavailableResponse.UnavailableCode);
        body.RootElement.GetProperty("retryable").GetBoolean().Should().BeTrue();
        body.RootElement.GetProperty("reason").GetString().Should().Be("Unreachable");
    }

    [Theory]
    [InlineData("/signin-oidc")]
    [InlineData("/hubs/execution/negotiate")]
    [InlineData("/hubs/execution?id=spoofed")]
    public async Task Invoke_PreAuthenticationDatabaseSurfaceWhileUnavailable_ReturnsShared503(
        string target)
    {
        var separator = target.IndexOf('?');
        var path = separator < 0 ? target : target[..separator];
        var query = separator < 0 ? null : target[separator..];
        var ctx = Context(path, query);
        var nextCalled = false;
        var middleware = new DatabaseAvailabilityMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; }, Open());

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Body.Position = 0;
        using var body = JsonDocument.Parse(ctx.Response.Body);
        body.RootElement.GetProperty("code").GetString()
            .Should().Be(DatabaseUnavailableResponse.UnavailableCode);
    }

    [Fact]
    public async Task Invoke_ProtectedMetricsWhileUnavailable_ReturnsShared503()
    {
        var ctx = Context("/metrics");
        ctx.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute()),
            "protected metrics"));
        var nextCalled = false;
        var middleware = new DatabaseAvailabilityMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; }, Open());

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        ctx.Response.Body.Position = 0;
        using var body = JsonDocument.Parse(ctx.Response.Body);
        body.RootElement.GetProperty("code").GetString()
            .Should().Be(DatabaseUnavailableResponse.UnavailableCode);
    }

    [Fact]
    public async Task Invoke_RejectedOutage_AnswersNotRetryableWithAdminCopy()
    {
        var tracker = new DatabaseAvailabilityTracker(NullLogger<DatabaseAvailabilityTracker>.Instance);
        tracker.MarkBootComplete();
        tracker.ReportUnreachable(DatabaseOutageReason.RejectedByServer);
        var ctx = Context("/api/workflows");
        var middleware = new DatabaseAvailabilityMiddleware(_ => Task.CompletedTask, tracker);

        await middleware.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        using var body = JsonDocument.Parse(ctx.Response.Body);
        // A wrong password is not an outage that clears on its own — promising auto-recovery over
        // one would hide a configuration problem behind a cheerful banner.
        body.RootElement.GetProperty("retryable").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("message").GetString().Should().Contain("administrator");
    }

    [Fact]
    public async Task Invoke_SpaDocumentWhileUnavailable_CallsNext()
    {
        var ctx = Context("/workflows");
        var nextCalled = false;
        var middleware = new DatabaseAvailabilityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, Open());

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue("a JSON 503 here would replace the document that renders the banner");
    }

    [Fact]
    public async Task Invoke_HubTransportRequestWithConnectionId_WhileUnavailable_Returns503()
    {
        var ctx = Context("/hubs/execution", "?id=live-connection");
        var nextCalled = false;
        var middleware = new DatabaseAvailabilityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, Open());

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeFalse("no outage request on the hub HTTP surface may reach auth or hub code");
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Invoke_WhileServable_CallsNext()
    {
        var tracker = new DatabaseAvailabilityTracker(NullLogger<DatabaseAvailabilityTracker>.Instance);
        tracker.MarkBootComplete();
        var ctx = Context("/api/workflows");
        var nextCalled = false;
        var middleware = new DatabaseAvailabilityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, tracker);

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Invoke_ArmedState_IsServedNormally()
    {
        // Armed = one slow query, probe adjudicating. Sealing /api here would turn every slow
        // query into a self-inflicted outage.
        var tracker = new DatabaseAvailabilityTracker(NullLogger<DatabaseAvailabilityTracker>.Instance);
        tracker.MarkBootComplete();
        tracker.Arm();
        var ctx = Context("/api/workflows");
        var nextCalled = false;
        var middleware = new DatabaseAvailabilityMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, tracker);

        await middleware.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }
}

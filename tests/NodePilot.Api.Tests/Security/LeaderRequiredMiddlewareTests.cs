using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Security;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Verifies the follower-node 503 firewall. The middleware is the last line of defence
/// against an LB mis-route or an operator who hits a follower's IP directly.
/// </summary>
public class LeaderRequiredMiddlewareTests
{
    private static async Task<(int statusCode, bool nextCalled)> Run(
        IClusterStateProvider cluster, string path, string method = "POST")
    {
        var nextCalled = false;
        var middleware = new LeaderRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            NullLogger<LeaderRequiredMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(ctx, cluster);
        return (ctx.Response.StatusCode, nextCalled);
    }

    [Theory]
    [InlineData("/api/workflows")]
    [InlineData("/api/workflows/123")]
    [InlineData("/api/executions")]
    [InlineData("/api/webhooks/foo/bar")]
    [InlineData("/api/auth/login")]
    [InlineData("/api/audit")]
    [InlineData("/api/credentials")]
    [InlineData("/api/backup/restore")]
    [InlineData("/api/alerting/test-fire")]
    [InlineData("/api/custom-activities")]
    [InlineData("/api/maintenance-windows")]
    [InlineData("/api/global-variable-folders")]
    [InlineData("/api/admin/settings/authentication")]
    [InlineData("/api/secrets/reencrypt")]
    [InlineData("/hubs/execution")]
    public async Task Follower_LeaderOnlyPath_Returns503(string path)
    {
        var (status, nextCalled) = await Run(new FakeCluster(false), path);

        status.Should().Be((int)HttpStatusCode.ServiceUnavailable,
            $"a follower must reject {path} with 503");
        nextCalled.Should().BeFalse("next pipeline must not run for blocked paths");
    }

    [Theory]
    [InlineData("/healthz/live")]
    [InlineData("/healthz/ready")]
    [InlineData("/healthz/leader")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/index.html")]
    [InlineData("/")]
    public async Task Follower_NonLeaderOnlyPath_PassesThrough(string path)
    {
        var (_, nextCalled) = await Run(new FakeCluster(false), path);

        nextCalled.Should().BeTrue($"non-leader-only path {path} must always pass through, " +
            "so operators can probe a follower's health and SPA assets still load");
    }

    [Theory]
    [InlineData("/api/workflows")]
    [InlineData("/api/backup/manifest")]
    [InlineData("/api/system-info")]
    public async Task Follower_ReadOnlyApiRequest_PassesThrough(string path)
    {
        var (_, nextCalled) = await Run(new FakeCluster(false), path, "GET");

        nextCalled.Should().BeTrue("read-only diagnostics may be served by a follower");
    }

    [Fact]
    public async Task Leader_AllPathsPassThrough()
    {
        var (_, nextCalled) = await Run(new FakeCluster(true), "/api/workflows");
        nextCalled.Should().BeTrue("on a leader the middleware is a no-op");
    }

    [Fact]
    public async Task PreAuthenticationOidcFence_BlocksFollowerCallbackBeforeRemoteHandler()
    {
        var nextCalled = false;
        var middleware = new OidcCallbackLeaderFenceMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/signin-oidc";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, new FakeCluster(false));

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        nextCalled.Should().BeFalse();
    }

    private sealed class FakeCluster : IClusterStateProvider
    {
        public FakeCluster(bool isLeader) { IsLeader = isLeader; }
        public bool IsLeader { get; }
        public string NodeId => "test";
        public DateTime? LeaseExpiresAt => null;
        public long LeaseEpoch => 0;
        public DateTime? LastSuccessfulRenewAt => null;
        public event Action<long>? OnLeadershipAcquired { add { } remove { } }
        public event Action? OnLeadershipLost { add { } remove { } }
    }
}

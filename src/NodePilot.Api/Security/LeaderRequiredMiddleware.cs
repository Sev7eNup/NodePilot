using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Security;

/// <summary>
/// Defense-in-depth: even if the load-balancer is mis-configured (or a caller hits a
/// follower's IP directly), refuses any mutating API or hub request from a follower with
/// 503 + Retry-After. The /healthz/* paths and read-only metadata endpoints are allowed
/// through so operators can still diagnose a follower remotely.
/// <para>
/// Single-node mode <see cref="IClusterStateProvider.IsLeader"/> is permanently true so
/// this middleware is a no-op.
/// </para>
/// </summary>
public sealed class LeaderRequiredMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LeaderRequiredMiddleware> _logger;

    public LeaderRequiredMiddleware(RequestDelegate next, ILogger<LeaderRequiredMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx, IClusterStateProvider cluster)
    {
        if (cluster.IsLeader)
        {
            await _next(ctx);
            return;
        }

        var path = ctx.Request.Path.Value ?? string.Empty;
        var isReadOnlyMethod = HttpMethods.IsGet(ctx.Request.Method)
            || HttpMethods.IsHead(ctx.Request.Method)
            || HttpMethods.IsOptions(ctx.Request.Method);
        // Future-safe policy: every non-read API request is leader-only. Maintaining a
        // route allow-list inevitably lets newly added mutation surfaces drift through.
        // Hubs are always leader-only because a WebSocket can invoke mutating methods
        // after its initial GET handshake.
        var leaderOnly = path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/signin-oidc", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/auth/oidc", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                && !isReadOnlyMethod);
        if (!leaderOnly)
        {
            await _next(ctx);
            return;
        }

        _logger.LogWarning(
            "Follower {NodeId} received leader-only request {Method} {Path} — responding 503. Caller bypassed the load-balancer VIP.",
            cluster.NodeId, ctx.Request.Method, path);
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers["Retry-After"] = "30";
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = "follower_node",
            nodeId = cluster.NodeId,
            message = "This NodePilot node is not the active cluster leader. Retry via the load-balancer VIP."
        });
    }
}

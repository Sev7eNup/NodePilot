using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Security;

/// <summary>
/// Pre-authentication HA fence for the OpenID Connect remote callback. The OIDC handler
/// consumes <c>/signin-oidc</c> inside <c>UseAuthentication</c>, so the general leader
/// middleware that follows authentication cannot protect this state-changing callback.
/// </summary>
public sealed class OidcCallbackLeaderFenceMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IClusterStateProvider cluster)
    {
        if (!cluster.IsLeader
            && context.Request.Path.Equals("/signin-oidc", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "30";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "follower_node",
                nodeId = cluster.NodeId,
                message = "The OIDC callback must be completed by the active cluster leader.",
            });
            return;
        }

        await next(context);
    }
}

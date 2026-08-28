using Microsoft.AspNetCore.Authorization;
using NodePilot.Data.Availability;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Answers 503 immediately while the breaker is open, instead of letting every request queue behind
/// a
/// database that is not going to reply.
///
/// <para>This is the piece that turns the breaker from an observation into a behaviour. Without it
/// the
/// process still detects an outage, but each request still pays
/// <c>TokenValidityMiddleware</c>'s three database reads first; without its dedicated short budget,
/// that can still be minutes per caller with a bounded connection pool behind it.</para>
///
/// <para><b>Placement</b> — after <c>UseRateLimiter()</c> and before <c>UseAuthentication()</c>:
/// <list type="bullet">
/// <item>After the rate limiter, so a 503 storm from a reconnecting SPA stays throttled and
/// <c>/api/auth/login</c> keeps its 50/min cap while sealed.</item>
/// <item>Before authentication, because <c>OidcTicketStore</c> resolves a DbContext inside
/// <c>UseAuthentication()</c>, and before <c>TokenValidityMiddleware</c>, whose three reads are the
/// actual hang.</item>
/// <item>Before <c>LeaderRequiredMiddleware</c>: during a shared-database outage no node can renew
/// its
/// lease, so every node self-demotes and would answer "not the leader" — a symptom that hides the
/// cause.</item>
/// </list></para>
/// </summary>
public sealed class DatabaseAvailabilityMiddleware(RequestDelegate next, IDatabaseAvailability availability)
{
    internal const string AnonymizeSpaPrincipalItem =
        "NodePilot.DatabaseAvailability.AnonymizeSpaPrincipal";

    public async Task InvokeAsync(HttpContext context)
    {
        if (availability.IsServable)
        {
            await next(context);
            return;
        }

        if (!ShouldGate(context))
        {
            // Authentication has not run yet at this point, so the gate cannot replace ctx.User
            // itself. Mark only browser navigations carrying the auth cookie;
            // TokenValidityMiddleware,
            // which runs after UseAuthentication, consumes the marker and strips the principal
            // before
            // any revocation/session/user query. The SPA document can then render the outage banner
            // immediately instead of waiting for the short auth command budget to expire first.
            if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && context.Request.Cookies.ContainsKey(
                    NodePilot.Api.Controllers.AuthController.AuthCookieName))
            {
                context.Items[AnonymizeSpaPrincipalItem] = true;
            }

            await next(context);
            return;
        }

        NodePilot.Api.Telemetry.ApiMetrics.DatabaseRequestsRejected.Add(1);
        await DatabaseUnavailableResponse.WriteUnavailableAsync(
            context, availability.CurrentOutage, context.RequestAborted);
    }

    /// <summary>
    /// Gates every database-backed HTTP surface; lets the static SPA document and health endpoints
    /// through so they can explain and report the outage.
    ///
    /// <para><b>Everything else is load-bearing, not defensive.</b> There is no
    /// <c>UseDefaultFiles</c>
    /// in this pipeline, so <c>GET /</c>, <c>/login</c> and <c>/workflows</c> are served by
    /// <c>MapFallbackToFile("index.html")</c> — an *endpoint*, which runs after this middleware. A
    /// blanket 503 would replace the very document that renders the outage banner, and the user
    /// would
    /// see raw JSON instead of an explanation. <c>/healthz/*</c> needs no explicit carve-out (it
    /// matches
    /// neither prefix) but is worth knowing about: it is how the SPA learns the outage
    /// exists.</para>
    /// </summary>
    internal static bool ShouldGate(HttpContext context)
    {
        var path = context.Request.Path;

        if (path.StartsWithSegments("/api")
            || path.StartsWithSegments("/hubs")
            || path.StartsWithSegments("/signin-oidc"))
            return true;

        // The scrape endpoint is database-free and may deliberately be public so an external
        // monitor
        // can observe an outage. The default/protected endpoint, however, enters authentication
        // before
        // its Authorize metadata can be enforced and must therefore fail here with the same 503
        // body as
        // /api. Endpoint metadata is normally available because routing precedes this middleware;
        // fail closed if a non-standard pipeline invokes /metrics without a selected endpoint.
        if (path.StartsWithSegments("/metrics"))
        {
            var endpoint = context.GetEndpoint();
            return endpoint is null
                || endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0;
        }

        // No query-string heuristic here. `?id=` is caller-controlled and is present on WebSocket,
        // SSE and long-poll requests alike; using it as proof of an established connection lets a
        // spoofed request bypass the breaker and reach the database-backed auth pipeline.
        return false;
    }
}

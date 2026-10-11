using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace NodePilot.Api.Security;

/// <summary>
/// Authenticates the Engine Switcher by its machine-local key (<see cref="SwitcherKeyStore"/>).
/// A request that carries the key header is handled here and nowhere else: it must be a direct
/// loopback connection, may only reach the five endpoints the Switcher needs, and runs as a
/// session-less Admin principal. Everything else is answered with 401/403; a bad key never
/// falls through to another sign-in.
/// </summary>
public sealed partial class SwitcherKeyMiddleware
{
    internal const string PrincipalItem = "NodePilot.Security.SwitcherPrincipal";
    internal const string AuthenticationType = "SwitcherKey";
    internal const string UserName = "switcher";

    private readonly RequestDelegate _next;

    public SwitcherKeyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx, SwitcherKeyStore store)
    {
        if (!ctx.Request.Headers.TryGetValue(SwitcherKeyStore.Header, out var presented))
        {
            await _next(ctx);
            return;
        }

        if (ctx.User.Identity?.IsAuthenticated == true
            || !store.Validate(presented.Count == 1 ? presented[0] : null))
        {
            await RejectAsync(ctx, StatusCodes.Status401Unauthorized, "switcher_key_invalid",
                "The Switcher key is missing, revoked or invalid.");
            return;
        }

        if (!IsDirectLoopback(ctx))
        {
            await RejectAsync(ctx, StatusCodes.Status403Forbidden, "switcher_key_not_local",
                "The Switcher key is only accepted on a direct loopback connection.");
            return;
        }

        if (!IsAllowedRequest(ctx.Request))
        {
            await RejectAsync(ctx, StatusCodes.Status403Forbidden, "switcher_key_scope",
                "The Switcher key does not permit this request.");
            return;
        }

        var identity = new ClaimsIdentity(AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, UserName));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        ctx.User = new ClaimsPrincipal(identity);
        ctx.Items[PrincipalItem] = true;
        await _next(ctx);
    }

    // The client must sit on this machine: a loopback connection, or one whose source address is
    // the server's own address (a local process using the machine name). A reverse proxy on the
    // same host looks the same but adds forwarding headers; those requests are refused.
    private static bool IsDirectLoopback(HttpContext ctx)
    {
        var connection = ctx.Connection;
        if (connection.RemoteIpAddress is not { } remote || connection.LocalIpAddress is not { } local)
            return false;
        var onThisMachine = (IPAddress.IsLoopback(remote) && IPAddress.IsLoopback(local))
            || remote.Equals(local);
        if (!onThisMachine) return false;

        var headers = ctx.Request.Headers;
        // UseForwardedHeaders strips X-Forwarded-* from a trusted proxy and leaves X-Original-*.
        return !headers.ContainsKey("Forwarded")
            && !headers.Keys.Any(name =>
                name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("X-Original-", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsAllowedRequest(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        if (HttpMethods.IsGet(request.Method))
            return path.Equals("/api/workflows", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/operations/graph", StringComparison.OrdinalIgnoreCase);
        return HttpMethods.IsPost(request.Method) && WorkflowActionPath().IsMatch(path);
    }

    [GeneratedRegex("^/api/workflows/[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}/(enable|disable|cancel-all)$")]
    private static partial Regex WorkflowActionPath();

    private static Task RejectAsync(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync($"{{\"code\":\"{code}\",\"message\":\"{message}\"}}");
    }
}

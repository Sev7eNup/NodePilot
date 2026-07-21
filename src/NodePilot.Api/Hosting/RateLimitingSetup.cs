using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Crude per-IP rate-limit caps on the abuse-prone endpoints (login, refresh, webhook,
/// external trigger) plus the ForwardedHeaders setup the limiter depends on. Not a
/// replacement for a real WAF, but fast to add and immediately blocks low-effort
/// brute-force and webhook floods.
/// </summary>
public static class RateLimitingSetup
{
    /// <summary>
    /// Forwarded-headers support. When NodePilot sits behind a reverse proxy (IIS-ARR,
    /// nginx, Azure Front Door, etc.) the direct connection IP is always the proxy's —
    /// without this every rate-limit bucket collapses to a single global partition and
    /// logins/webhooks from one client would 429 everyone else. Only trusted-network ranges
    /// are accepted (configure ForwardedHeaders:KnownProxies or :KnownNetworks to lock down;
    /// we seed private ranges as the safe default for typical on-prem deployments).
    /// </summary>
    public static IServiceCollection AddNodePilotForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Loopback is always trusted; most installs front the API from an on-box proxy.
            // ASPDEPR005: KnownNetworks/Microsoft.AspNetCore.HttpOverrides.IPNetwork are obsolete
            // in .NET 10 — moved to KnownIPNetworks/System.Net.IPNetwork (BCL).
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Loopback, 8));
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.IPv6Loopback, 128));
            // Add any operator-configured upstream networks/proxies.
            foreach (var cidr in configuration.GetSection("ForwardedHeaders:KnownNetworks").GetChildren())
            {
                var v = cidr.Value;
                if (string.IsNullOrWhiteSpace(v)) continue;
                var parts = v.Split('/');
                if (parts.Length == 2
                    && System.Net.IPAddress.TryParse(parts[0], out var ipnet)
                    && int.TryParse(parts[1], out var prefix))
                {
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(ipnet, prefix));
                }
            }
            foreach (var proxy in configuration.GetSection("ForwardedHeaders:KnownProxies").GetChildren())
            {
                if (System.Net.IPAddress.TryParse(proxy.Value, out var ip))
                    options.KnownProxies.Add(ip);
            }
        });
        return services;
    }

    public static IServiceCollection AddNodePilotRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = (context, _) =>
            {
                var policy = context.HttpContext.GetEndpoint()?.Metadata
                    .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "unknown";
                ApiMetrics.RateLimitRejections.Add(1,
                    new(TelemetryConstants.Attributes.RateLimitPolicy, policy),
                    new("source", "rate_limiter"));
                return ValueTask.CompletedTask;
            };

            options.AddPolicy("login",   ctx => IpWindow(ctx, 50, TimeSpan.FromMinutes(1)));
            options.AddPolicy("refresh", ctx => IpWindow(ctx, 20, TimeSpan.FromMinutes(1)));
            options.AddPolicy("webhook", ctx => IpWindow(ctx, 60, TimeSpan.FromMinutes(1)));
            // Audit H-1: external trigger was missing a rate-limit partition entirely. With an
            // invalid key every miss still does a DB Workflow lookup (cheap DoS); with a valid
            // key, an attacker can queue unlimited executions. 30/min/IP is generous for legit
            // callers (a webhook-to-webhook orchestrator typically fires well under that).
            options.AddPolicy("trigger", ctx => IpWindow(ctx, 30, TimeSpan.FromMinutes(1)));
            // AI assistant endpoints: 20/min/IP. Guards against cost-runaway (a user mashing
            // the Generate button in a loop) and against valid Operator credentials being
            // abused from a script. Hardcoded — Operator already has Llm:Enabled as the master
            // switch and Llm:MaxTokens as the per-call cost cap, so no further tuning is needed here.
            options.AddPolicy("ai-generate", ctx => IpWindow(ctx, 20, TimeSpan.FromMinutes(1)));
            // Audit endpoints: Admin-only + a 500-row cap is enough in practice, but without its
            // own limit /api/audit was the only endpoint in the system that let a compromised
            // Admin token put unbounded load on the DB. 60/min/IP is generous for any legitimate
            // reviewer (the UI polls every 15s = 4/min) and only kicks in for script-driven abuse.
            options.AddPolicy("audit", ctx => IpWindow(ctx, 60, TimeSpan.FromMinutes(1)));
            // System-config backup (ADR 0001): Admin-only, but restore is a heavy, transactional
            // bulk write and export reads every secret — a compromised admin token shouldn't be
            // able to hammer either. 10/min/IP is far above any legit operator use (export/restore
            // are rare, deliberate actions) and caps script-driven abuse.
            options.AddPolicy("backup", ctx => IpWindow(ctx, 10, TimeSpan.FromMinutes(1)));
            // System-alert policy preview + test-fire (ADR 0008): both are Admin/Operator but heavy —
            // preview runs a live source sample (DB scans) per call and test-fire does real outbound
            // channel I/O. 20/min/IP is generous for the editor (an operator previews interactively) and
            // caps script-driven abuse of either.
            options.AddPolicy("alerting-heavy", ctx => IpWindow(ctx, 20, TimeSpan.FromMinutes(1)));
        });
        return services;
    }

    /// <summary>
    /// Audit H-4: IPv4 → partition by full /32, IPv6 → partition by /64 so an attacker
    /// controlling a single /64 (the typical ISP allocation) cannot rotate through 2^64
    /// source addresses to bypass the limit.
    /// </summary>
    private static string IpKey(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            return $"v6:{bytes[0]:x2}{bytes[1]:x2}{bytes[2]:x2}{bytes[3]:x2}" +
                   $"{bytes[4]:x2}{bytes[5]:x2}{bytes[6]:x2}{bytes[7]:x2}";
        }
        return $"v4:{ip}";
    }

    private static RateLimitPartition<string> IpWindow(HttpContext ctx, int limit, TimeSpan window) =>
        RateLimitPartition.GetSlidingWindowLimiter(
            IpKey(ctx),
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = window,
                SegmentsPerWindow = 4,
                QueueLimit = 0,
            });
}

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodePilot.Api.Hosting;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Pins the security-header contract of <see cref="SecurityPipelineSetup"/>. The CSP is the
/// production-only XSS floor: no test environment renders the SPA behind it (Development and
/// the hermetic E2E suite skip this middleware), so a directive regression would otherwise
/// surface only on a deployed server, for example a tightened style directive silently
/// breaking the CodeMirror and Monaco editors.
/// </summary>
public sealed class SecurityPipelineSetupTests
{
    private static async Task<HttpResponseMessage> GetThroughPipelineAsync(
        Action<WebApplication>? preSecurityMiddleware = null)
    {
        // The security-headers extension no-ops in Development, so the headers only appear
        // when the environment is set to Production, as here. Its parameterless
        // UseExceptionHandler requires a ProblemDetails service; Program.cs registers one,
        // so the test host mirrors that.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        var app = builder.Build();

        preSecurityMiddleware?.Invoke(app);
        app.UseNodePilotSecurityHeaders();
        app.MapGet("/probe", () => Results.Text("ok"));

        await app.StartAsync();
        try
        {
            return await app.GetTestClient().GetAsync("/probe");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task UseNodePilotSecurityHeaders_SetsBaselineHeaders()
    {
        using var response = await GetThroughPipelineAsync();

        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle()
            .Which.Should().Be("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().ContainSingle()
            .Which.Should().Be("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().ContainSingle()
            .Which.Should().Be("no-referrer");
    }

    [Fact]
    public async Task UseNodePilotSecurityHeaders_Csp_AllowsInlineStylesButKeepsScriptsStrict()
    {
        using var response = await GetThroughPipelineAsync();

        var csp = response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle().Subject;

        // Style injection must stay permitted: CodeMirror 6 (style-mod) and Monaco create
        // runtime <style> elements; blocking them renders the designer's editors unstyled.
        csp.Should().Contain("style-src 'self' 'unsafe-inline';");

        // The load-bearing XSS floor: scripts remain same-origin only, no inline, no eval.
        csp.Should().Contain("script-src 'self';");
        csp.Should().NotContain("script-src 'self' 'unsafe");
        csp.Should().NotContain("unsafe-eval");

        csp.Should().Contain("default-src 'self';");
        csp.Should().Contain("object-src 'none';");
        csp.Should().Contain("frame-ancestors 'none';");
        csp.Should().Contain("base-uri 'self'");
    }

    [Fact]
    public async Task UseNodePilotSecurityHeaders_PresetCsp_IsNotOverwritten()
    {
        using var response = await GetThroughPipelineAsync(app =>
            app.Use(async (ctx, next) =>
            {
                ctx.Response.Headers["Content-Security-Policy"] = "preset-by-earlier-middleware";
                await next();
            }));

        response.Headers.GetValues("Content-Security-Policy").Should().ContainSingle()
            .Which.Should().Be("preset-by-earlier-middleware");
    }

    /// <summary>
    /// Mirrors the Program.cs pipeline for a TLS-terminating reverse proxy that speaks plain
    /// HTTP to Kestrel. <c>UseHsts()</c> short-circuits on <c>!Request.IsHttps</c>, so the
    /// relative order of ForwardedHeaders and the security headers decides whether HSTS
    /// reaches the wire at all, a failure that config inspection alone cannot reveal.
    /// </summary>
    private static async Task<HttpResponseMessage> GetThroughProxiedPipelineAsync(bool forwardedHeadersFirst)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        builder.Services.AddNodePilotForwardedHeaders(builder.Configuration);
        // HstsOptions.ExcludedHosts skips localhost / 127.0.0.1 / [::1] by default, and TestServer
        // only accepts its own host. Clearing the list is what lets the assertion see the header;
        // it does not affect what the ordering under test decides.
        builder.Services.Configure<HstsOptions>(o => o.ExcludedHosts.Clear());
        var app = builder.Build();

        // TestServer leaves RemoteIpAddress null, but ForwardedHeaders only honours X-Forwarded-*
        // from a trusted peer. Loopback is the seeded default (RateLimitingSetup) and matches the
        // common on-box-proxy deployment.
        app.Use(async (ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next();
        });

        if (forwardedHeadersFirst)
        {
            app.UseForwardedHeaders();
            app.UseNodePilotSecurityHeaders();
        }
        else
        {
            app.UseNodePilotSecurityHeaders();
            app.UseForwardedHeaders();
        }

        app.MapGet("/probe", (HttpContext ctx) =>
            Results.Text($"scheme={ctx.Request.Scheme};https={ctx.Request.IsHttps};host={ctx.Request.Host};ip={ctx.Connection.RemoteIpAddress}"));

        await app.StartAsync();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            request.Headers.Add("X-Forwarded-Proto", "https");
            return await app.GetTestClient().SendAsync(request);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForwardedHeaders_RunningFirst_EmitsHstsForProxyTerminatedTls()
    {
        using var response = await GetThroughProxiedPipelineAsync(forwardedHeadersFirst: true);
        var probe = await response.Content.ReadAsStringAsync();

        response.Headers.Contains("Strict-Transport-Security").Should().BeTrue(
            $"X-Forwarded-Proto: https must be applied before UseHsts() reads Request.IsHttps (probe saw {probe})");
    }

    [Fact]
    public async Task ForwardedHeaders_RunningAfterSecurityHeaders_SilentlyDropsHsts()
    {
        // Pins the ordering regression: security headers running before ForwardedHeaders
        // means UseHsts() never sees the proxied https scheme.
        using var response = await GetThroughProxiedPipelineAsync(forwardedHeadersFirst: false);

        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse(
            "with the forwarded scheme applied too late, HSTS sees plain HTTP and emits nothing");
    }
}

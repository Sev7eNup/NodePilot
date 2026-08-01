using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
/// surface only on a deployed server — as the 2026-08-01 lab rollout proved when
/// style-src-elem 'self' silently broke every CodeMirror editor.
/// </summary>
public sealed class SecurityPipelineSetupTests
{
    private static async Task<HttpResponseMessage> GetThroughPipelineAsync(
        Action<WebApplication>? preSecurityMiddleware = null)
    {
        // Production: the extension no-ops in Development (line 13), so the headers only exist
        // outside it. Its parameterless UseExceptionHandler requires a ProblemDetails service —
        // Program.cs registers one, so the test host mirrors that.
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
}

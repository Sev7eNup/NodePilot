using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NodePilot.Api.Controllers;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public class CsrfMiddlewareTests
{
    private const string ValidCsrf = "csrf-token-value-44-chars-aaaaaaaaaaaaaaaaaaaa";

    private static HttpContext NewCtx(
        string method,
        string path = "/api/workflows",
        string? authCookie = "auth-jwt",
        string? csrfCookie = ValidCsrf,
        string? csrfHeader = ValidCsrf,
        string? authorizationHeader = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();

        var cookies = new Dictionary<string, string>();
        if (authCookie is not null) cookies[AuthController.AuthCookieName] = authCookie;
        if (csrfCookie is not null) cookies[AuthController.CsrfCookieName] = csrfCookie;
        ctx.Request.Headers["Cookie"] = string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));
        // DefaultHttpContext builds the Cookies collection lazily from the header, so setting
        // the header above is enough as long as nothing has read .Cookies yet. Also build a
        // CookieCollection explicitly so the test does not depend on that timing.
        var cookieFeature = new Microsoft.AspNetCore.Http.Features.RequestCookiesFeature(ctx.Features);
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IRequestCookiesFeature>(cookieFeature);

        if (csrfHeader is not null) ctx.Request.Headers["X-CSRF-Token"] = csrfHeader;
        if (authorizationHeader is not null) ctx.Request.Headers["Authorization"] = authorizationHeader;
        return ctx;
    }

    private static (CsrfMiddleware middleware, Func<bool> nextWasCalled) Build()
    {
        bool called = false;
        var mw = new CsrfMiddleware(_ => { called = true; return Task.CompletedTask; });
        return (mw, () => called);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    public async Task SafeMethods_AlwaysSkipped(string method)
    {
        var ctx = NewCtx(method, csrfHeader: null);
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task PostWithoutAuthCookie_Skipped()
    {
        // No np_auth cookie means either unauthenticated (downstream returns 401) or Bearer
        // auth (browsers never auto-attach Bearer headers, so CSRF does not apply here).
        var ctx = NewCtx("POST", authCookie: null, csrfCookie: null, csrfHeader: null);
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeTrue();
    }

    [Fact]
    public async Task PostWithBearerHeader_Skipped()
    {
        // Bearer requests come from non-browser clients; the browser never auto-attaches
        // an Authorization header, so CSRF cannot exploit them.
        var ctx = NewCtx("POST", authorizationHeader: "Bearer some.jwt.token", csrfCookie: null, csrfHeader: null);
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeTrue();
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Negotiate YIIB...")]
    [InlineData("Digest username=\"foo\"")]
    public async Task PostWithNonBearerAuthHeader_StillEnforcesCsrf(string authHeader)
    {
        // An attacker could attach an Authorization header of an unsupported scheme alongside
        // a legitimate np_auth cookie. JwtBearer ignores the header, so skipping CSRF whenever
        // any Authorization header is present would leave this request unprotected.
        var ctx = NewCtx("POST", authorizationHeader: authHeader, csrfHeader: null);
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ReadBody(ctx).Should().Contain("csrf_mismatch");
    }

    [Fact]
    public async Task PostToLoginEndpoint_Skipped()
    {
        // /api/auth/login is the cookie-bootstrap path. Even if a stale cookie is replayed,
        // login validates the password and sets a fresh cookie regardless. Gating it on
        // CSRF is pointless and would actually break first-login bootstrap.
        var ctx = NewCtx("POST", path: "/api/auth/login", csrfHeader: null);
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeTrue();
    }

    [Fact]
    public async Task PostToWindowsSsoEndpoint_Skipped()
    {
        // /api/auth/windows is a cookie-bootstrap endpoint like login: it is Negotiate-gated
        // and mints a fresh np_auth + np_csrf pair on success. A stale np_auth cookie without
        // a matching np_csrf must not 403 the SSO handshake before it runs.
        var ctx = NewCtx("POST", path: "/api/auth/windows", csrfHeader: null);
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeTrue();
    }

    [Fact]
    public async Task PostWithMatchingCsrf_PassesThrough()
    {
        var ctx = NewCtx("POST");
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task PostMissingCsrfHeader_Returns403()
    {
        var ctx = NewCtx("POST", csrfHeader: null);
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        var body = ReadBody(ctx);
        body.Should().Contain("csrf_mismatch");
    }

    [Fact]
    public async Task PostMissingCsrfCookie_Returns403()
    {
        var ctx = NewCtx("POST", csrfCookie: null);
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task PostWithMismatchedCsrf_Returns403()
    {
        var ctx = NewCtx("POST", csrfHeader: "tampered-different-value");
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task PostWithEmptyCsrfCookie_Returns403()
    {
        // An empty cookie must NOT pass even if the header is also empty — that would
        // let an attacker who can plant an empty cookie bypass with no header.
        var ctx = NewCtx("POST", csrfCookie: "", csrfHeader: "");
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task OtherMutatingMethods_AlsoEnforced(string method)
    {
        var ctx = NewCtx(method, csrfHeader: null);
        var (mw, called) = Build();

        await mw.InvokeAsync(ctx);

        called().Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEnd();
    }
}

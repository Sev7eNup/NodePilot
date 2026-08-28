using System.Net;
using System.Text;
using FluentAssertions;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Enums;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Pins the double-submit CSRF gate (CsrfMiddleware) through the real pipeline; the
/// direct-controller suite never runs the middleware, so it cannot cover this.
/// A cookie-authenticated mutating request must echo the JS-readable np_csrf cookie in the
/// X-CSRF-Token header, or CsrfMiddleware rejects it with 403 and code "csrf_mismatch"
/// before any role gate or action logic runs.
/// </summary>
[Collection(ApiPipelineCollection.Name)] // serialize full-host boots — see ApiPipelineCollection
public sealed class CsrfSmokeTests
{
    [Fact]
    public async Task CookieAuthenticatedMutation_RequiresCsrfHeader()
    {
        using var factory = new ApiPipelineFactory();
        const string password = "Csrf-Smoke-1!";
        await factory.CreateUserAsync("csrf-admin", password, UserRole.Admin);

        using var client = factory.CreateClient();
        var session = await ApiPipelineFactory.LoginAsync(client, "csrf-admin", password);

        // Same valid request twice; only the header differs. Admin role + valid body mean
        // nothing except the CSRF gate can produce a 403 here.
        const string machineBody = """{"name":"csrf-smoke","hostname":"csrf-host"}""";

        // 1) Without the header: CsrfMiddleware must reject with its pinned 403 + code. The
        //    np_auth cookie is attached automatically by the client's cookie container.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/machines")
               { Content = new StringContent(machineBody, Encoding.UTF8, "application/json") })
        {
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "a cookie-authenticated mutating request without X-CSRF-Token must be " +
                "rejected by CsrfMiddleware — anything else means the CSRF gate moved or died");
            body.Should().Contain("csrf_mismatch",
                "the rejection must be the CSRF middleware's own machine-readable body, " +
                "not a coincidental 403 from a later gate");
        }

        // 2) With the header: the request must get past the CSRF gate. Any non-CSRF status
        //    would do; with an Admin session and a valid body the deterministic outcome is
        //    201, which we pin as the strongest available proof the gate opened.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/machines")
               { Content = new StringContent(machineBody, Encoding.UTF8, "application/json") })
        {
            request.Headers.Add("X-CSRF-Token", session.CsrfToken);
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.Created,
                $"the same request WITH the CSRF header must clear the gate and reach the action; body: {body}");
        }
    }
}

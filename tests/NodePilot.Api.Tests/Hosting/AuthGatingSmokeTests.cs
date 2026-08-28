using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Pipeline-level authentication gate: every endpoint the host actually maps (discovered at
/// runtime via ApiExplorer, not by source scanning) must reject an unauthenticated request
/// before its action runs. Direct-controller tests bypass routing, [Authorize] metadata, and
/// the middleware chain entirely, so they cannot catch a deleted [Authorize] attribute.
///
/// The expected status is always 401. Middleware order in Program.cs is UseRateLimiter,
/// DatabaseAvailability, UseAuthentication, TokenValidity, CsrfMiddleware, UseAuthorization,
/// MapControllers (in that order), and nothing before authorization can answer first on an
/// unauthenticated request:
/// <list type="bullet">
///   <item>CsrfMiddleware.ShouldEnforce bails out when the request carries no np_auth
///     cookie, so an anonymous mutating request is never swallowed by the CSRF 403 — it
///     reaches the authorization middleware, whose default JwtBearer challenge answers 401
///     for every verb.</item>
///   <item>The rate-limit policies (login/refresh/webhook/trigger/ai-generate/audit/backup/
///     alerting-heavy) are per-IP windows with limits of at least 10/min; this test sends
///     exactly one request per endpoint, so a 429 cannot mask a missing gate.</item>
///   <item>TokenValidityMiddleware only re-validates already authenticated principals; it
///     passes anonymous requests straight through.</item>
/// </list>
///
/// SCIM endpoints (ScimUsers/ScimGroups/ScimDiscovery) are deliberately not skipped: they
/// carry no [Authorize] at all — their gate is the [ScimAuthorize] MVC authorization filter
/// (ScimAuthorizationFilter), which answers 401 application/scim+json whenever no valid
/// SCIM bearer token is presented (and always, while SCIM is unconfigured as in this host).
/// That satisfies the same "never reaches the action" invariant, so they participate in the
/// plain-401 expectation like everything else.
/// </summary>
[Collection(ApiPipelineCollection.Name)] // serialize full-host boots — see ApiPipelineCollection
public sealed class AuthGatingSmokeTests
{
    private readonly ITestOutputHelper _output;

    public AuthGatingSmokeTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Endpoints that provably never reach their action unauthenticated but cannot answer a
    /// clean 401 in this host. Keep this list small and every entry justified — an
    /// unexplained addition here is how a real auth hole gets waved through.
    /// Keys are "METHOD relative/path" (case-insensitive). Allowlisted endpoints are still
    /// asserted non-2xx: the invariant that the action never runs holds regardless.
    /// </summary>
    private static readonly Dictionary<string, string> KnownNonStandardRejections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST api/Auth/windows"] =
            "[Authorize(AuthenticationSchemes = \"WindowsChallenge\")] — AuthenticationSetup " +
            "registers the Negotiate handler only when Authentication:Windows:Enabled=true, " +
            "which this host (correctly) leaves off. Challenging the unregistered scheme " +
            "throws InvalidOperationException inside the authorization middleware, which the " +
            "exception handler surfaces as a 500. The action is still never reached; in a " +
            "production host with SSO enabled the Negotiate handler answers 401 itself.",
    };

    [Fact]
    public async Task EveryMappedEndpoint_WithoutAllowAnonymous_RejectsUnauthenticatedRequestsWith401()
    {
        using var factory = new ApiPipelineFactory();
        using var client = factory.CreateClient();

        var provider = factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>();
        // Dedup on (verb, template): ApiExplorer may emit one ApiDescription per supported
        // request format for the same action.
        var endpoints = provider.ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .Where(api => api.RelativePath is not null)
            .GroupBy(api => $"{api.HttpMethod ?? "GET"} {api.RelativePath}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Scanner meta-check (repo pattern: guards verify themselves): if an ApiExplorer or
        // conventions regression empties the discovery, this test must fail loudly instead
        // of green-lighting a zero-endpoint sweep.
        endpoints.Count.Should().BeGreaterThan(150,
            "runtime endpoint discovery via IApiDescriptionGroupCollectionProvider collapsed — " +
            "the auth-gating sweep would be meaningless on this few endpoints");

        var violations = new List<string>();
        var anonymousCount = 0;
        var checkedCount = 0;

        foreach (var api in endpoints)
        {
            if (AllowsAnonymous(api)) { anonymousCount++; continue; }
            checkedCount++;

            var method = api.HttpMethod ?? "GET";
            var key = $"{method} {api.RelativePath}";
            var allowlistedReason = KnownNonStandardRejections.GetValueOrDefault(key);

            using var request = new HttpRequestMessage(new HttpMethod(method), "/" + FillRouteTemplate(api));
            if (method is "POST" or "PUT" or "PATCH")
                request.Content = BuildBodyMatchingConsumesConstraint(api);

            HttpStatusCode status;
            try
            {
                using var response = await client.SendAsync(request);
                status = response.StatusCode;
            }
            catch (Exception ex)
            {
                if (allowlistedReason is not null) continue; // exception means action never ran
                violations.Add($"{key} → request threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (allowlistedReason is not null)
            {
                // Weaker but still load-bearing assertion for the documented exceptions:
                // whatever the status, it must never be a success (the action must not run).
                if ((int)status is >= 200 and < 300)
                    violations.Add($"{key} → {(int)status} {status} DESPITE allowlist entry ({allowlistedReason})");
                // Self-pruning: an allowlist entry for an endpoint that answers a clean 401
                // is stale and must be removed — otherwise the list silently accumulates
                // blind spots that would wave a future real regression through.
                else if (status == HttpStatusCode.Unauthorized)
                    violations.Add($"{key} → 401 — allowlist entry is STALE, remove it (documented reason no longer applies: {allowlistedReason})");
                else
                    _output.WriteLine($"allowlisted: {key} → {(int)status} {status} (action not reached; see allowlist reason)");
                continue;
            }

            if (status != HttpStatusCode.Unauthorized)
                violations.Add($"{key} → {(int)status} {status}");
        }

        _output.WriteLine(
            $"Discovered {endpoints.Count} mapped endpoints: {checkedCount} checked for the 401 gate, " +
            $"{anonymousCount} [AllowAnonymous] skipped, {KnownNonStandardRejections.Count} allowlisted.");

        violations.Should().BeEmpty(
            "every mapped endpoint without [AllowAnonymous] must answer 401 to an " +
            "unauthenticated request. A non-401 here usually means someone dropped an " +
            "[Authorize] attribute (2xx/404-from-action/400-model-validation = the request " +
            "reached the action or its binding), and it must turn CI red. Violations:\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// True when the action opts out of authentication. Checked in both places the marker
    /// can live: endpoint metadata (how [AllowAnonymous] on action or controller surfaces
    /// under endpoint routing) and the legacy MVC filter pipeline (IAllowAnonymousFilter),
    /// so neither representation can slip past the sweep.
    /// </summary>
    private static bool AllowsAnonymous(ApiDescription api)
        => api.ActionDescriptor.EndpointMetadata?.OfType<IAllowAnonymous>().Any() == true
           || api.ActionDescriptor.FilterDescriptors?.Any(f => f.Filter is IAllowAnonymousFilter) == true;

    /// <summary>
    /// Substitutes route template tokens with values that satisfy their constraints, so the
    /// request matches the intended endpoint instead of dying in routing with a 404 that
    /// this test could not tell apart from a missing gate. Types come from the path-bound
    /// parameter descriptions; leftover tokens (catch-alls etc.) get a plain segment — any
    /// value works there because the request must be rejected at authorization anyway.
    /// </summary>
    private static string FillRouteTemplate(ApiDescription api)
    {
        var path = api.RelativePath!;
        foreach (var parameter in api.ParameterDescriptions)
        {
            if (parameter.Source != BindingSource.Path) continue;
            var value =
                parameter.Type == typeof(Guid) || parameter.Type == typeof(Guid?) ? Guid.NewGuid().ToString()
                : parameter.Type == typeof(int) || parameter.Type == typeof(long) ? "1"
                : "x";
            // Tolerates every token spelling: {name}, {name?}, {name:constraint}, {*name},
            // {**name}.
            path = Regex.Replace(
                path,
                $@"\{{\*{{0,2}}{Regex.Escape(parameter.Name)}(:[^}}]*)?\??\}}",
                value,
                RegexOptions.IgnoreCase);
        }
        return Regex.Replace(path, @"\{[^}]+\}", "x");
    }

    /// <summary>
    /// Body whose content type matches the endpoint's [Consumes] constraint. Necessary
    /// because ConsumesMatcherPolicy answers 415 during routing, before authentication,
    /// when the content type matches no candidate, which would mask the auth verdict for
    /// the multipart backup endpoints and the XML SCOrch import.
    /// </summary>
    private static HttpContent BuildBodyMatchingConsumesConstraint(ApiDescription api)
    {
        var mediaType = api.ActionDescriptor.EndpointMetadata?
                            .OfType<ConsumesAttribute>()
                            .SelectMany(c => c.ContentTypes)
                            .FirstOrDefault()
                        ?? api.SupportedRequestFormats.FirstOrDefault()?.MediaType
                        ?? "application/json";

        if (mediaType.Contains("multipart", StringComparison.OrdinalIgnoreCase))
            return new MultipartFormDataContent();
        if (mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            return new StringContent("<smoke />", Encoding.UTF8, "application/xml");
        return new StringContent("{}", Encoding.UTF8, "application/json");
    }
}

using System.Net;
using System.Text;
using FluentAssertions;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Enums;
using Xunit;

namespace NodePilot.Api.Tests.Hosting;

/// <summary>
/// Pins the documented role matrix (CLAUDE.md "## Autorisierung") end-to-end: real users
/// seeded with the production BCrypt path, real logins through POST /api/auth/login, real
/// cookie + CSRF handling, real [Authorize(Roles=...)] evaluation by the authorization
/// middleware. Direct-controller tests can't cover this — they hand-craft ClaimsPrincipals
/// and never execute the attribute metadata.
///
/// Verdict semantics per row:
/// <list type="bullet">
///   <item>Denied: expected 403 from the ROLE gate. All mutating requests carry a valid
///     X-CSRF-Token, and the body is additionally checked for the csrf_mismatch marker, so
///     a CSRF 403 (which fires BEFORE authorization in the pipeline) can never be mistaken
///     for a role denial.</item>
///   <item>Passed gate: anything but 401/403. Rows use nonexistent GUIDs / minimal bodies,
///     so the expected happy statuses are 404 (resource lookup after the gate), 400 (model
///     or domain validation after the gate) or 2xx — every one of them proves the request
///     got PAST authentication + authorization.</item>
/// </list>
///
/// One factory boot, three logins, all rows driven sequentially — keeps the WAF cost of the
/// whole matrix at a single host start.
/// </summary>
[Collection(ApiPipelineCollection.Name)] // serialize full-host boots — see ApiPipelineCollection
public sealed class RoleMatrixSmokeTests
{
    private sealed record UserSession(string Label, HttpClient Client, string CsrfToken);

    private sealed record MatrixRow(
        string Method,
        string Url,
        string? JsonBody,
        UserSession Session,
        bool ExpectDenied,
        string DocumentedRule);

    [Fact]
    public async Task DocumentedRoleMatrix_HoldsEndToEndThroughRealLogins()
    {
        using var factory = new ApiPipelineFactory();

        const string password = "RoleMatrix-Smoke-1!";
        await factory.CreateUserAsync("smoke-admin", password, UserRole.Admin);
        await factory.CreateUserAsync("smoke-operator", password, UserRole.Operator);
        await factory.CreateUserAsync("smoke-viewer", password, UserRole.Viewer);

        // One client per user: the cookie container is per-client, so sharing one client
        // would overwrite the previous user's np_auth cookie on each login.
        using var adminClient = factory.CreateClient();
        using var operatorClient = factory.CreateClient();
        using var viewerClient = factory.CreateClient();

        var admin = new UserSession("Admin", adminClient,
            (await ApiPipelineFactory.LoginAsync(adminClient, "smoke-admin", password)).CsrfToken);
        var op = new UserSession("Operator", operatorClient,
            (await ApiPipelineFactory.LoginAsync(operatorClient, "smoke-operator", password)).CsrfToken);
        var viewer = new UserSession("Viewer", viewerClient,
            (await ApiPipelineFactory.LoginAsync(viewerClient, "smoke-viewer", password)).CsrfToken);

        // Bodies: minimal but VALID where the positive row should demonstrate a clean pass
        // (workflows/machines create → 201); empty where a 400 after the gate is the point
        // (alerting rules). Random GUIDs make the delete/cancel rows deterministic 404s.
        var workflowBody = """{"name":"Role matrix smoke","definitionJson":"{\"nodes\":[],\"edges\":[]}"}""";
        var machineBody = """{"name":"role-matrix-smoke","hostname":"smoke-host"}""";

        var rows = new List<MatrixRow>
        {
            // POST /api/workflows — Admin/Operator create; Viewer read-only.
            new("POST", "/api/workflows", workflowBody, viewer, ExpectDenied: true,
                "POST /api/workflows: Viewer → 403"),
            new("POST", "/api/workflows", workflowBody, op, ExpectDenied: false,
                "POST /api/workflows: Operator allowed (Root FolderEditor default grant)"),

            // DELETE /api/workflows/{id} — Admin-only.
            new("DELETE", $"/api/workflows/{Guid.NewGuid()}", null, op, ExpectDenied: true,
                "DELETE /api/workflows/{id}: Operator → 403"),
            new("DELETE", $"/api/workflows/{Guid.NewGuid()}", null, admin, ExpectDenied: false,
                "DELETE /api/workflows/{id}: Admin passes the gate (404 for a random id)"),

            // GET /api/credentials — Admin/Operator; Viewer excluded at controller level.
            new("GET", "/api/credentials", null, viewer, ExpectDenied: true,
                "GET /api/credentials: Viewer → 403"),
            new("GET", "/api/credentials", null, op, ExpectDenied: false,
                "GET /api/credentials: Operator allowed"),

            // POST /api/machines — Admin/Operator.
            new("POST", "/api/machines", machineBody, viewer, ExpectDenied: true,
                "POST /api/machines: Viewer → 403"),
            new("POST", "/api/machines", machineBody, op, ExpectDenied: false,
                "POST /api/machines: Operator allowed"),

            // POST /api/alerting/rules — controller allows Admin/Operator, mutation is
            // Admin-only. The empty body 400s for Admin, which still proves gate passage.
            new("POST", "/api/alerting/rules", "{}", op, ExpectDenied: true,
                "POST /api/alerting/rules: Operator → 403 (rule mutations are Admin-only)"),
            new("POST", "/api/alerting/rules", "{}", admin, ExpectDenied: false,
                "POST /api/alerting/rules: Admin passes the gate (400 for the empty draft)"),

            // DELETE /api/machines/{id} — Admin-only.
            new("DELETE", $"/api/machines/{Guid.NewGuid()}", null, op, ExpectDenied: true,
                "DELETE /api/machines/{id}: Operator → 403"),
            new("DELETE", $"/api/machines/{Guid.NewGuid()}", null, admin, ExpectDenied: false,
                "DELETE /api/machines/{id}: Admin passes the gate (404 for a random id)"),

            // POST /api/executions/{id}/cancel — Admin/Operator.
            new("POST", $"/api/executions/{Guid.NewGuid()}/cancel", null, viewer, ExpectDenied: true,
                "POST /api/executions/{id}/cancel: Viewer → 403"),
            new("POST", $"/api/executions/{Guid.NewGuid()}/cancel", null, op, ExpectDenied: false,
                "POST /api/executions/{id}/cancel: Operator passes the gate (404 for a random id)"),
        };

        var violations = new List<string>();

        foreach (var row in rows)
        {
            using var request = new HttpRequestMessage(new HttpMethod(row.Method), row.Url);
            if (row.JsonBody is not null)
                request.Content = new StringContent(row.JsonBody, Encoding.UTF8, "application/json");
            if (row.Method != "GET")
            {
                // Cookie-authenticated mutations must reflect the CSRF token — including the
                // deny rows, so their 403 provably comes from the role gate, not from
                // CsrfMiddleware (which runs earlier and also answers 403).
                request.Headers.Add("X-CSRF-Token", row.Session.CsrfToken);
            }

            using var response = await row.Session.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (row.ExpectDenied)
            {
                if (response.StatusCode != HttpStatusCode.Forbidden)
                    violations.Add($"{row.DocumentedRule} — {row.Session.Label} got {(int)response.StatusCode} {response.StatusCode}, expected 403");
                else if (body.Contains("csrf_mismatch", StringComparison.Ordinal))
                    violations.Add($"{row.DocumentedRule} — 403 came from the CSRF middleware, not the role gate: {body}");
            }
            else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                violations.Add($"{row.DocumentedRule} — {row.Session.Label} was rejected with {(int)response.StatusCode} {response.StatusCode}: {body}");
            }
        }

        violations.Should().BeEmpty(
            "the CLAUDE.md role matrix must hold through the real HTTP pipeline — a mismatch " +
            "means a role gate was widened, narrowed or dropped. Violations:\n" +
            string.Join("\n", violations));
    }
}

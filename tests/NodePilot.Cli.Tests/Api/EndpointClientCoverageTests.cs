using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NodePilot.Cli.Tests.Api;

/// <summary>
/// Enforces the CLAUDE.md rule "Jeder neue API-Endpoint braucht beide Clients" at the level
/// nothing guarded before: ENDPOINT EXISTENCE. <see cref="ApiDtoParityTests"/> compares
/// fields of DTOs a client already declares — a client that never adds the type (or never
/// calls the route) was invisible. This guard scans the controllers for their route
/// templates and each client project for the <c>"api/…"</c> URL literals it actually
/// requests, then requires every endpoint route to be reachable from both clients unless it
/// is a documented known gap.
///
/// <para>Matching is path-shape only (parameters normalized to <c>*</c>, query strings
/// stripped, case-insensitive) and deliberately ignores the HTTP method — the invariant is
/// "some client code path reaches this route", which is exactly the level at which the
/// custom-activities surface went missing without anything failing.</para>
/// </summary>
public sealed class EndpointClientCoverageTests
{
    public static TheoryData<string> ClientProjects() => new("Cli", "Mcp");

    /// <summary>
    /// Documented gaps per client. Same contract as the DTO guard's known-gaps list: every
    /// entry names what the gap costs (or why it is deliberate), and
    /// <see cref="KnownGaps_AreAllStillReal"/> fails on entries that have been closed, so
    /// the list cannot rot into a blanket opt-out. Closing a gap = add the command/tool and
    /// delete the entry here.
    /// </summary>
    // Shared rationales — deliberate architectural gaps that apply to whole endpoint families.
    private const string ScimSurface = "DELIBERATE: SCIM is the IdP-facing wire protocol (Okta/Entra provisioning) — never a human client surface";
    private const string BrowserAuthFlow = "DELIBERATE: browser redirect/Negotiate flow — impossible for a headless client; np auth login covers local/LDAP";
    private const string SpaBootstrap = "DELIBERATE: SPA-internal bootstrap/UI surface, not an automation target";
    private const string InteractiveAiSse = "DELIBERATE: interactive SSE surface for the designer/knowledge chat UI; clients have no streaming UX";
    private const string WebhookIngress = "DELIBERATE: external webhook ingress — callers are third-party systems, not our clients";
    private const string CustomActivityGap = "audit finding F1: the custom-activities surface has NO client — close by adding np custom-activity + MCP tools";

    private static readonly Dictionary<string, string> KnownCliGaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api/activity-catalog"] = "np renders no activity palette; the MCP server serves the catalog in-proc from Core",
        ["api/admin/scim-groups/*/reactivate"] = "SCIM tombstone administration is a UI-only surface",
        ["api/admin/scim-groups/tombstones"] = "SCIM tombstone administration is a UI-only surface",
        ["api/admin/settings/test/ldap"] = "np settings has smtp/llm probes but no LDAP probe",
        ["api/ai/chat"] = InteractiveAiSse,
        ["api/ai/chat/activity/*"] = InteractiveAiSse,
        ["api/ai/chat/applied"] = InteractiveAiSse,
        ["api/ai/generate-script"] = InteractiveAiSse,
        ["api/ai/generate-workflow"] = InteractiveAiSse,
        ["api/ai/knowledge/ask"] = InteractiveAiSse,
        ["api/ai/knowledge/capabilities"] = InteractiveAiSse,
        ["api/alerting/rules/*/disable"] = "np alerting toggles via PUT update; the dedicated enable/disable endpoints have no CLI verb",
        ["api/alerting/rules/*/enable"] = "np alerting toggles via PUT update; the dedicated enable/disable endpoints have no CLI verb",
        ["api/alerting/system/preview"] = "system-policy preview is a UI builder affordance",
        ["api/audit/export"] = "np audit list exists but cannot download the CSV export",
        ["api/auth/oidc"] = BrowserAuthFlow,
        ["api/auth/oidc/callback"] = BrowserAuthFlow,
        ["api/auth/windows"] = BrowserAuthFlow,
        ["api/custom-activities"] = CustomActivityGap,
        ["api/custom-activities/*"] = CustomActivityGap,
        ["api/custom-activities/*/disable"] = CustomActivityGap,
        ["api/custom-activities/*/enable"] = CustomActivityGap,
        ["api/custom-activities/*/rollback/*"] = CustomActivityGap,
        ["api/custom-activities/*/versions"] = CustomActivityGap,
        ["api/custom-activities/export"] = CustomActivityGap,
        ["api/custom-activities/import"] = CustomActivityGap,
        ["api/dbadmin/tables"] = "np db covers info+read-only query; the table browser is a UI surface",
        ["api/dbadmin/tables/*/rows"] = "np db covers info+read-only query; the table browser is a UI surface",
        ["api/diagnostics/support-events"] = "np has no support-log surface (the MCP server has read tools)",
        ["api/diagnostics/support-events/export"] = "np has no support-log surface (the MCP server has read tools)",
        ["api/diagnostics/support-log"] = "np has no support-log surface (the MCP server has read tools)",
        ["api/diagnostics/support-log/download"] = "np has no support-log surface (the MCP server has read tools)",
        ["api/maintenance-windows/affecting/*"] = "designer hint endpoint; np maintenance covers CRUD",
        ["api/observability/config"] = SpaBootstrap,
        ["api/observability/dashboards/*"] = SpaBootstrap,
        ["api/scim/v2/groups"] = ScimSurface,
        ["api/scim/v2/groups/*"] = ScimSurface,
        ["api/scim/v2/resourcetypes"] = ScimSurface,
        ["api/scim/v2/schemas"] = ScimSurface,
        ["api/scim/v2/schemas/*"] = ScimSurface,
        ["api/scim/v2/serviceproviderconfig"] = ScimSurface,
        ["api/scim/v2/users"] = ScimSurface,
        ["api/scim/v2/users/*"] = ScimSurface,
        ["api/system/host-info"] = "UI About surface",
        ["api/users/*/reactivate"] = "np user has no reactivate verb (deactivate-only)",
        ["api/webhooks/*/*"] = WebhookIngress,
    };

    private static readonly Dictionary<string, string> KnownMcpGaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["api/activity-catalog"] = "the MCP server serves the catalog in-proc from NodePilot.Core instead",
        ["api/admin/scim-groups/*/reactivate"] = "SCIM tombstone administration is a UI-only surface",
        ["api/admin/scim-groups/tombstones"] = "SCIM tombstone administration is a UI-only surface",
        ["api/admin/settings"] = "no MCP admin-settings tools — settings mutation via agent considered too destructive",
        ["api/admin/settings/*"] = "no MCP admin-settings tools — settings mutation via agent considered too destructive",
        ["api/admin/settings/effective-sizing"] = "no MCP admin-settings tools",
        ["api/admin/settings/status"] = "no MCP admin-settings tools",
        ["api/admin/settings/system-info"] = "no MCP admin-settings tools",
        ["api/admin/settings/test/ldap"] = "no MCP admin-settings tools",
        ["api/admin/settings/test/llm"] = "no MCP admin-settings tools",
        ["api/admin/settings/test/smtp"] = "no MCP admin-settings tools",
        ["api/ai/chat"] = InteractiveAiSse,
        ["api/ai/chat/activity/*"] = InteractiveAiSse,
        ["api/ai/chat/applied"] = InteractiveAiSse,
        ["api/ai/generate-script"] = InteractiveAiSse,
        ["api/ai/generate-workflow"] = InteractiveAiSse,
        ["api/ai/knowledge/ask"] = InteractiveAiSse,
        ["api/ai/knowledge/capabilities"] = InteractiveAiSse,
        ["api/alerting/rules/*/disable"] = "MCP alerting tools toggle via update; dedicated enable/disable endpoints unused",
        ["api/alerting/rules/*/enable"] = "MCP alerting tools toggle via update; dedicated enable/disable endpoints unused",
        ["api/alerting/system/preview"] = "system-policy preview is a UI builder affordance",
        ["api/audit/export"] = "MCP has audit read tools but no CSV export",
        ["api/auth/login"] = "DELIBERATE: the MCP server reuses the CLI's DPAPI session (np auth login) — it never logs in itself",
        ["api/auth/logout"] = "DELIBERATE: session lifecycle belongs to np auth",
        ["api/auth/methods"] = "DELIBERATE: session lifecycle belongs to np auth",
        ["api/auth/oidc"] = BrowserAuthFlow,
        ["api/auth/oidc/callback"] = BrowserAuthFlow,
        ["api/auth/windows"] = BrowserAuthFlow,
        ["api/backup/export"] = "system backup/restore is an operator (UI/CLI) task; no MCP backup tools",
        ["api/backup/manifest"] = "system backup/restore is an operator (UI/CLI) task; no MCP backup tools",
        ["api/backup/preview"] = "system backup/restore is an operator (UI/CLI) task; no MCP backup tools",
        ["api/backup/restore"] = "system backup/restore is an operator (UI/CLI) task; no MCP backup tools",
        ["api/custom-activities"] = CustomActivityGap,
        ["api/custom-activities/*"] = CustomActivityGap,
        ["api/custom-activities/*/disable"] = CustomActivityGap,
        ["api/custom-activities/*/enable"] = CustomActivityGap,
        ["api/custom-activities/*/rollback/*"] = CustomActivityGap,
        ["api/custom-activities/*/versions"] = CustomActivityGap,
        ["api/custom-activities/export"] = CustomActivityGap,
        ["api/custom-activities/import"] = CustomActivityGap,
        ["api/dbadmin/tables/*/rows"] = "MCP reads the DB via the knowledge SQL tools; the row browser is a UI surface",
        ["api/diagnostics/support-events/export"] = "MCP has support-log read tools but no export/download",
        ["api/diagnostics/support-log/download"] = "MCP has support-log read tools but no export/download",
        ["api/maintenance-windows"] = "no MCP maintenance-window tools",
        ["api/maintenance-windows/*"] = "no MCP maintenance-window tools",
        ["api/maintenance-windows/affecting/*"] = "no MCP maintenance-window tools",
        ["api/observability/config"] = SpaBootstrap,
        ["api/observability/dashboards/*"] = SpaBootstrap,
        ["api/observability/query"] = "metrics UI surface; no MCP observability tools",
        ["api/observability/query_range"] = "metrics UI surface; no MCP observability tools",
        ["api/observability/summary"] = "metrics UI surface; no MCP observability tools",
        ["api/scim/v2/groups"] = ScimSurface,
        ["api/scim/v2/groups/*"] = ScimSurface,
        ["api/scim/v2/resourcetypes"] = ScimSurface,
        ["api/scim/v2/schemas"] = ScimSurface,
        ["api/scim/v2/schemas/*"] = ScimSurface,
        ["api/scim/v2/serviceproviderconfig"] = ScimSurface,
        ["api/scim/v2/users"] = ScimSurface,
        ["api/scim/v2/users/*"] = ScimSurface,
        ["api/secrets/reencrypt"] = "secret re-encryption is an operator (UI/CLI) task",
        ["api/shared-workflow-folders"] = "no MCP folder-RBAC tools",
        ["api/shared-workflow-folders/*"] = "no MCP folder-RBAC tools",
        ["api/shared-workflow-folders/*/move"] = "no MCP folder-RBAC tools",
        ["api/shared-workflow-folders/*/permissions"] = "no MCP folder-RBAC tools",
        ["api/shared-workflow-folders/*/permissions/*"] = "no MCP folder-RBAC tools",
        ["api/system/host-info"] = "UI About surface",
        ["api/users"] = "no MCP user-management tools — deliberate, user admin stays human",
        ["api/users/*"] = "no MCP user-management tools — deliberate, user admin stays human",
        ["api/users/*/reactivate"] = "no MCP user-management tools — deliberate, user admin stays human",
        ["api/webhooks/*/*"] = WebhookIngress,
    };

    [Theory]
    [MemberData(nameof(ClientProjects))]
    public void EveryApiEndpoint_IsReachableFromClient_OrIsAKnownGap(string client)
    {
        var endpoints = DiscoverEndpointRoutes();
        endpoints.Should().NotBeEmpty("the controller scan must find endpoints — an empty scan means the scanner broke, not that the API is empty");

        var clientRoutes = DiscoverClientUrls($"src/NodePilot.{client}");
        clientRoutes.Should().NotBeEmpty($"the {client} URL scan must find api/ literals");

        var known = client == "Cli" ? KnownCliGaps : KnownMcpGaps;
        var uncovered = endpoints
            .Where(e => !IsCovered(e, clientRoutes) && !known.ContainsKey(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();

        uncovered.Should().BeEmpty(
            $"CLAUDE.md: every API endpoint needs BOTH clients. These routes have no {client} " +
            "call site — either add the command/tool, or document the gap in the known-gaps " +
            "list with what it costs:\n" + string.Join("\n", uncovered));
    }

    /// <summary>Keeps the exemption lists honest — a closed gap must leave the list.</summary>
    [Theory]
    [MemberData(nameof(ClientProjects))]
    public void KnownGaps_AreAllStillReal(string client)
    {
        var endpoints = DiscoverEndpointRoutes();
        var clientRoutes = DiscoverClientUrls($"src/NodePilot.{client}");
        var known = client == "Cli" ? KnownCliGaps : KnownMcpGaps;

        var stale = known.Keys
            .Where(route => !endpoints.Contains(route) || IsCovered(route, clientRoutes))
            .ToList();

        stale.Should().BeEmpty(
            $"these {client} known-gap entries are stale (endpoint gone or now covered) — " +
            "remove them so the list stays an honest inventory:\n" + string.Join("\n", stale));
    }

    // ---- endpoint discovery -------------------------------------------------------------

    private static readonly Regex ClassRoutePattern = new(@"^\s*\[Route\(""([^""]+)""\)\]", RegexOptions.Compiled);
    private static readonly Regex ClassDeclPattern = new(@"\bclass\s+(\w+?)Controller\b", RegexOptions.Compiled);
    private static readonly Regex HttpVerbPattern = new(@"^\s*\[Http(Get|Post|Put|Delete|Patch|Head)(?:\(""([^""]+)""\))?\]", RegexOptions.Compiled);

    private static HashSet<string> DiscoverEndpointRoutes()
    {
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var controllersDir = Path.Combine(RepoRoot(), "src", "NodePilot.Api", "Controllers");

        foreach (var file in Directory.EnumerateFiles(controllersDir, "*.cs", SearchOption.AllDirectories))
        {
            string? pendingRoute = null;
            string? classRoute = null;

            foreach (var line in File.ReadLines(file))
            {
                var routeMatch = ClassRoutePattern.Match(line);
                if (routeMatch.Success) { pendingRoute = routeMatch.Groups[1].Value; continue; }

                var classMatch = ClassDeclPattern.Match(line);
                if (classMatch.Success)
                {
                    classRoute = pendingRoute?.Replace("[controller]", classMatch.Groups[1].Value);
                    pendingRoute = null;
                    continue;
                }

                var verbMatch = HttpVerbPattern.Match(line);
                if (!verbMatch.Success) continue;

                var template = verbMatch.Groups[2].Success ? verbMatch.Groups[2].Value : string.Empty;
                string full;
                if (template.StartsWith('/')) full = template;
                else if (classRoute is null) continue; // abstract base without route — actions surface via derived classes
                else full = template.Length == 0 ? classRoute : $"{classRoute}/{template}";

                routes.Add(NormalizeRoute(full));
            }
        }

        return routes;
    }

    // ---- client URL discovery -----------------------------------------------------------

    private static readonly Regex ApiUrlLiteralPattern = new(@"""(/?api/[^""]*)""", RegexOptions.Compiled);

    private static HashSet<string> DiscoverClientUrls(string relativeProjectDir)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(RepoRoot(), relativeProjectDir.Replace('/', Path.DirectorySeparatorChar));

        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;

            foreach (Match m in ApiUrlLiteralPattern.Matches(File.ReadAllText(file)))
                urls.Add(NormalizeRoute(m.Groups[1].Value));
        }

        return urls;
    }

    // ---- matching -----------------------------------------------------------------------

    /// <summary>
    /// A client URL covers an endpoint when segment counts match and every segment is
    /// compatible — equal, or a wildcard on either side (a client interpolation hole like
    /// <c>{(enable ? "enable" : "disable")}</c> legitimately reaches literal endpoint
    /// segments at runtime).
    /// </summary>
    private static bool IsCovered(string endpoint, HashSet<string> clientRoutes)
    {
        var e = endpoint.Split('/');
        foreach (var route in clientRoutes)
        {
            var c = route.Split('/');
            if (c.Length != e.Length) continue;

            var allCompatible = true;
            for (var i = 0; i < e.Length; i++)
            {
                if (e[i] != c[i] && e[i] != "*" && c[i] != "*") { allCompatible = false; break; }
            }

            if (allCompatible) return true;
        }

        return false;
    }

    // ---- shared normalization -----------------------------------------------------------

    /// <summary>
    /// Both sides collapse to the same shape: leading slash and query string stripped, every
    /// parameterized segment (route <c>{id:guid}</c>/<c>{*path}</c> or interpolation hole
    /// <c>{Uri.EscapeDataString(x)}</c>) becomes <c>*</c>, compared case-insensitively.
    /// </summary>
    private static string NormalizeRoute(string raw)
    {
        var path = raw.TrimStart('/');
        var q = path.IndexOf('?');
        if (q >= 0) path = path[..q];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Contains('{') ? "*" : s.ToLowerInvariant());
        return string.Join('/', segments);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && dir is not null; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NodePilot.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"Could not locate NodePilot.slnx walking up from {AppContext.BaseDirectory}");
    }
}

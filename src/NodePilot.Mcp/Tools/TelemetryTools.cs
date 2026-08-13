using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Read-only observability tools: dashboard summary, per-node coverage/health/stats, the audit
/// log (Admin) and the support diagnostics (Admin). Large payloads are projected/truncated.
/// </summary>
[McpServerToolType]
public sealed class TelemetryTools
{
    private readonly NodePilotApiClient _api;

    public TelemetryTools(NodePilotApiClient api) => _api = api;

    [McpServerTool(Name = "get_dashboard_stats", ReadOnly = true)]
    [Description("Get a slim system dashboard: workflow/machine/execution totals, last-24h outcome counts, running/pending counts, failing workflows, active edit locks, DB provider and cluster role.")]
    public async Task<object> GetDashboardStats(CancellationToken cancellationToken = default)
    {
        var root = await ApiErrorMapper.Guard(() => _api.GetDashboardAsync(cancellationToken));
        return new
        {
            workflowsTotal = Int(root, "workflowsTotal"),
            workflowsEnabled = Int(root, "workflowsEnabled"),
            machinesTotal = Int(root, "machinesTotal"),
            machinesReachable = Int(root, "machinesReachable"),
            executionsTotal = Int(root, "executionsTotal"),
            last24h = Element(root, "last24h"),
            pendingCount = Int(root, "pendingCount"),
            runningCount = Int(root, "runningCount"),
            longRunningCount = Int(root, "longRunningCount"),
            failingWorkflows = Element(root, "failingWorkflows"),
            editLocks = Element(root, "editLocks"),
            databaseProvider = Str(root, "databaseProvider"),
            clusterRole = Str(root, "clusterRole"),
        };
    }

    /// <summary>
    /// How many finished runs <c>get_operations_graph</c> hands an agent. Deliberately far below the
    /// server's own render budget: the SPA draws every returned run, an agent reads a few and pays for
    /// the rest in context. Truncation is reported in the payload rather than done silently.
    /// </summary>
    private const int RecentToolCap = 200;

    [McpServerTool(Name = "get_operations_graph", ReadOnly = true)]
    [Description("Live-ops Mission-Control snapshot (RBAC-scoped): workflow nodes (name, folder, enabled, live runningCount, lastStatus, per-node canRun/canEdit), call edges between workflows (startWorkflow/forEach, with refStatus Resolved|Dynamic|Unresolved|Ambiguous), currently-running executions, and recently finished executions within the selected window. The server caps 'recent' at the newest 4000 and this tool trims it further to the newest 200, reporting 'meta.recentToolCap' and 'meta.recentWithheldByTool' when it does — running executions are never trimmed. When the SERVER cap bit, when it is, 'meta.recentTruncated' is true and 'density' carries the per-workflow run counts for the whole window in 'meta.densityBucketSeconds' buckets (bucketIndex 0 starts at meta.recentSinceUtc), each with total/failed/cancelled — so 'how much ran in the last four hours' is answerable even though not every run is listed individually. 'meta.oldestReturnedCompletedAt' marks where the individual list stops and 'meta.densityCapped' says the counts are a floor. Answers 'what is running right now, what just finished, and how do workflows call each other?'.")]
    public async Task<object> GetOperationsGraph(
        [Description("Look-back window for finished runs, in minutes: 30 (default) or 60. Other values clamp to 30. Running executions are always returned in full.")] int windowMinutes = 30,
        CancellationToken cancellationToken = default)
    {
        var root = await ApiErrorMapper.Guard(() => _api.GetOperationsGraphAsync(cancellationToken, windowMinutes));
        var graph = JsonNode.Parse(root.GetRawText())!.AsObject();

        // The console's budget is not an agent's. A 4 h window on a busy board fills 'recent' with the
        // server cap's worth of rows -- roughly 900 KB of GUIDs and ISO timestamps -- which is a sensible
        // payload for a timeline that draws every one of them and a waste of an agent's context, which
        // reads a handful. Trimmed from the OLD end: the server orders newest-first, and "what just
        // finished" is the question this list answers. 'running' is never trimmed -- it is the answer to
        // "what is going on right now" and it is small.
        if (graph["recent"] is JsonArray recent && recent.Count > RecentToolCap)
        {
            var withheld = recent.Count - RecentToolCap;
            while (recent.Count > RecentToolCap) recent.RemoveAt(recent.Count - 1);
            if (graph["meta"] is JsonObject meta)
            {
                meta["recentToolCap"] = RecentToolCap;
                meta["recentWithheldByTool"] = withheld;
            }
        }

        return graph;
    }

    [McpServerTool(Name = "get_workflow_coverage", ReadOnly = true)]
    [Description("Per-node coverage for a workflow over the last windowDays: how often each node executed/failed/was skipped, and when it last ran. Answers 'what logic actually runs in production?'.")]
    public async Task<object> GetWorkflowCoverage(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("Look-back window in days (1-365, default server value ~30).")] int? windowDays = null,
        CancellationToken cancellationToken = default)
    {
        var id = await ResolveIdAsync(idOrName, cancellationToken);
        return await ApiErrorMapper.Guard(() => _api.GetCoverageAsync(id, windowDays, cancellationToken));
    }

    [McpServerTool(Name = "get_workflow_step_health", ReadOnly = true)]
    [Description("The last N execution outcomes per step (sparkline data) for a workflow. Optionally restrict to specific step ids (comma-separated).")]
    public async Task<object> GetWorkflowStepHealth(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("Optional comma-separated step ids to restrict to.")] string? stepIds = null,
        [Description("How many recent outcomes per step (1-20, default 8).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var id = await ResolveIdAsync(idOrName, cancellationToken);
        var health = await ApiErrorMapper.Guard(() => _api.GetStepHealthAsync(id, stepIds, limit, cancellationToken));
        return new { workflowId = id, stepHealth = health };
    }

    [McpServerTool(Name = "get_workflow_step_stats", ReadOnly = true)]
    [Description("Per-step aggregate stats over the last windowDays: total/failed runs, failure rate, avg/p95/last duration. Use it to find the slow or flaky steps.")]
    public async Task<object> GetWorkflowStepStats(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("Look-back window in days (1-365, default server value ~30).")] int? windowDays = null,
        CancellationToken cancellationToken = default)
    {
        var id = await ResolveIdAsync(idOrName, cancellationToken);
        var stats = await ApiErrorMapper.Guard(() => _api.GetStepStatsAsync(id, windowDays, cancellationToken));
        return new { workflowId = id, stepStats = stats };
    }

    [McpServerTool(Name = "query_audit_log", ReadOnly = true)]
    [Description("Query the audit log (Admin only), newest first, cursor-paginated. Filter by action code and/or resourceType and/or since/until (ISO timestamps). Pass the returned nextCursor's timestamp+id back as afterTimestamp+afterId for the next page.")]
    public async Task<object> QueryAuditLog(
        [Description("Optional audit action code filter, e.g. WORKFLOW_UPDATED.")] string? action = null,
        [Description("Optional resource type filter, e.g. Workflow.")] string? resourceType = null,
        [Description("Optional ISO start timestamp (inclusive).")] string? since = null,
        [Description("Optional ISO end timestamp (inclusive).")] string? until = null,
        [Description("Cursor: timestamp from a previous response's nextCursor.")] string? afterTimestamp = null,
        [Description("Cursor: id from a previous response's nextCursor.")] string? afterId = null,
        [Description("Page size (1-500, default 50).")] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var page = await ApiErrorMapper.Guard(() =>
            _api.AuditAsync(action, resourceType, afterTimestamp, afterId, since, until, Math.Clamp(take, 1, 500), cancellationToken));
        var items = page.Items.Select(e => new
        {
            e.Id, e.Timestamp, e.Username, e.Action, e.ResourceType, e.ResourceId, e.IpAddress,
            details = PayloadShaping.Truncate(e.Details),
        });
        return new { items, nextCursor = page.NextCursor };
    }

    [McpServerTool(Name = "get_support_diagnostics", ReadOnly = true)]
    [Description("Get support diagnostics (Admin only). kind='log' tails the support log file; kind='events' returns recent structured support events. limit caps lines/events.")]
    public async Task<object> GetSupportDiagnostics(
        [Description("Either 'log' (tail the support log) or 'events' (structured support events).")] string kind,
        [Description("Max lines/events to return (default 100).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(limit, 1, 1000);
        var raw = kind.ToLowerInvariant() switch
        {
            "log" => await ApiErrorMapper.Guard(() => _api.GetSupportLogAsync(clamped, cancellationToken)),
            "events" => await ApiErrorMapper.Guard(() => _api.GetSupportEventsAsync(clamped, cancellationToken)),
            _ => throw new McpException("kind must be 'log' or 'events'."),
        };
        // Truncate every string leaf (log lines / event messages / propertiesJson can be huge).
        return PayloadShaping.TruncateStrings(raw) ?? (object)new { };
    }

    private async Task<Guid> ResolveIdAsync(string idOrName, CancellationToken ct)
    {
        if (Guid.TryParse(idOrName, out var id)) return id;
        var wf = await ApiErrorMapper.Guard(() => _api.GetWorkflowByNameAsync(idOrName, ct));
        return wf.Id;
    }

    private static int? Int(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null;

    private static string? Str(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static JsonElement? Element(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var e) ? e.Clone() : null;
}

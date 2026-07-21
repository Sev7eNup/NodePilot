using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Read-only workflow tools. Summaries deliberately omit the (potentially multi-MiB)
/// DefinitionJson — fetch that explicitly via get_workflow_definition (added in a later phase).
/// </summary>
[McpServerToolType]
public sealed class WorkflowReadTools
{
    private readonly NodePilotApiClient _api;

    public WorkflowReadTools(NodePilotApiClient api) => _api = api;

    [McpServerTool(Name = "list_workflows", ReadOnly = true)]
    [Description("List workflows as slim summaries (no definition JSON). Optionally filter by a name substring and/or only enabled workflows. Returns at most 'limit' rows (default 50).")]
    public async Task<object> ListWorkflows(
        [Description("Case-insensitive substring to match against the workflow name. Omit to list all.")] string? nameContains = null,
        [Description("When true, only enabled workflows are returned.")] bool enabledOnly = false,
        [Description("Maximum number of rows to return (1-200, default 50).")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var all = await ApiErrorMapper.Guard(() => _api.ListWorkflowsAsync(cancellationToken));
        IEnumerable<WorkflowResponse> q = all;
        if (!string.IsNullOrWhiteSpace(nameContains))
            q = q.Where(w => w.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        if (enabledOnly)
            q = q.Where(w => w.IsEnabled);

        var clamped = Math.Clamp(limit, 1, 200);
        var rows = q.Take(clamped).Select(Summarize).ToList();
        return new { count = rows.Count, totalAvailable = all.Count, workflows = rows };
    }

    [McpServerTool(Name = "get_workflow", ReadOnly = true)]
    [Description("Get a single workflow summary by id (GUID) or exact name. Does NOT include the definition JSON — use get_workflow_definition for that.")]
    public async Task<object> GetWorkflow(
        [Description("The workflow GUID, or its exact (case-sensitive) name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        return Detail(wf);
    }

    [McpServerTool(Name = "get_workflow_definition", ReadOnly = true)]
    [Description("Get a workflow's full definition (nodes + edges). This can be large (token-heavy). Pass nodeIdsOnly=true to get just a compact node list (id/label/activityType) + edge count instead of the whole graph.")]
    public async Task<object> GetWorkflowDefinition(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("When true, return only a compact node list + edge count, not the full definition.")] bool nodeIdsOnly = false,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(wf.DefinitionJson) ? "{}" : wf.DefinitionJson);
        var root = doc.RootElement;

        if (nodeIdsOnly)
        {
            var nodes = root.TryGetProperty("nodes", out var n) && n.ValueKind == JsonValueKind.Array
                ? n.EnumerateArray().Select(SummarizeNode).ToList()
                : [];
            var edgeCount = root.TryGetProperty("edges", out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : 0;
            return new { wf.Id, wf.Name, wf.Version, nodeCount = nodes.Count, edgeCount, nodes };
        }

        // Mask inline secrets (webhook secret / apiKey / password / …) — the API hands an
        // Admin/Operator service account the RAW definition; the agent must not see secrets.
        return new { wf.Id, wf.Name, wf.Version, definition = DefinitionRedactor.Redact(root) };
    }

    [McpServerTool(Name = "get_workflow_contract", ReadOnly = true)]
    [Description("Get a workflow's calling contract: declared inputs (from manualTrigger.parameters) and downstream outputs (from returnData keys + system outputs). Use this before calling a workflow as a sub-workflow or via execute_workflow.")]
    public async Task<object> GetWorkflowContract(
        [Description("The workflow GUID or exact (case-sensitive) name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var contract = Guid.TryParse(idOrName, out var id)
            ? await ApiErrorMapper.Guard(() => _api.GetContractAsync(id, cancellationToken))
            : await ApiErrorMapper.Guard(() => _api.GetContractByNameAsync(idOrName, cancellationToken));
        return contract;
    }

    [McpServerTool(Name = "list_workflow_versions", ReadOnly = true)]
    [Description("List a workflow's version history (metadata only — no definition JSON). Use get_workflow_version to fetch a specific version's definition.")]
    public async Task<object> ListWorkflowVersions(
        [Description("The workflow GUID or exact name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var versions = await ApiErrorMapper.Guard(() => _api.ListVersionsAsync(wf.Id, cancellationToken));
        return new { wf.Id, wf.Name, versions };
    }

    [McpServerTool(Name = "get_workflow_version", ReadOnly = true)]
    [Description("Get one historical version of a workflow, including its definition (parsed). Useful to compare against the current definition before a rollback.")]
    public async Task<object> GetWorkflowVersion(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("The version number to fetch.")] int version,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var v = await ApiErrorMapper.Guard(() => _api.GetVersionAsync(wf.Id, version, cancellationToken));
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(v.DefinitionJson) ? "{}" : v.DefinitionJson);
        return new
        {
            wf.Id, v.Version, v.Name, v.Description, v.CreatedAt, v.CreatedBy, v.ChangeNote, v.IsCurrent,
            definition = DefinitionRedactor.Redact(doc.RootElement),
        };
    }

    [McpServerTool(Name = "export_workflow", ReadOnly = true)]
    [Description("Export a single workflow as a portable envelope (nodepilot-workflow-export/v1, secrets redacted). Use the result with import_workflow to copy it elsewhere.")]
    public async Task<object> ExportWorkflow(
        [Description("The workflow GUID or exact name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        return await ApiErrorMapper.Guard(() => _api.ExportOneAsync(wf.Id, cancellationToken));
    }

    private async Task<WorkflowResponse> ResolveAsync(string idOrName, CancellationToken ct)
        => Guid.TryParse(idOrName, out var id)
            ? await ApiErrorMapper.Guard(() => _api.GetWorkflowAsync(id, ct))
            : await ApiErrorMapper.Guard(() => _api.GetWorkflowByNameAsync(idOrName, ct));

    private static object SummarizeNode(JsonElement node)
    {
        var data = node.TryGetProperty("data", out var d) ? d : default;
        return new
        {
            id = node.TryGetProperty("id", out var i) ? i.GetString() : null,
            label = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("label", out var l) ? l.GetString() : null,
            activityType = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("activityType", out var a) ? a.GetString() : null,
        };
    }

    private static object Summarize(WorkflowResponse w) => new
    {
        id = w.Id,
        name = w.Name,
        enabled = w.IsEnabled,
        version = w.Version,
        activityCount = w.ActivityCount,
        triggerTypes = w.TriggerTypes,
        checkedOutBy = w.CheckedOutByUserName,
        lastStatus = w.LastExecution?.Status,
    };

    private static object Detail(WorkflowResponse w) => new
    {
        id = w.Id,
        name = w.Name,
        description = w.Description,
        enabled = w.IsEnabled,
        version = w.Version,
        activityCount = w.ActivityCount,
        triggerTypes = w.TriggerTypes,
        checkedOutBy = w.CheckedOutByUserName,
        checkedOutAt = w.CheckedOutAt,
        successCount = w.SuccessCount,
        totalCount = w.TotalCount,
        avgDurationMs = w.AvgDurationMs,
        lastExecution = w.LastExecution,
        createdAt = w.CreatedAt,
        updatedAt = w.UpdatedAt,
    };
}

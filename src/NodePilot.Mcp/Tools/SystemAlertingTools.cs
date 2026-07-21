using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// System-alert policy management. "System-alert policies" (ADR 0008) are the built-in
/// infra/health alerts (backlog too high, a machine unreachable, a credential about to expire, …)
/// — a fixed catalog of sources an admin enables and tunes, distinct from the free-form custom
/// rules handled by <see cref="AlertingTools"/>. This class covers the server-owned source catalog
/// plus policy read + create/update/enable/disable/test-fire. Delete lives in the gated
/// <see cref="DestructiveTools"/>. Read paths never surface route secrets — the API redacts them
/// and <see cref="Summarize"/> only projects channel/target/order.
/// </summary>
[McpServerToolType]
public sealed class SystemAlertingTools
{
    private readonly NodePilotApiClient _api;

    public SystemAlertingTools(NodePilotApiClient api) => _api = api;

    [McpServerTool(Name = "get_system_alert_catalog", ReadOnly = true)]
    [Description("Get the server-owned system-alert source catalog (ADR 0008): each source's category, scope capability, default severity, condition fields (with operators/units/enum values), tunable parameters, presets, and a live availability flag. Author policies against these source ids and fields.")]
    public async Task<object> GetSystemAlertCatalog(CancellationToken cancellationToken = default)
    {
        var catalog = await ApiErrorMapper.Guard(() => _api.GetSystemAlertCatalogAsync(cancellationToken));
        return new { count = catalog.Sources.Count, sources = catalog.Sources };
    }

    [McpServerTool(Name = "list_system_alert_policies", ReadOnly = true)]
    [Description("List system-alert policies with their source, condition, scope, sustain window, throttle and route channels. No route secrets are surfaced.")]
    public async Task<object> ListSystemAlertPolicies(CancellationToken cancellationToken = default)
    {
        var policies = await ApiErrorMapper.Guard(() => _api.ListSystemAlertPoliciesAsync(cancellationToken));
        return new { count = policies.Count, policies = policies.Select(Summarize) };
    }

    [McpServerTool(Name = "get_system_alert_policy", ReadOnly = true)]
    [Description("Get one system-alert policy by id.")]
    public async Task<object> GetSystemAlertPolicy(
        [Description("The policy GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var p = await ApiErrorMapper.Guard(() => _api.GetSystemAlertPolicyAsync(ExecutionTools.ParseGuid(id, "policy id"), cancellationToken));
        return Summarize(p);
    }

    [McpServerTool(Name = "create_system_alert_policy")]
    [Description("Create a system-alert policy (Admin only). Binds a catalog source, an optional condition + parameters, a sustain window, scope, severity and routes. Use get_system_alert_catalog first to learn valid source ids, fields and parameters.")]
    public async Task<object> CreateSystemAlertPolicy(
        [Description("Policy name.")] string name,
        [Description("Catalog source id (from get_system_alert_catalog).")] string sourceId,
        [Description("Optional description.")] string? description = null,
        [Description("Optional preset id from the source's presets.")] string? presetId = null,
        [Description("Optional source parameters as a JSON object, e.g. '{\"thresholdMb\":500}'. Keys/types must match the source descriptor.")] string? sourceParametersJson = null,
        [Description("Optional condition expression JSON (AST over the source's fields). Omit to alert on every observed instance.")] string? conditionJson = null,
        [Description("Seconds the condition must hold before firing (default 0 = fire immediately).")] int sustainForSeconds = 0,
        [Description("Optional severity override: Info | Warning | Critical (omit to use the source default).")] string? severityOverride = null,
        [Description("Global | Folders | Workflows (default Global). Some sources are Global-only.")] string scopeKind = "Global",
        [Description("Comma-separated email recipients (one Email route each).")] string? emails = null,
        [Description("Comma-separated webhook URLs (one GenericWebhook route each).")] string? webhooks = null,
        [Description("Comma-separated folder GUIDs (when scopeKind=Folders).")] string? folderIds = null,
        [Description("Comma-separated workflow GUIDs (when scopeKind=Workflows).")] string? workflowIds = null,
        [Description("Create enabled (default true). An enabled policy requires at least one route.")] bool isEnabled = true,
        [Description("Cooldown minutes between alerts per dedup key (default 0).")] int cooldownMinutes = 0,
        [Description("Min occurrences before firing — flap suppression (default 1).")] int minOccurrences = 1,
        [Description("Occurrence window minutes for min-occurrences (default 0).")] int occurrenceWindowMinutes = 0,
        CancellationToken cancellationToken = default)
    {
        var req = new SaveSystemAlertPolicyRequest(
            name, description, isEnabled, sourceId,
            string.IsNullOrWhiteSpace(presetId) ? null : presetId,
            ParseParameters(sourceParametersJson),
            string.IsNullOrWhiteSpace(conditionJson) ? null : conditionJson,
            sustainForSeconds,
            string.IsNullOrWhiteSpace(severityOverride) ? null : severityOverride,
            scopeKind,
            BuildTargets(scopeKind, folderIds, workflowIds),
            BuildRoutes(emails, webhooks),
            cooldownMinutes, minOccurrences, occurrenceWindowMinutes);
        var p = await ApiErrorMapper.Guard(() => _api.CreateSystemAlertPolicyAsync(req, cancellationToken));
        return new { created = true, policyId = p.Id, name = p.Name };
    }

    [McpServerTool(Name = "update_system_alert_policy")]
    [Description("Update a system-alert policy (Admin only). Read-modify-write: omitted fields keep their current value. Passing any emails/webhooks replaces ALL routes; passing any folderIds/workflowIds replaces ALL targets.")]
    public async Task<object> UpdateSystemAlertPolicy(
        [Description("The policy GUID.")] string id,
        [Description("New name (omit to keep).")] string? name = null,
        [Description("New description (omit to keep).")] string? description = null,
        [Description("New source id (omit to keep).")] string? sourceId = null,
        [Description("New preset id (omit to keep).")] string? presetId = null,
        [Description("New source parameters as a JSON object (omit to keep).")] string? sourceParametersJson = null,
        [Description("New condition expression JSON (omit to keep).")] string? conditionJson = null,
        [Description("New sustain seconds (omit to keep).")] int? sustainForSeconds = null,
        [Description("New severity override Info|Warning|Critical (omit to keep).")] string? severityOverride = null,
        [Description("New scope Global|Folders|Workflows (omit to keep).")] string? scopeKind = null,
        [Description("Replacement email routes, comma-separated (omit to keep current routes).")] string? emails = null,
        [Description("Replacement webhook routes, comma-separated (omit to keep current routes).")] string? webhooks = null,
        [Description("Folder GUIDs when scope=Folders (omit to keep current targets).")] string? folderIds = null,
        [Description("Workflow GUIDs when scope=Workflows (omit to keep current targets).")] string? workflowIds = null,
        [Description("Enable or disable (omit to keep).")] bool? isEnabled = null,
        [Description("New cooldown minutes (omit to keep).")] int? cooldownMinutes = null,
        [Description("New min occurrences (omit to keep).")] int? minOccurrences = null,
        [Description("New occurrence window minutes (omit to keep).")] int? occurrenceWindowMinutes = null,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "policy id");
        var current = await ApiErrorMapper.Guard(() => _api.GetSystemAlertPolicyAsync(guid, cancellationToken));
        var scope = scopeKind ?? current.ScopeKind;

        var routes = (emails is not null || webhooks is not null)
            ? BuildRoutes(emails, webhooks)
            : current.Routes;
        var targets = (folderIds is not null || workflowIds is not null)
            ? BuildTargets(scope, folderIds, workflowIds)
            : current.Targets;

        var req = new SaveSystemAlertPolicyRequest(
            name ?? current.Name,
            description ?? current.Description,
            isEnabled ?? current.IsEnabled,
            sourceId ?? current.SourceId,
            presetId is null ? current.PresetId : (string.IsNullOrWhiteSpace(presetId) ? null : presetId),
            sourceParametersJson is null ? current.SourceParameters : ParseParameters(sourceParametersJson),
            conditionJson is null ? current.ConditionJson : (string.IsNullOrWhiteSpace(conditionJson) ? null : conditionJson),
            sustainForSeconds ?? current.SustainForSeconds,
            severityOverride is null ? current.SeverityOverride : (string.IsNullOrWhiteSpace(severityOverride) ? null : severityOverride),
            scope,
            targets,
            routes,
            cooldownMinutes ?? current.CooldownMinutes,
            minOccurrences ?? current.MinOccurrences,
            occurrenceWindowMinutes ?? current.OccurrenceWindowMinutes);

        await ApiErrorMapper.Guard(() => _api.UpdateSystemAlertPolicyAsync(guid, req, cancellationToken));
        return new { updated = true, policyId = id };
    }

    [McpServerTool(Name = "enable_system_alert_policy")]
    [Description("Enable a system-alert policy (Admin only). Requires the policy to have at least one route.")]
    public async Task<object> EnableSystemAlertPolicy(
        [Description("The policy GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "policy id");
        await ApiErrorMapper.Guard(() => _api.EnableSystemAlertPolicyAsync(guid, cancellationToken));
        return new { enabled = true, policyId = guid };
    }

    [McpServerTool(Name = "disable_system_alert_policy")]
    [Description("Disable a system-alert policy (Admin only). Kill-switch: the policy stops firing.")]
    public async Task<object> DisableSystemAlertPolicy(
        [Description("The policy GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "policy id");
        await ApiErrorMapper.Guard(() => _api.DisableSystemAlertPolicyAsync(guid, cancellationToken));
        return new { disabled = true, policyId = guid };
    }

    [McpServerTool(Name = "test_fire_system_alert_policy")]
    [Description("Send a synthetic test notification through every route of a system-alert policy (Admin only). Returns per-route success.")]
    public async Task<object> TestFireSystemAlertPolicy(
        [Description("The policy GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var result = await ApiErrorMapper.Guard(() => _api.TestFireSystemAlertPolicyAsync(ExecutionTools.ParseGuid(id, "policy id"), cancellationToken));
        return new { allSucceeded = result.AllSucceeded, results = result.Results };
    }

    private static object Summarize(SystemAlertPolicyResponse p) => new
    {
        id = p.Id,
        name = p.Name,
        description = p.Description,
        isEnabled = p.IsEnabled,
        sourceId = p.SourceId,
        presetId = p.PresetId,
        sourceParameters = p.SourceParameters,
        conditionJson = p.ConditionJson,
        sustainForSeconds = p.SustainForSeconds,
        severityOverride = p.SeverityOverride,
        scopeKind = p.ScopeKind,
        cooldownMinutes = p.CooldownMinutes,
        minOccurrences = p.MinOccurrences,
        occurrenceWindowMinutes = p.OccurrenceWindowMinutes,
        // Route secrets are never surfaced — only channel/target/order.
        routes = p.Routes.Select(x => new { x.Channel, x.Target, x.Order, x.ConditionExpressionJson }),
        targets = p.Targets,
        activatedAt = p.ActivatedAt,
        updatedAt = p.UpdatedAt,
        updatedBy = p.UpdatedBy,
    };

    private static Dictionary<string, object?>? ParseParameters(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, object?>>(json, NodePilotApiClient.JsonOptions);

    private static List<string> Split(string? csv)
        => string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<NotificationRouteDto> BuildRoutes(string? emails, string? webhooks)
    {
        var list = new List<NotificationRouteDto>();
        var order = 0;
        foreach (var e in Split(emails)) list.Add(new NotificationRouteDto(null, "Email", e, null, order++));
        foreach (var w in Split(webhooks)) list.Add(new NotificationRouteDto(null, "GenericWebhook", w, null, order++));
        return list;
    }

    private static List<NotificationRuleTargetDto> BuildTargets(string scope, string? folderIds, string? workflowIds)
    {
        var list = new List<NotificationRuleTargetDto>();
        if (scope.Equals("Folders", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var fid in Split(folderIds))
                if (Guid.TryParse(fid, out var g)) list.Add(new NotificationRuleTargetDto("Folder", g));
        }
        else if (scope.Equals("Workflows", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var wid in Split(workflowIds))
                if (Guid.TryParse(wid, out var g)) list.Add(new NotificationRuleTargetDto("Workflow", g));
        }
        return list;
    }
}

using System.ComponentModel;
using ModelContextProtocol.Server;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Alerting rule management (read + create/update + test-fire). Delete lives in the gated
/// <see cref="DestructiveTools"/>. Read paths never surface route secrets — the API redacts them.
/// </summary>
[McpServerToolType]
public sealed class AlertingTools
{
    private readonly NodePilotApiClient _api;

    public AlertingTools(NodePilotApiClient api) => _api = api;

    [McpServerTool(Name = "list_alerting_rules", ReadOnly = true)]
    [Description("List alerting rules with their event types, scope, throttle and route channels.")]
    public async Task<object> ListAlertingRules(CancellationToken cancellationToken = default)
    {
        var rules = await ApiErrorMapper.Guard(() => _api.ListAlertingRulesAsync(cancellationToken));
        return new { count = rules.Count, rules = rules.Select(Summarize) };
    }

    [McpServerTool(Name = "get_alerting_rule", ReadOnly = true)]
    [Description("Get one alerting rule by id.")]
    public async Task<object> GetAlertingRule(
        [Description("The rule GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var r = await ApiErrorMapper.Guard(() => _api.GetAlertingRuleAsync(ExecutionTools.ParseGuid(id, "rule id"), cancellationToken));
        return Summarize(r);
    }

    [McpServerTool(Name = "create_alerting_rule")]
    [Description("Create an alerting rule (Admin only). Notifies via the given routes when a matching event occurs.")]
    public async Task<object> CreateAlertingRule(
        [Description("Rule name.")] string name,
        [Description("Comma-separated event types. Execution/workflow-scoped: ExecutionFailed,ExecutionSucceeded,ExecutionCancelled,ExecutionRunningLong,ExecutionQueuedLong,ScheduleMissed,WorkflowNoRecentSuccess,CredentialFailure. Global signals: ServiceStale,MachineUnreachable,BacklogHigh,PendingHigh,CancelRateHigh,CredentialExpiring.")] string eventTypes,
        [Description("Optional description.")] string? description = null,
        [Description("Optional filter expression JSON (condition AST, operands of source 'event').")] string? filterExpressionJson = null,
        [Description("Global | Folders | Workflows (default Global).")] string scopeKind = "Global",
        [Description("Comma-separated email recipients (one Email route each).")] string? emails = null,
        [Description("Comma-separated webhook URLs (one GenericWebhook route each).")] string? webhooks = null,
        [Description("Comma-separated folder GUIDs (when scopeKind=Folders).")] string? folderIds = null,
        [Description("Comma-separated workflow GUIDs (when scopeKind=Workflows).")] string? workflowIds = null,
        [Description("Cooldown minutes between alerts per dedup key (default 0).")] int cooldownMinutes = 0,
        [Description("Min occurrences before firing — flap suppression (default 1).")] int minOccurrences = 1,
        [Description("Occurrence window minutes for min-occurrences (default 0).")] int occurrenceWindowMinutes = 0,
        [Description("Optional dedup grouping template, e.g. '{{eventType}}:{{workflowId}}'.")] string? dedupKeyTemplate = null,
        CancellationToken cancellationToken = default)
    {
        var req = new SaveNotificationRuleRequest(
            name, description, true, Split(eventTypes), filterExpressionJson, scopeKind,
            cooldownMinutes, minOccurrences, occurrenceWindowMinutes,
            BuildRoutes(emails, webhooks), BuildTargets(scopeKind, folderIds, workflowIds), dedupKeyTemplate);
        var rule = await ApiErrorMapper.Guard(() => _api.CreateAlertingRuleAsync(req, cancellationToken));
        return new { created = true, ruleId = rule.Id, name = rule.Name };
    }

    [McpServerTool(Name = "update_alerting_rule")]
    [Description("Update an alerting rule (Admin only). Read-modify-write: omitted fields keep their current value. Passing any emails/webhooks replaces ALL routes.")]
    public async Task<object> UpdateAlertingRule(
        [Description("The rule GUID.")] string id,
        [Description("New name (omit to keep).")] string? name = null,
        [Description("New description (omit to keep).")] string? description = null,
        [Description("New comma-separated event types (omit to keep).")] string? eventTypes = null,
        [Description("New filter JSON (omit to keep).")] string? filterExpressionJson = null,
        [Description("New scope Global|Folders|Workflows (omit to keep).")] string? scopeKind = null,
        [Description("Replacement email routes, comma-separated (omit to keep current routes).")] string? emails = null,
        [Description("Replacement webhook routes, comma-separated (omit to keep current routes).")] string? webhooks = null,
        [Description("Folder GUIDs when scope=Folders (omit to keep).")] string? folderIds = null,
        [Description("Workflow GUIDs when scope=Workflows (omit to keep).")] string? workflowIds = null,
        [Description("New cooldown minutes (omit to keep).")] int? cooldownMinutes = null,
        [Description("New min occurrences (omit to keep).")] int? minOccurrences = null,
        [Description("New occurrence window minutes (omit to keep).")] int? occurrenceWindowMinutes = null,
        [Description("Enable or disable (omit to keep).")] bool? isEnabled = null,
        [Description("New dedup grouping template (omit to keep).")] string? dedupKeyTemplate = null,
        CancellationToken cancellationToken = default)
    {
        var guid = ExecutionTools.ParseGuid(id, "rule id");
        var current = await ApiErrorMapper.Guard(() => _api.GetAlertingRuleAsync(guid, cancellationToken));
        var scope = scopeKind ?? current.ScopeKind;

        var routes = (emails is not null || webhooks is not null)
            ? BuildRoutes(emails, webhooks)
            : current.Routes;
        var targets = (folderIds is not null || workflowIds is not null)
            ? BuildTargets(scope, folderIds, workflowIds)
            : current.Targets;

        var req = new SaveNotificationRuleRequest(
            name ?? current.Name,
            description ?? current.Description,
            isEnabled ?? current.IsEnabled,
            eventTypes is null ? current.EventTypes : Split(eventTypes),
            filterExpressionJson ?? current.FilterExpressionJson,
            scope,
            cooldownMinutes ?? current.CooldownMinutes,
            minOccurrences ?? current.MinOccurrences,
            occurrenceWindowMinutes ?? current.OccurrenceWindowMinutes,
            routes, targets, dedupKeyTemplate ?? current.DedupKeyTemplate);

        await ApiErrorMapper.Guard(() => _api.UpdateAlertingRuleAsync(guid, req, cancellationToken));
        return new { updated = true, ruleId = id };
    }

    [McpServerTool(Name = "list_alerting_deliveries", ReadOnly = true)]
    [Description("Read the alerting delivery ledger (recent attempts, newest first). Optional filter by ruleId and/or status (Pending|Sent|Failed). No secrets are surfaced.")]
    public async Task<object> ListAlertingDeliveries(
        [Description("Optional rule GUID to filter to.")] string? ruleId = null,
        [Description("Optional status filter: Pending | Sent | Failed.")] string? status = null,
        [Description("Max rows (default 100, max 500).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        Guid? rid = string.IsNullOrWhiteSpace(ruleId) ? null : ExecutionTools.ParseGuid(ruleId, "rule id");
        var rows = await ApiErrorMapper.Guard(() => _api.ListAlertingDeliveriesAsync(rid, status, limit, cancellationToken));
        return new { count = rows.Count, deliveries = rows };
    }

    [McpServerTool(Name = "test_fire_alerting_rule")]
    [Description("Send a synthetic test notification through every route of a rule (Admin only). Returns per-route success.")]
    public async Task<object> TestFireAlertingRule(
        [Description("The rule GUID.")] string id,
        CancellationToken cancellationToken = default)
    {
        var result = await ApiErrorMapper.Guard(() => _api.TestFireAlertingRuleAsync(ExecutionTools.ParseGuid(id, "rule id"), cancellationToken));
        return new { allSucceeded = result.AllSucceeded, results = result.Results };
    }

    private static object Summarize(NotificationRuleResponse r) => new
    {
        id = r.Id,
        name = r.Name,
        description = r.Description,
        isEnabled = r.IsEnabled,
        eventTypes = r.EventTypes,
        filterExpressionJson = r.FilterExpressionJson,
        scopeKind = r.ScopeKind,
        cooldownMinutes = r.CooldownMinutes,
        minOccurrences = r.MinOccurrences,
        occurrenceWindowMinutes = r.OccurrenceWindowMinutes,
        dedupKeyTemplate = r.DedupKeyTemplate,
        // Route secrets are never surfaced — only channel/target/order.
        routes = r.Routes.Select(x => new { x.Channel, x.Target, x.Order, x.ConditionExpressionJson }),
        targets = r.Targets,
        updatedAt = r.UpdatedAt,
        updatedBy = r.UpdatedBy,
    };

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

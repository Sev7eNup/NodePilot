using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodePilot.Core.Activities;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Config;
using NodePilot.Mcp.Mapping;
using NodePilot.Mcp.Resources;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Orientation tools an agent should call first: who am I, and what is gated.
/// </summary>
[McpServerToolType]
public sealed class DiscoveryTools
{
    private readonly NodePilotApiClient _api;
    private readonly McpServerConfig _config;

    public DiscoveryTools(NodePilotApiClient api, McpServerConfig config)
    {
        _api = api;
        _config = config;
    }

    [McpServerTool(Name = "whoami", ReadOnly = true)]
    [Description("Return the authenticated NodePilot user, their role (Admin/Operator/Viewer) and the server URL. Call this first to learn what you are allowed to do.")]
    public async Task<object> WhoAmI(CancellationToken cancellationToken = default)
    {
        var me = await ApiErrorMapper.Guard(() => _api.MeAsync(cancellationToken));
        return new
        {
            authenticated = true,
            username = me.Username,
            role = me.Role,
            userId = me.Id,
            server = _api.Session?.Server,
        };
    }

    [McpServerTool(Name = "get_safety_status", ReadOnly = true)]
    [Description("Report whether destructive/admin tools are enabled (NODEPILOT_MCP_ALLOW_DESTRUCTIVE) and which tool names are currently blocked. Read-only, no network call.")]
    public object GetSafetyStatus()
    {
        var allow = _config.AllowDestructive;
        return new
        {
            allowDestructive = allow,
            blockedTools = allow ? Array.Empty<string>() : DestructiveToolNames,
            hint = allow
                ? "Destructive tools are registered and callable."
                : "Set NODEPILOT_MCP_ALLOW_DESTRUCTIVE=true to register destructive tools (delete/force-unlock/cancel-all/step-test).",
        };
    }

    [McpServerTool(Name = "list_activity_types", ReadOnly = true)]
    [Description("List the available workflow activity/trigger types with their category, whether they are triggers or run remotely (WinRM), and their stable output parameters. Computed in-process from the backend catalog — no network call. Optionally filter by category: Trigger, Action, ControlFlow, Logic.")]
    public object ListActivityTypes(
        [Description("Optional category filter: Trigger | Action | ControlFlow | Logic. Omit for all.")] string? category = null)
    {
        IEnumerable<ActivityDescriptor> all = ActivityCatalog.All;
        if (!string.IsNullOrWhiteSpace(category) && Enum.TryParse<ActivityCategory>(category, ignoreCase: true, out var cat))
            all = all.Where(a => a.Category == cat);

        var rows = all.Select(a => new
        {
            type = a.Type,
            category = a.Category.ToString(),
            isTrigger = a.IsTrigger,
            isRemote = a.IsRemote,
            outputParameters = a.OutputParameters.Select(p => new { name = p.Name, type = p.Type }),
        });
        return new { activityTypes = rows };
    }

    [McpServerTool(Name = "get_activity_config_reference", ReadOnly = true)]
    [Description("Get the config-key reference for an activity type (key, type, required, description + example) so you can author a valid node 'config' object. The backend catalog only carries OUTPUT params; this curated reference fills the CONFIG-key gap. No network call.")]
    public object GetActivityConfigReference(
        [Description("The activity type, e.g. 'runScript', 'restApi', 'sql'.")] string activityType)
    {
        var json = EmbeddedResources.Read(EmbeddedResources.ActivityConfigReference);
        using var doc = JsonDocument.Parse(json);
        var activities = doc.RootElement.GetProperty("activities");
        if (activities.TryGetProperty(activityType, out var entry))
            return new { activityType, config = entry.Clone() };

        return new
        {
            activityType,
            found = false,
            note = "No curated config reference for this type yet. Use list_activity_types for outputs, and the styleguide resource.",
            documentedTypes = activities.EnumerateObject().Select(p => p.Name).ToArray(),
        };
    }

    [McpServerTool(Name = "validate_cron", ReadOnly = true)]
    [Description("Validate a Quartz cron expression and preview its next fire times. Useful when configuring a scheduleTrigger.")]
    public async Task<object> ValidateCron(
        [Description("Quartz cron expression, e.g. '0 0 2 * * ?' for daily at 02:00.")] string cron,
        [Description("How many upcoming fire times to return (1-20, default 5).")] int count = 5,
        CancellationToken cancellationToken = default)
    {
        var clamped = Math.Clamp(count, 1, 20);
        var res = await ApiErrorMapper.Guard(() => _api.CronNextFiresAsync(cron, clamped, cancellationToken));
        return new { valid = true, summary = res.Summary, nextFires = res.Fires };
    }

    // The destructive tools that ACTUALLY exist today and are gated out by default. Keep this
    // honest — only list tools that are really registered under the gate (grow it as
    // delete_*/force_unlock_workflow land in later phases), so the agent isn't told a tool is
    // "blocked" when it doesn't exist yet.
    private static readonly string[] DestructiveToolNames =
    [
        "cancel_all_executions",
        "test_step",
        "delete_workflow",
        "force_unlock_workflow",
        "delete_machine",
        "delete_credential",
        "delete_global_variable",
        "delete_global_variable_folder",
        "delete_alerting_rule",
        "delete_system_alert_policy",
    ];
}

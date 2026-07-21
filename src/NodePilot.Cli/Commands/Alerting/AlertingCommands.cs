using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Alerting;

[SupportedOSPlatform("windows")]
public sealed class AlertingListCommand : BaseCommand<GlobalSettings>
{
    public AlertingListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListAlertingRulesAsync(ct);
        writer.WriteData(rows, (console, list) => Renderers.AlertingRules(console, list));
        return ExitCodes.Success;
    }
}

public sealed class AlertingIdSettings : GlobalSettings
{
    [CommandArgument(0, "<RULE-ID>")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class AlertingGetCommand : BaseCommand<AlertingIdSettings>
{
    public AlertingGetCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, AlertingIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var r = (await api.ListAlertingRulesAsync(ct)).FirstOrDefault(x => x.Id == settings.Id);
        if (r is null) { writer.Error($"Alerting rule {settings.Id} nicht gefunden."); return ExitCodes.Error; }
        writer.WriteData(new[] { r }, (console, list) => Renderers.AlertingRules(console, list));
        return ExitCodes.Success;
    }
}

// Shared option surface for create/update. On update only the provided options override the
// current rule (the command fetches it first). Not sealed — AlertingUpdateSettings adds the id arg.
public class AlertingWriteSettings : GlobalSettings
{
    [CommandOption("--name <NAME>")] public string? Name { get; set; }
    [CommandOption("--description <TEXT>")] public string? Description { get; set; }
    [CommandOption("--disabled")]
    [Description("Create/keep the rule disabled (default enabled).")]
    public bool Disabled { get; set; }
    [CommandOption("--enabled")]
    [Description("Force-enable the rule (overrides --disabled on update).")]
    public bool Enabled { get; set; }

    [CommandOption("--event-types <LIST>")]
    [Description("Comma-separated event types. Execution/workflow-scoped: ExecutionFailed,ExecutionSucceeded,ExecutionCancelled,ExecutionRunningLong,ExecutionQueuedLong,ScheduleMissed,WorkflowNoRecentSuccess,CredentialFailure. Global signals: ServiceStale,MachineUnreachable,BacklogHigh,PendingHigh,CancelRateHigh,CredentialExpiring.")]
    public string? EventTypes { get; set; }

    [CommandOption("--filter-json <JSON>")]
    [Description("Optional filter expression (condition AST JSON, operands of source 'event').")]
    public string? FilterJson { get; set; }

    [CommandOption("--dedup-key-template <TEMPLATE>")]
    [Description("Optional dedup grouping template, e.g. '{{eventType}}:{{workflowId}}'.")]
    public string? DedupKeyTemplate { get; set; }

    [CommandOption("--scope <SCOPE>")]
    [Description("Global | Folders | Workflows. Default Global.")]
    public string? Scope { get; set; }

    [CommandOption("--folder <GUID>")]
    [Description("Folder target (repeatable). Use with --scope Folders.")]
    public Guid[] Folders { get; set; } = [];

    [CommandOption("--workflow <GUID>")]
    [Description("Workflow target (repeatable). Use with --scope Workflows.")]
    public Guid[] Workflows { get; set; } = [];

    [CommandOption("--email <ADDRESS>")]
    [Description("Email recipient route (repeatable).")]
    public string[] Emails { get; set; } = [];

    [CommandOption("--webhook <URL>")]
    [Description("Generic-webhook route URL (repeatable).")]
    public string[] Webhooks { get; set; } = [];

    [CommandOption("--cooldown-minutes <N>")] public int? CooldownMinutes { get; set; }
    [CommandOption("--min-occurrences <N>")] public int? MinOccurrences { get; set; }
    [CommandOption("--occurrence-window-minutes <N>")] public int? OccurrenceWindowMinutes { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class AlertingCreateCommand : BaseCommand<AlertingWriteSettings>
{
    public AlertingCreateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, AlertingWriteSettings s, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) { writer.Error("--name ist Pflicht."); return ExitCodes.Error; }
        if (string.IsNullOrWhiteSpace(s.EventTypes)) { writer.Error("--event-types ist Pflicht."); return ExitCodes.Error; }

        var routes = AlertingCommandHelpers.BuildRoutes(s.Emails, s.Webhooks);
        if (routes.Count == 0) { writer.Error("Mindestens ein --email oder --webhook ist Pflicht."); return ExitCodes.Error; }

        var scope = s.Scope ?? "Global";
        var req = new SaveNotificationRuleRequest(
            s.Name, s.Description, !s.Disabled,
            AlertingCommandHelpers.SplitEvents(s.EventTypes), s.FilterJson, scope,
            s.CooldownMinutes ?? 0, s.MinOccurrences ?? 1, s.OccurrenceWindowMinutes ?? 0,
            routes, AlertingCommandHelpers.BuildTargets(scope, s.Folders, s.Workflows), s.DedupKeyTemplate);

        var api = ClientFactory.Create(session);
        var created = await api.CreateAlertingRuleAsync(req, ct);
        writer.Success($"Alerting rule angelegt: [bold]{Markup.Escape(created.Name)}[/].");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AlertingUpdateCommand : BaseCommand<AlertingUpdateSettings>
{
    public AlertingUpdateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, AlertingUpdateSettings s, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var current = (await api.ListAlertingRulesAsync(ct)).FirstOrDefault(x => x.Id == s.Id);
        if (current is null) { writer.Error($"Alerting rule {s.Id} nicht gefunden."); return ExitCodes.Error; }

        var scope = s.Scope ?? current.ScopeKind;
        var isEnabled = s.Enabled ? true : (s.Disabled ? false : current.IsEnabled);
        var eventTypes = string.IsNullOrWhiteSpace(s.EventTypes) ? current.EventTypes : AlertingCommandHelpers.SplitEvents(s.EventTypes);
        // Routes: replace only when the caller passed any; otherwise keep the current routes as
        // returned by the API (their secret values are already masked with a placeholder, not
        // the real value, so re-sending them as-is is safe).
        var routes = (s.Emails.Length > 0 || s.Webhooks.Length > 0)
            ? AlertingCommandHelpers.BuildRoutes(s.Emails, s.Webhooks)
            : current.Routes;
        var targets = (s.Folders.Length > 0 || s.Workflows.Length > 0)
            ? AlertingCommandHelpers.BuildTargets(scope, s.Folders, s.Workflows)
            : current.Targets;

        var req = new SaveNotificationRuleRequest(
            s.Name ?? current.Name, s.Description ?? current.Description, isEnabled,
            eventTypes, s.FilterJson ?? current.FilterExpressionJson, scope,
            s.CooldownMinutes ?? current.CooldownMinutes,
            s.MinOccurrences ?? current.MinOccurrences,
            s.OccurrenceWindowMinutes ?? current.OccurrenceWindowMinutes,
            routes, targets, s.DedupKeyTemplate ?? current.DedupKeyTemplate);

        await api.UpdateAlertingRuleAsync(s.Id, req, ct);
        writer.Success($"Alerting rule [bold]{Markup.Escape(req.Name)}[/] aktualisiert.");
        return ExitCodes.Success;
    }
}

public sealed class AlertingUpdateSettings : AlertingWriteSettings
{
    [CommandArgument(0, "<RULE-ID>")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class AlertingDeleteCommand : BaseCommand<AlertingIdSettings>
{
    public AlertingDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, AlertingIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"Alerting rule [red]{settings.Id}[/] wirklich löschen?", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }
        var api = ClientFactory.Create(session);
        await api.DeleteAlertingRuleAsync(settings.Id, ct);
        writer.Success("Alerting rule gelöscht.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class AlertingTestFireCommand : BaseCommand<AlertingIdSettings>
{
    public AlertingTestFireCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, AlertingIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.TestFireAlertingRuleAsync(settings.Id, ct);
        writer.WriteData(result.Results, (console, list) =>
        {
            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Channel").AddColumn("Target").AddColumn("Result").AddColumn("Error");
            foreach (var r in list)
                table.AddRow(
                    Markup.Escape(r.Channel), Markup.Escape(r.Target),
                    r.Success ? "[green]ok[/]" : "[red]failed[/]",
                    Markup.Escape(r.Error ?? "-"));
            console.Write(table);
        });
        return result.AllSucceeded ? ExitCodes.Success : ExitCodes.Error;
    }
}

public sealed class AlertingDeliveriesSettings : GlobalSettings
{
    [CommandOption("--rule <GUID>")]
    [Description("Filter to one rule's deliveries.")]
    public Guid? Rule { get; set; }

    [CommandOption("--status <STATUS>")]
    [Description("Filter by status: Pending | Sent | Failed.")]
    public string? Status { get; set; }

    [CommandOption("--limit <N>")]
    [Description("Max rows (default 100, max 500).")]
    public int Limit { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class AlertingDeliveriesCommand : BaseCommand<AlertingDeliveriesSettings>
{
    public AlertingDeliveriesCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, AlertingDeliveriesSettings s, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListAlertingDeliveriesAsync(s.Rule, s.Status, s.Limit, ct);
        writer.WriteData(rows, (console, list) => Renderers.AlertingDeliveries(console, list));
        return ExitCodes.Success;
    }
}

internal static class AlertingCommandHelpers
{
    public static List<string> SplitEvents(string csv)
        => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static List<NotificationRouteDto> BuildRoutes(string[] emails, string[] webhooks)
    {
        var list = new List<NotificationRouteDto>();
        var order = 0;
        foreach (var e in emails)
            if (!string.IsNullOrWhiteSpace(e)) list.Add(new NotificationRouteDto(null, "Email", e.Trim(), null, order++));
        foreach (var w in webhooks)
            if (!string.IsNullOrWhiteSpace(w)) list.Add(new NotificationRouteDto(null, "GenericWebhook", w.Trim(), null, order++));
        return list;
    }

    public static List<NotificationRuleTargetDto> BuildTargets(string scope, Guid[] folders, Guid[] workflows)
    {
        var list = new List<NotificationRuleTargetDto>();
        if (scope.Equals("Folders", StringComparison.OrdinalIgnoreCase))
            list.AddRange(folders.Select(f => new NotificationRuleTargetDto("Folder", f)));
        else if (scope.Equals("Workflows", StringComparison.OrdinalIgnoreCase))
            list.AddRange(workflows.Select(w => new NotificationRuleTargetDto("Workflow", w)));
        return list;
    }
}

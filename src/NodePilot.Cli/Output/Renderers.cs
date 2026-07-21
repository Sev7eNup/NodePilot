using NodePilot.Cli.Api.Dtos;
using Spectre.Console;

namespace NodePilot.Cli.Output;

/// <summary>
/// Spectre table renderers for each list/detail shape. Kept in one file so it is
/// trivial to spot how a column is built and to keep formatting consistent.
/// </summary>
public static class Renderers
{
    public static void Workflows(IAnsiConsole console, IReadOnlyList<WorkflowResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Name")
            .AddColumn("State")
            .AddColumn("Lock")
            .AddColumn("Activities")
            .AddColumn("Last Run")
            .AddColumn("Updated");
        foreach (var w in rows)
        {
            var state = w.IsEnabled ? "[green]Enabled[/]" : "[grey]Disabled[/]";
            var locker = string.IsNullOrEmpty(w.CheckedOutByUserName) ? "-" : $"[yellow]{w.CheckedOutByUserName}[/]";
            var lastRun = w.LastExecution is null
                ? "-"
                : $"{StatusMarkup(w.LastExecution.Status)} {w.LastExecution.StartedAt.ToLocalTime():g}";
            table.AddRow(
                ShortGuid(w.Id),
                Markup.Escape(w.Name),
                state,
                locker,
                w.ActivityCount.ToString(),
                lastRun,
                w.UpdatedAt.ToLocalTime().ToString("g"));
        }
        console.Write(table);
    }

    public static void WorkflowDetail(IAnsiConsole console, WorkflowResponse w)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("Id", w.Id.ToString());
        grid.AddRow("Name", Markup.Escape(w.Name));
        grid.AddRow("Description", Markup.Escape(w.Description ?? ""));
        grid.AddRow("State", w.IsEnabled ? "[green]Enabled[/]" : "[grey]Disabled[/]");
        grid.AddRow("Version", w.Version.ToString());
        grid.AddRow("Triggers", string.Join(", ", w.TriggerTypes));
        grid.AddRow("Activities", w.ActivityCount.ToString());
        grid.AddRow("Lock", string.IsNullOrEmpty(w.CheckedOutByUserName) ? "-" : $"[yellow]{w.CheckedOutByUserName}[/] @ {w.CheckedOutAt:u}");
        grid.AddRow("Updated", w.UpdatedAt.ToLocalTime().ToString("u"));
        grid.AddRow("Updated By", w.UpdatedBy ?? "-");
        if (w.LastExecution is not null)
            grid.AddRow("Last Exec", $"{StatusMarkup(w.LastExecution.Status)} {w.LastExecution.StartedAt.ToLocalTime():g} ({w.LastExecution.DurationMs}ms)");
        console.Write(grid);
    }

    public static void Executions(IAnsiConsole console, IReadOnlyList<ExecutionResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Workflow")
            .AddColumn("Status")
            .AddColumn("Started")
            .AddColumn("Duration")
            .AddColumn("By");
        foreach (var e in rows)
        {
            var dur = e.CompletedAt.HasValue
                ? $"{(e.CompletedAt.Value - e.StartedAt).TotalSeconds:F1}s"
                : "-";
            table.AddRow(
                ShortGuid(e.Id),
                ShortGuid(e.WorkflowId),
                StatusMarkup(e.Status),
                e.StartedAt.ToLocalTime().ToString("g"),
                dur,
                Markup.Escape(e.TriggeredBy ?? "-"));
        }
        console.Write(table);
    }

    public static void ExecutionDetail(IAnsiConsole console, ExecutionResponse e)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("Id", e.Id.ToString());
        grid.AddRow("Workflow", e.WorkflowId.ToString());
        grid.AddRow("Status", StatusMarkup(e.Status));
        grid.AddRow("Started", e.StartedAt.ToLocalTime().ToString("u"));
        grid.AddRow("Completed", e.CompletedAt?.ToLocalTime().ToString("u") ?? "-");
        grid.AddRow("Triggered By", e.TriggeredBy ?? "-");
        if (!string.IsNullOrEmpty(e.ErrorMessage)) grid.AddRow("Error", Markup.Escape(e.ErrorMessage));
        if (!string.IsNullOrEmpty(e.TraceId)) grid.AddRow("Trace", e.TraceId);
        console.Write(grid);
    }

    public static void Steps(IAnsiConsole console, IReadOnlyList<StepExecutionResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Step")
            .AddColumn("Type")
            .AddColumn("Status")
            .AddColumn("Started")
            .AddColumn("Duration")
            .AddColumn("Attempts");
        foreach (var s in rows)
        {
            var dur = s is { StartedAt: not null, CompletedAt: not null }
                ? $"{(s.CompletedAt!.Value - s.StartedAt!.Value).TotalSeconds:F1}s"
                : "-";
            table.AddRow(
                Markup.Escape(s.StepName ?? s.StepId),
                Markup.Escape(s.StepType),
                StatusMarkup(s.Status),
                s.StartedAt?.ToLocalTime().ToString("g") ?? "-",
                dur,
                s.AttemptCount.ToString());
        }
        console.Write(table);
    }

    public static void Audit(IAnsiConsole console, AuditPageResponse page)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Time")
            .AddColumn("Action")
            .AddColumn("User")
            .AddColumn("IP")
            .AddColumn("Resource")
            .AddColumn("Details");
        foreach (var a in page.Items)
        {
            var resource = a.ResourceType is null && a.ResourceId is null
                ? "-"
                : $"{a.ResourceType ?? "?"}/{(a.ResourceId is null ? "-" : ShortGuid(a.ResourceId.Value))}";
            var details = string.IsNullOrEmpty(a.Details) ? "-" : (a.Details!.Length > 80 ? a.Details[..77] + "..." : a.Details);
            // Username column wins over the UserId fallback — frozen-at-write so deleted
            // users still show up as their original name. Falls back to short id if the
            // audit row predates the username column.
            var user = !string.IsNullOrEmpty(a.Username)
                ? a.Username
                : (a.UserId is null ? "-" : ShortGuid(a.UserId.Value));
            table.AddRow(
                a.Timestamp.ToLocalTime().ToString("u"),
                Markup.Escape(a.Action),
                Markup.Escape(user),
                Markup.Escape(a.IpAddress ?? "-"),
                Markup.Escape(resource),
                Markup.Escape(details));
        }
        console.Write(table);
        if (page.NextCursor is not null)
        {
            console.MarkupLine($"[grey]next cursor: --after-ts {page.NextCursor.Timestamp:o} --after-id {page.NextCursor.Id}[/]");
        }
    }

    public static void Versions(IAnsiConsole console, IReadOnlyList<WorkflowVersionInfo> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Ver")
            .AddColumn("Name")
            .AddColumn("Created")
            .AddColumn("Author")
            .AddColumn("Note")
            .AddColumn("Current");
        foreach (var v in rows)
        {
            table.AddRow(
                v.Version.ToString(),
                Markup.Escape(v.Name),
                v.CreatedAt.ToLocalTime().ToString("u"),
                v.CreatedBy ?? "-",
                Markup.Escape(v.ChangeNote ?? "-"),
                v.IsCurrent ? "[green]*[/]" : "");
        }
        console.Write(table);
    }

    public static void WorkflowVersionDetail(IAnsiConsole console, WorkflowVersionDetail v)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("Version", v.Version.ToString());
        grid.AddRow("Name", Markup.Escape(v.Name));
        grid.AddRow("Description", Markup.Escape(v.Description ?? ""));
        grid.AddRow("Created", v.CreatedAt.ToLocalTime().ToString("u"));
        grid.AddRow("Author", v.CreatedBy ?? "-");
        grid.AddRow("Note", Markup.Escape(v.ChangeNote ?? "-"));
        grid.AddRow("Current", v.IsCurrent ? "[green]yes[/]" : "no");
        grid.AddRow("Definition", $"({v.DefinitionJson.Length} bytes — use -o json to dump full)");
        console.Write(grid);
    }

    public static void Machines(IAnsiConsole console, IReadOnlyList<MachineResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Name")
            .AddColumn("Hostname")
            .AddColumn("Port")
            .AddColumn("SSL")
            .AddColumn("Reachable")
            .AddColumn("Last Check");
        foreach (var m in rows)
        {
            table.AddRow(
                ShortGuid(m.Id),
                Markup.Escape(m.Name),
                Markup.Escape(m.Hostname),
                m.WinRmPort.ToString(),
                m.UseSsl ? "[green]yes[/]" : "no",
                m.IsReachable ? "[green]yes[/]" : "[red]no[/]",
                m.LastConnectivityCheck?.ToLocalTime().ToString("g") ?? "-");
        }
        console.Write(table);
    }

    public static void MachineDetail(IAnsiConsole console, MachineResponse m)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("Id", m.Id.ToString());
        grid.AddRow("Name", Markup.Escape(m.Name));
        grid.AddRow("Hostname", Markup.Escape(m.Hostname));
        grid.AddRow("Port", m.WinRmPort.ToString());
        grid.AddRow("SSL", m.UseSsl ? "[green]yes[/]" : "no");
        grid.AddRow("Reachable", m.IsReachable ? "[green]yes[/]" : "[red]no[/]");
        grid.AddRow("Default Credential", m.DefaultCredentialId?.ToString() ?? "-");
        grid.AddRow("Tags", Markup.Escape(m.Tags ?? "-"));
        grid.AddRow("Last Check", m.LastConnectivityCheck?.ToLocalTime().ToString("u") ?? "-");
        console.Write(grid);
    }

    public static void Credentials(IAnsiConsole console, IReadOnlyList<CredentialResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Name")
            .AddColumn("Username")
            .AddColumn("Domain");
        foreach (var c in rows)
            table.AddRow(ShortGuid(c.Id), Markup.Escape(c.Name), Markup.Escape(c.Username), Markup.Escape(c.Domain ?? "-"));
        console.Write(table);
    }

    public static void GlobalVariables(IAnsiConsole console, IReadOnlyList<GlobalVariableResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Name")
            .AddColumn("Value")
            .AddColumn("Secret")
            .AddColumn("Description")
            .AddColumn("Updated");
        foreach (var v in rows)
        {
            // Server already masks secrets to "***" — render as-is so the CLI never
            // accidentally exposes a value the API said was secret.
            table.AddRow(
                ShortGuid(v.Id),
                Markup.Escape(v.Name),
                Markup.Escape(v.Value ?? "-"),
                v.IsSecret ? "[yellow]yes[/]" : "no",
                Markup.Escape(v.Description ?? "-"),
                v.UpdatedAt.ToLocalTime().ToString("g"));
        }
        console.Write(table);
    }

    public static void GlobalVariableFolders(IAnsiConsole console, IReadOnlyList<GlobalVariableFolderResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Path")
            .AddColumn("Depth")
            .AddColumn("Variables");
        foreach (var f in rows.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            table.AddRow(
                ShortGuid(f.Id),
                Markup.Escape(f.Path),
                f.Depth.ToString(),
                f.VariableCount.ToString());
        }
        console.Write(table);
    }

    public static void MaintenanceWindows(IAnsiConsole console, IReadOnlyList<MaintenanceWindowResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Name")
            .AddColumn("Enabled")
            .AddColumn("Mode")
            .AddColumn("Scope")
            .AddColumn("When")
            .AddColumn("Targets");
        foreach (var w in rows)
        {
            table.AddRow(
                ShortGuid(w.Id),
                Markup.Escape(w.Name),
                w.IsEnabled ? "[green]yes[/]" : "[dim]no[/]",
                Markup.Escape(w.Mode),
                Markup.Escape(w.ScopeKind),
                Markup.Escape(DescribeWhen(w)),
                w.ScopeKind == "Global" ? "[dim]all[/]" : w.Targets.Count.ToString());
        }
        console.Write(table);
    }

    public static void AlertingRules(IAnsiConsole console, IReadOnlyList<NotificationRuleResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Name")
            .AddColumn("Enabled")
            .AddColumn("Events")
            .AddColumn("Scope")
            .AddColumn("Cooldown")
            .AddColumn("Routes");
        foreach (var r in rows)
        {
            var events = string.Join(",", r.EventTypes.Take(2));
            if (r.EventTypes.Count > 2) events += $",+{r.EventTypes.Count - 2}";
            var scope = r.ScopeKind == "Global" ? "Global" : $"{r.ScopeKind} ({r.Targets.Count})";
            var routes = r.Routes.Count == 0 ? "[dim]-[/]" : string.Join(",", r.Routes.Select(x => x.Channel).Distinct());
            table.AddRow(
                ShortGuid(r.Id),
                Markup.Escape(r.Name),
                r.IsEnabled ? "[green]yes[/]" : "[dim]no[/]",
                Markup.Escape(events),
                Markup.Escape(scope),
                r.CooldownMinutes > 0 ? $"{r.CooldownMinutes}m" : "[dim]-[/]",
                Markup.Escape(routes));
        }
        console.Write(table);
    }

    public static void AlertingDeliveries(IAnsiConsole console, IReadOnlyList<NotificationDeliveryDto> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("When")
            .AddColumn("Rule")
            .AddColumn("Route")
            .AddColumn("Status")
            .AddColumn("Try")
            .AddColumn("Error");
        foreach (var d in rows)
        {
            var statusMarkup = d.Status switch
            {
                "Sent" => "[green]Sent[/]",
                "Failed" => "[red]Failed[/]",
                _ => $"[yellow]{Markup.Escape(d.Status)}[/]",
            };
            table.AddRow(
                Markup.Escape(d.CreatedAt.ToLocalTime().ToString("g")),
                Markup.Escape((d.RuleName ?? "?") + (d.IsTest ? " [test]" : "")),
                Markup.Escape($"{d.Channel ?? "?"}:{d.Target ?? "?"}"),
                statusMarkup,
                d.Attempt.ToString(),
                Markup.Escape(d.Error ?? "-"));
        }
        console.Write(table);
    }

    public static void SystemAlertPolicies(IAnsiConsole console, IReadOnlyList<SystemAlertPolicyResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Name")
            .AddColumn("Enabled")
            .AddColumn("Source")
            .AddColumn("Preset")
            .AddColumn("Scope")
            .AddColumn("Cooldown")
            .AddColumn("Routes");
        foreach (var p in rows)
        {
            var scope = p.ScopeKind == "Global" ? "Global" : $"{p.ScopeKind} ({p.Targets.Count})";
            var routes = p.Routes.Count == 0 ? "[dim]-[/]" : string.Join(",", p.Routes.Select(x => x.Channel).Distinct());
            table.AddRow(
                ShortGuid(p.Id),
                Markup.Escape(p.Name),
                p.IsEnabled ? "[green]yes[/]" : "[dim]no[/]",
                Markup.Escape(p.SourceId),
                Markup.Escape(p.PresetId ?? "[dim]-[/]"),
                Markup.Escape(scope),
                p.CooldownMinutes > 0 ? $"{p.CooldownMinutes}m" : "[dim]-[/]",
                Markup.Escape(routes));
        }
        console.Write(table);
    }

    public static void SystemAlertSources(IAnsiConsole console, IReadOnlyList<SystemAlertSourceDto> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Source")
            .AddColumn("Category")
            .AddColumn("Scope")
            .AddColumn("Severity")
            .AddColumn("Fields")
            .AddColumn("Params")
            .AddColumn("Presets")
            .AddColumn("Available");
        foreach (var s in rows)
        {
            table.AddRow(
                Markup.Escape(s.SourceId),
                Markup.Escape(s.Category),
                Markup.Escape(s.ScopeCapability),
                Markup.Escape(s.DefaultSeverity ?? "-"),
                s.Fields.Count.ToString(),
                s.Parameters.Count.ToString(),
                s.Presets.Count.ToString(),
                s.Available ? "[green]yes[/]" : "[red]no[/]");
        }
        console.Write(table);
    }

    private static string DescribeWhen(MaintenanceWindowResponse w)
    {
        if (w.Recurrence == "OneTime")
            return $"{w.OneTimeStartUtc?.ToLocalTime():g} → {w.OneTimeEndUtc?.ToLocalTime():g}";
        if (w.Recurrence == "Weekly")
        {
            var days = DaysFromMask(w.WeeklyDaysMask);
            return $"{days} {MinuteToHhmm(w.WeeklyStartMinuteOfDay)}-{MinuteToHhmm(w.WeeklyEndMinuteOfDay)} ({w.TimeZoneId})";
        }
        return w.Recurrence;
    }

    private static readonly string[] DayAbbrev = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    private static string DaysFromMask(int mask)
    {
        var parts = new List<string>();
        for (var i = 0; i < 7; i++)
            if ((mask & (1 << i)) != 0) parts.Add(DayAbbrev[i]);
        return parts.Count == 0 ? "-" : string.Join(",", parts);
    }

    private static string MinuteToHhmm(int? minute)
        => minute is { } m ? $"{m / 60:D2}:{m % 60:D2}" : "--:--";

    public static void Users(IAnsiConsole console, IReadOnlyList<UserResponse> rows)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Id")
            .AddColumn("Username")
            .AddColumn("Role")
            .AddColumn("Active")
            .AddColumn("Created");
        foreach (var u in rows)
        {
            table.AddRow(
                ShortGuid(u.Id),
                Markup.Escape(u.Username),
                Markup.Escape(u.Role),
                u.IsActive ? "[green]yes[/]" : "[grey]no[/]",
                u.CreatedAt.ToLocalTime().ToString("u"));
        }
        console.Write(table);
    }

    public static void Dashboard(IAnsiConsole console, DashboardStats s)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("Workflows", $"{s.WorkflowsTotal} ({s.WorkflowsEnabled} enabled)");
        grid.AddRow("Machines", $"{s.MachinesTotal} ({s.MachinesReachable} reachable)");
        grid.AddRow("Executions (total)", s.ExecutionsTotal.ToString());
        grid.AddRow("Last 24h",
            $"[grey]total[/] {s.Last24h.Total}  [green]ok[/] {s.Last24h.Succeeded}  " +
            $"[red]fail[/] {s.Last24h.Failed}  [yellow]running[/] {s.Last24h.Running}  [grey]cancel[/] {s.Last24h.Cancelled}");
        grid.AddRow("Active runs", s.Running.Count.ToString());
        grid.AddRow("Recent runs", s.Recent.Count.ToString());
        grid.AddRow("Armed triggers", s.ArmedTriggers.Count.ToString());
        console.Write(grid);

        if (s.TopWorkflows.Count > 0)
        {
            console.WriteLine();
            var top = new Table().Title("Top workflows (24h)").Border(TableBorder.Rounded)
                .AddColumn("Name").AddColumn("Runs").AddColumn("OK").AddColumn("Fail");
            foreach (var w in s.TopWorkflows)
                top.AddRow(Markup.Escape(w.Name), w.RunCount.ToString(), w.SuccessCount.ToString(), w.FailCount.ToString());
            console.Write(top);
        }
    }

    public static void StepStats(IAnsiConsole console, IReadOnlyDictionary<string, StepStats> stats)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Step")
            .AddColumn("Runs")
            .AddColumn("Failed")
            .AddColumn("Fail-Rate")
            .AddColumn("Avg ms")
            .AddColumn("p95 ms")
            .AddColumn("Last ms");
        foreach (var (stepId, s) in stats.OrderByDescending(kv => kv.Value.FailureRate))
        {
            var rate = $"{s.FailureRate * 100:F1}%";
            var rateMarkup = s.FailureRate >= 0.10 ? $"[red]{rate}[/]"
                           : s.FailureRate >= 0.01 ? $"[yellow]{rate}[/]"
                           : $"[grey]{rate}[/]";
            table.AddRow(
                Markup.Escape(stepId),
                s.TotalRuns.ToString(),
                s.FailedRuns.ToString(),
                rateMarkup,
                s.AvgDurationMs.ToString(),
                s.P95DurationMs.ToString(),
                s.LastDurationMs.ToString());
        }
        console.Write(table);
    }

    public static void TelemetrySummary(IAnsiConsole console, TelemetrySummaryResponse r)
    {
        if (!r.Available)
        {
            console.MarkupLine("[grey]Prometheus is not configured.[/]");
            return;
        }
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Key").AddColumn("Title").AddColumn("Value").AddColumn("Unit");
        foreach (var p in r.Panels)
        {
            var value = p.Error is not null ? $"[red]err[/] {Markup.Escape(p.Error)}"
                     : p.Value is null ? "-"
                     : p.Value.Value.ToString("F2");
            table.AddRow(Markup.Escape(p.Key), Markup.Escape(p.Title), value, Markup.Escape(p.Unit));
        }
        console.Write(table);
    }

    public static string StatusMarkup(string status) => status switch
    {
        "Succeeded" => "[green]Succeeded[/]",
        "Failed" => "[red]Failed[/]",
        "Running" => "[yellow]Running[/]",
        "Cancelled" => "[grey]Cancelled[/]",
        "Skipped" => "[grey]Skipped[/]",
        "Paused" => "[blue]Paused[/]",
        "Pending" => "[silver]Pending[/]",
        _ => Markup.Escape(status),
    };

    private static string ShortGuid(Guid id) => id.ToString()[..8];
}

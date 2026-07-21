using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Maintenance;

[SupportedOSPlatform("windows")]
public sealed class MaintenanceListCommand : BaseCommand<GlobalSettings>
{
    public MaintenanceListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListMaintenanceWindowsAsync(ct);
        writer.WriteData(rows, (console, list) => Renderers.MaintenanceWindows(console, list));
        return ExitCodes.Success;
    }
}

public sealed class MaintenanceIdSettings : GlobalSettings
{
    [CommandArgument(0, "<WINDOW-ID>")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class MaintenanceGetCommand : BaseCommand<MaintenanceIdSettings>
{
    public MaintenanceGetCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, MaintenanceIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = (await api.ListMaintenanceWindowsAsync(ct)).FirstOrDefault(x => x.Id == settings.Id);
        if (w is null) { writer.Error($"Maintenance window {settings.Id} nicht gefunden."); return ExitCodes.Error; }
        writer.WriteData(new[] { w }, (console, list) => Renderers.MaintenanceWindows(console, list));
        return ExitCodes.Success;
    }
}

// Shared option surface for create/update. On update, only the provided options override the
// current value (the command fetches the current window first). Not sealed — MaintenanceUpdateSettings
// extends it to add the window-id argument.
public class MaintenanceWriteSettings : GlobalSettings
{
    [CommandOption("--name <NAME>")] public string? Name { get; set; }
    [CommandOption("--description <TEXT>")] public string? Description { get; set; }
    [CommandOption("--disabled")]
    [Description("Create/keep the window disabled (default enabled).")]
    public bool Disabled { get; set; }
    [CommandOption("--enabled")]
    [Description("Force-enable the window (overrides --disabled on update).")]
    public bool Enabled { get; set; }

    [CommandOption("--mode <MODE>")]
    [Description("Blackout (block during) or AllowOnly (run only during). Default Blackout.")]
    public string? Mode { get; set; }

    [CommandOption("--scope <SCOPE>")]
    [Description("Global | Folders | Workflows. Default Global.")]
    public string? Scope { get; set; }

    [CommandOption("--recurrence <REC>")]
    [Description("OneTime | Weekly | Cron. Default Weekly.")]
    public string? Recurrence { get; set; }

    [CommandOption("--tz <TZID>")]
    [Description("Time zone id for Weekly/Cron windows (e.g. 'W. Europe Standard Time'). Default UTC.")]
    public string? TimeZoneId { get; set; }

    [CommandOption("--cron <EXPR>")]
    [Description("Quartz cron expression for Cron windows (seconds field, e.g. '0 0 3 ? * SAT').")]
    public string? Cron { get; set; }

    [CommandOption("--duration-minutes <N>")]
    [Description("How long the window stays open after each cron fire (Cron windows, > 0).")]
    public int? DurationMinutes { get; set; }

    [CommandOption("--one-time-start <ISO>")] public DateTime? OneTimeStart { get; set; }
    [CommandOption("--one-time-end <ISO>")] public DateTime? OneTimeEnd { get; set; }

    [CommandOption("--days <LIST>")]
    [Description("Comma-separated weekdays for Weekly, e.g. Mon,Tue,Sat.")]
    public string? Days { get; set; }

    [CommandOption("--start <TIME>")]
    [Description("Weekly local start time as HH:MM.")]
    public string? Start { get; set; }

    [CommandOption("--end <TIME>")]
    [Description("Weekly local end time as HH:MM (may be earlier than start to wrap past midnight).")]
    public string? End { get; set; }

    [CommandOption("--folder <GUID>")]
    [Description("Folder target (repeatable). Use with --scope Folders.")]
    public Guid[] Folders { get; set; } = [];

    [CommandOption("--workflow <GUID>")]
    [Description("Workflow target (repeatable). Use with --scope Workflows.")]
    public Guid[] Workflows { get; set; } = [];
}

[SupportedOSPlatform("windows")]
public sealed class MaintenanceCreateCommand : BaseCommand<MaintenanceWriteSettings>
{
    public MaintenanceCreateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, MaintenanceWriteSettings s, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) { writer.Error("--name ist Pflicht."); return ExitCodes.Error; }

        var scope = s.Scope ?? "Global";
        var recurrence = s.Recurrence ?? "Weekly";
        if (!MaintenanceCommandHelpers.TryParseTime(s.Start, out var startMin, out var err1)) { writer.Error(err1); return ExitCodes.Error; }
        if (!MaintenanceCommandHelpers.TryParseTime(s.End, out var endMin, out var err2)) { writer.Error(err2); return ExitCodes.Error; }
        var targets = MaintenanceCommandHelpers.BuildTargets(scope, s.Folders, s.Workflows);

        var req = new CreateMaintenanceWindowRequest(
            s.Name, s.Description, !s.Disabled,
            s.Mode ?? "Blackout", scope, recurrence,
            s.OneTimeStart, s.OneTimeEnd,
            MaintenanceCommandHelpers.MaskFromDays(s.Days), startMin, endMin,
            s.Cron, s.DurationMinutes, s.TimeZoneId, targets);

        var api = ClientFactory.Create(session);
        var created = await api.CreateMaintenanceWindowAsync(req, ct);
        writer.Success($"Maintenance window angelegt: [bold]{Markup.Escape(created.Name)}[/] ({created.Mode}/{created.ScopeKind}).");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class MaintenanceUpdateCommand : BaseCommand<MaintenanceUpdateSettings>
{
    public MaintenanceUpdateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, MaintenanceUpdateSettings s, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var current = (await api.ListMaintenanceWindowsAsync(ct)).FirstOrDefault(x => x.Id == s.Id);
        if (current is null) { writer.Error($"Maintenance window {s.Id} nicht gefunden."); return ExitCodes.Error; }

        var scope = s.Scope ?? current.ScopeKind;
        var recurrence = s.Recurrence ?? current.Recurrence;
        var isEnabled = s.Enabled ? true : (s.Disabled ? false : current.IsEnabled);

        int? startMin = current.WeeklyStartMinuteOfDay, endMin = current.WeeklyEndMinuteOfDay;
        if (s.Start is not null && !MaintenanceCommandHelpers.TryParseTime(s.Start, out startMin, out var err1)) { writer.Error(err1); return ExitCodes.Error; }
        if (s.End is not null && !MaintenanceCommandHelpers.TryParseTime(s.End, out endMin, out var err2)) { writer.Error(err2); return ExitCodes.Error; }

        // Targets: replace only when the caller passed new ones for the active scope; otherwise keep current.
        var targets = (s.Folders.Length > 0 || s.Workflows.Length > 0)
            ? MaintenanceCommandHelpers.BuildTargets(scope, s.Folders, s.Workflows)
            : current.Targets;

        var req = new UpdateMaintenanceWindowRequest(
            s.Name ?? current.Name,
            s.Description ?? current.Description,
            isEnabled,
            s.Mode ?? current.Mode,
            scope,
            recurrence,
            s.OneTimeStart ?? current.OneTimeStartUtc,
            s.OneTimeEnd ?? current.OneTimeEndUtc,
            s.Days is not null ? MaintenanceCommandHelpers.MaskFromDays(s.Days) : current.WeeklyDaysMask,
            startMin, endMin,
            s.Cron ?? current.CronExpression,
            s.DurationMinutes ?? current.DurationMinutes,
            s.TimeZoneId ?? current.TimeZoneId,
            targets);

        await api.UpdateMaintenanceWindowAsync(s.Id, req, ct);
        writer.Success($"Maintenance window [bold]{Markup.Escape(req.Name)}[/] aktualisiert.");
        return ExitCodes.Success;
    }
}

public sealed class MaintenanceUpdateSettings : MaintenanceWriteSettings
{
    [CommandArgument(0, "<WINDOW-ID>")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class MaintenanceDeleteCommand : BaseCommand<MaintenanceIdSettings>
{
    public MaintenanceDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, MaintenanceIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"Maintenance window [red]{settings.Id}[/] wirklich löschen?", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }
        var api = ClientFactory.Create(session);
        await api.DeleteMaintenanceWindowAsync(settings.Id, ct);
        writer.Success("Maintenance window gelöscht.");
        return ExitCodes.Success;
    }
}

internal static class MaintenanceCommandHelpers
{
    private static readonly Dictionary<string, int> DayBits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sun"] = 0, ["sunday"] = 0,
        ["mon"] = 1, ["monday"] = 1,
        ["tue"] = 2, ["tuesday"] = 2,
        ["wed"] = 3, ["wednesday"] = 3,
        ["thu"] = 4, ["thursday"] = 4,
        ["fri"] = 5, ["friday"] = 5,
        ["sat"] = 6, ["saturday"] = 6,
    };

    public static int MaskFromDays(string? days)
    {
        if (string.IsNullOrWhiteSpace(days)) return 0;
        var mask = 0;
        foreach (var token in days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (DayBits.TryGetValue(token, out var bit)) mask |= 1 << bit;
        return mask;
    }

    public static bool TryParseTime(string? hhmm, out int? minuteOfDay, out string error)
    {
        minuteOfDay = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(hhmm)) return true; // optional
        var parts = hhmm.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m)
            && h is >= 0 and <= 23 && m is >= 0 and <= 59)
        {
            minuteOfDay = h * 60 + m;
            return true;
        }
        error = $"Ungültige Zeit '{hhmm}' — erwartet HH:MM.";
        return false;
    }

    public static List<MaintenanceWindowTargetDto> BuildTargets(string scope, Guid[] folders, Guid[] workflows)
    {
        var list = new List<MaintenanceWindowTargetDto>();
        if (scope.Equals("Folders", StringComparison.OrdinalIgnoreCase))
            list.AddRange(folders.Select(f => new MaintenanceWindowTargetDto("Folder", f)));
        else if (scope.Equals("Workflows", StringComparison.OrdinalIgnoreCase))
            list.AddRange(workflows.Select(w => new MaintenanceWindowTargetDto("Workflow", w)));
        return list;
    }
}

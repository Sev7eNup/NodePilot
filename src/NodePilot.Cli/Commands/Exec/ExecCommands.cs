using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Exec;

public sealed class ExecListSettings : GlobalSettings
{
    [CommandOption("-w|--workflow <ID-OR-NAME>")]
    [Description("Filter by a workflow GUID or name.")]
    public string? Workflow { get; set; }

    [CommandOption("--limit <N>")]
    [Description("Limit rows shown (server caps at 100).")]
    public int? Limit { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class ExecListCommand : BaseCommand<ExecListSettings>
{
    public ExecListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, ExecListSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        Guid? workflowId = null;
        if (!string.IsNullOrWhiteSpace(settings.Workflow))
        {
            var w = await Commands.WorkflowResolver.ResolveAsync(api, settings.Workflow, ct);
            workflowId = w.Id;
        }

        var rows = await api.ListExecutionsAsync(workflowId, ct);
        if (settings.Limit is > 0) rows = rows.Take(settings.Limit.Value).ToList();
        writer.WriteData(rows, (console, list) => Renderers.Executions(console, list));
        return ExitCodes.Success;
    }
}

public class ExecIdSettings : GlobalSettings
{
    [CommandArgument(0, "<EXECUTION-ID>")]
    [Description("Execution GUID.")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class ExecGetCommand : BaseCommand<ExecIdSettings>
{
    public ExecGetCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, ExecIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var e = await api.GetExecutionAsync(settings.Id, ct);
        writer.WriteData(e, (console, value) => Renderers.ExecutionDetail(console, value));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ExecStepsCommand : BaseCommand<ExecIdSettings>
{
    public ExecStepsCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, ExecIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var steps = await api.GetStepsAsync(settings.Id, ct);
        writer.WriteData(steps, (console, list) => Renderers.Steps(console, list));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ExecCancelCommand : BaseCommand<ExecIdSettings>
{
    public ExecCancelCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, ExecIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        await api.CancelExecutionAsync(settings.Id, ct);
        writer.Success($"Execution {settings.Id} cancelled.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ExecRetryCommand : BaseCommand<ExecIdSettings>
{
    public ExecRetryCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, ExecIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var retry = await api.RetryExecutionAsync(settings.Id, ct);
        writer.Success($"Re-Run gestartet: {retry.Id}");
        writer.WriteData(retry, (console, value) => Renderers.ExecutionDetail(console, value));
        return ExitCodes.Success;
    }
}

public sealed class ExecResumeSettings : ExecIdSettings
{
    [CommandOption("--step <STEP-ID>")]
    [Description("Step id of the paused breakpoint to resume.")]
    public string Step { get; set; } = "";

    [CommandOption("--mode <MODE>")]
    [Description("Resume mode: continue (default) | stepOver | stop.")]
    public string Mode { get; set; } = "continue";

    [CommandOption("--override <KEY=VALUE>")]
    [Description("Variable override (repeatable). Mixed into the step's variables before execution.")]
    public string[] Overrides { get; set; } = Array.Empty<string>();
}

[SupportedOSPlatform("windows")]
public sealed class ExecResumeCommand : BaseCommand<ExecResumeSettings>
{
    public ExecResumeCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, ExecResumeSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Step))
        {
            writer.Error("--step <STEP-ID> ist Pflicht (Engine kennt mehrere Pausen pro Lauf).");
            return ExitCodes.Error;
        }

        Dictionary<string, string>? overrides = null;
        if (settings.Overrides.Length > 0)
        {
            overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var raw in settings.Overrides)
            {
                var idx = raw.IndexOf('=');
                if (idx <= 0)
                {
                    writer.Error($"Ungültiger Override '{raw}' — erwartet KEY=VALUE.");
                    return ExitCodes.Error;
                }
                overrides[raw[..idx]] = raw[(idx + 1)..];
            }
        }

        var api = ClientFactory.Create(session);
        await api.ResumeExecutionAsync(settings.Id, new ResumeDebugRequest(settings.Step, settings.Mode, overrides), ct);
        writer.Success($"Resume signal sent (mode={settings.Mode}, step={settings.Step}).");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ExecPausedStepsCommand : BaseCommand<ExecIdSettings>
{
    public ExecPausedStepsCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, ExecIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.GetPausedStepsAsync(settings.Id, ct);
        writer.WriteData(rows, (console, list) =>
        {
            if (list.Count == 0) { console.MarkupLine("[grey]No paused steps.[/]"); return; }
            foreach (var s in list) console.MarkupLine($"  • {Markup.Escape(s)}");
        });
        return ExitCodes.Success;
    }
}

public sealed class ExecWatchSettings : ExecIdSettings
{
    [CommandOption("--no-signalr")]
    [Description("Force polling fallback even when SignalR is available.")]
    public bool NoSignalR { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class ExecWatchCommand : BaseCommand<ExecWatchSettings>
{
    public ExecWatchCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override Task<int> RunAsync(CommandContext _, ExecWatchSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        return ExecWatcher.RunAsync(api, session, settings.Id, writer, followAfterTerminal: false, ct, forcePolling: settings.NoSignalR);
    }
}

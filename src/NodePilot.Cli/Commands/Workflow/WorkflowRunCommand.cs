using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Workflow;

public sealed class WorkflowRunSettings : WorkflowGetSettings
{
    [CommandOption("-p|--params <KV>")]
    [Description("Parameter as key=value (repeatable).")]
    public string[] Params { get; set; } = Array.Empty<string>();

    [CommandOption("--wait")]
    [Description("Block until the run reaches a terminal status (poll-based).")]
    public bool Wait { get; set; }

    [CommandOption("--follow")]
    [Description("Stream live step events via SignalR until the run finishes.")]
    public bool Follow { get; set; }

    [CommandOption("--debug")]
    [Description("Start the run in debug mode (breakpoints will pause).")]
    public bool Debug { get; set; }

    [CommandOption("--timeout <SECONDS>")]
    [Description("Cap the whole run at N seconds.")]
    public int? TimeoutSeconds { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowRunCommand : BaseCommand<WorkflowRunSettings>
{
    public WorkflowRunCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowRunSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);

        Dictionary<string, string>? parameters;
        try
        {
            parameters = RunParameterParser.Parse(settings.Params);
        }
        catch (ArgumentException ex)
        {
            writer.Error(ex.Message);
            return ExitCodes.Error;
        }

        var req = new ExecuteWorkflowRequest(parameters, settings.TimeoutSeconds, settings.Debug);
        var execution = await api.ExecuteWorkflowAsync(w.Id, req, ct);
        writer.Info($"Execution gestartet: [bold]{execution.Id}[/]");

        if (!settings.Wait && !settings.Follow)
        {
            writer.WriteData(execution, (console, value) => Renderers.ExecutionDetail(console, value));
            return ExitCodes.Success;
        }

        if (settings.Follow)
            return await Exec.ExecWatcher.RunAsync(api, session, execution.Id, writer, followAfterTerminal: false, ct);

        // --wait → simple polling.
        return await PollUntilTerminalAsync(api, execution.Id, writer, ct);
    }

    internal static async Task<int> PollUntilTerminalAsync(NodePilotApiClient api, Guid execId, OutputWriter writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var current = await api.GetExecutionAsync(execId, ct);
            if (IsTerminal(current.Status))
            {
                writer.WriteData(current, (console, value) => Renderers.ExecutionDetail(console, value));
                return ExitCodeFor(current.Status);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return ExitCodes.Error;
    }

    internal static bool IsTerminal(string status) => status is "Succeeded" or "Failed" or "Cancelled" or "Skipped";

    internal static int ExitCodeFor(string status) => status switch
    {
        "Succeeded" => ExitCodes.Success,
        "Failed" => ExitCodes.RunFailed,
        "Cancelled" => ExitCodes.RunFailed,
        _ => ExitCodes.Error,
    };
}

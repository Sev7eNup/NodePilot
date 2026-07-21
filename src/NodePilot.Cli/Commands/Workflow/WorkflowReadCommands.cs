using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Workflow;

public class WorkflowGetSettings : GlobalSettings
{
    [CommandArgument(0, "<ID-OR-NAME>")]
    [Description("Workflow GUID or unique name.")]
    public string IdOrName { get; set; } = "";
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowListCommand : BaseCommand<GlobalSettings>
{
    public WorkflowListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListWorkflowsAsync(ct);
        writer.WriteData(rows, (console, list) => Renderers.Workflows(console, list));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowGetCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowGetCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        writer.WriteData(w, (console, value) => Renderers.WorkflowDetail(console, value));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowVersionsCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowVersionsCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var versions = await api.ListVersionsAsync(w.Id, ct);
        writer.WriteData(versions, (console, list) => Renderers.Versions(console, list));
        return ExitCodes.Success;
    }
}

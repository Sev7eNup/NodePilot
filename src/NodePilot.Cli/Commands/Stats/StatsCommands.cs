using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Stats;

[SupportedOSPlatform("windows")]
public sealed class DashboardCommand : BaseCommand<GlobalSettings>
{
    public DashboardCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var d = await api.GetDashboardAsync(ct);
        writer.WriteData(d, (console, value) => Renderers.Dashboard(console, value));
        return ExitCodes.Success;
    }
}

public sealed class WorkflowStatsSettings : GlobalSettings
{
    [CommandArgument(0, "<ID-OR-NAME>")]
    [Description("Workflow GUID or unique name.")]
    public string IdOrName { get; set; } = "";

    [CommandOption("--window-days <N>")]
    [Description("Aggregation window in days (1..365, default 30).")]
    public int? WindowDays { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowStatsCommand : BaseCommand<WorkflowStatsSettings>
{
    public WorkflowStatsCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowStatsSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await Commands.WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var stats = await api.GetStepStatsAsync(w.Id, settings.WindowDays, ct);
        writer.WriteData(stats, (console, value) => Renderers.StepStats(console, value));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ObservabilitySummaryCommand : BaseCommand<GlobalSettings>
{
    public ObservabilitySummaryCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var r = await api.GetObservabilitySummaryAsync(ct);
        writer.WriteData(r, (console, value) => Renderers.TelemetrySummary(console, value));
        return ExitCodes.Success;
    }
}

using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Workflow;

// ---- np workflow contract <id-or-name> -------------------------------------

[SupportedOSPlatform("windows")]
public sealed class WorkflowContractCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowContractCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);

        // Use the by-name endpoint directly when the arg is non-Guid — it mirrors the engine's
        // resolution (exact-case wins, else case-insensitive; ambiguous → 409), so the CLI
        // shows the same contract the runtime would resolve
        // (which is the point: CI gates need to verify "what would startWorkflow see").
        WorkflowContractResponse contract;
        if (Guid.TryParse(settings.IdOrName, out var id))
            contract = await api.GetContractAsync(id, ct);
        else
            contract = await api.GetContractByNameAsync(settings.IdOrName, ct);

        writer.WriteData(contract, (console, value) =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("Workflow", $"{Markup.Escape(value.WorkflowName)} ({value.WorkflowId})");
            grid.AddRow("ManualTrigger", value.HasManualTrigger ? "[green]yes[/]" : "[grey]no (no declared input contract)[/]");
            grid.AddRow("ReturnData", value.HasReturnData ? "[green]yes[/]" : "[grey]no (only system outputs)[/]");
            if (value.HasMultipleReturnDataNodes)
                grid.AddRow("[yellow]Warning[/]", "Multiple returnData nodes — outputs are 'may be available', not guaranteed.");
            console.Write(grid);

            console.WriteLine();
            var inputs = new Table().Title("Inputs (manualTrigger.parameters)").Border(TableBorder.Rounded)
                .AddColumn("Name").AddColumn("Type").AddColumn("Required").AddColumn("Default").AddColumn("Description").AddColumn("Conflict");
            if (value.Inputs.Count == 0)
                inputs.AddRow("[grey]<none>[/]", "", "", "", "", "");
            else
                foreach (var i in value.Inputs)
                    inputs.AddRow(
                        Markup.Escape(i.Name),
                        Markup.Escape(i.Type),
                        i.Required ? "[yellow]yes[/]" : "no",
                        Markup.Escape(i.Default ?? "-"),
                        Markup.Escape(i.Description ?? "-"),
                        i.HasConflict ? "[red]yes[/]" : "");
            console.Write(inputs);

            console.WriteLine();
            var outputs = new Table().Title("Outputs (returnData + system)").Border(TableBorder.Rounded)
                .AddColumn("Name").AddColumn("Source");
            foreach (var o in value.Outputs)
            {
                var sourceMarkup = o.Source switch
                {
                    "system" => "[grey]system[/]",
                    "single" => "[green]single[/]",
                    "multiple" => "[yellow]multiple[/]",
                    _ => Markup.Escape(o.Source),
                };
                outputs.AddRow(Markup.Escape(o.Name), sourceMarkup);
            }
            console.Write(outputs);
        });
        return ExitCodes.Success;
    }
}

// ---- np workflow coverage <id-or-name> -------------------------------------

public sealed class WorkflowCoverageSettings : WorkflowGetSettings
{
    [CommandOption("--window-days <N>")]
    [Description("Lookback window in days (default 30, capped 365 server-side).")]
    public int? WindowDays { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowCoverageCommand : BaseCommand<WorkflowCoverageSettings>
{
    public WorkflowCoverageCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, WorkflowCoverageSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var coverage = await api.GetCoverageAsync(w.Id, settings.WindowDays, ct);

        writer.WriteData(coverage, (console, value) =>
        {
            var hdr = new Grid().AddColumn().AddColumn();
            hdr.AddRow("Workflow", $"{Markup.Escape(w.Name)} ({value.WorkflowId})");
            hdr.AddRow("Window", $"{value.WindowDays} days");
            hdr.AddRow("Total Executions", value.TotalExecutions.ToString());
            hdr.AddRow("Oldest in Window", value.OldestExecutionInWindow?.ToLocalTime().ToString("u") ?? "-");
            console.Write(hdr);

            if (value.Nodes.Count == 0)
            {
                console.WriteLine();
                console.MarkupLine("[grey]No coverage data — workflow did not run in the window.[/]");
                return;
            }

            console.WriteLine();
            var t = new Table().Border(TableBorder.Rounded)
                .AddColumn("Step")
                .AddColumn("Executed")
                .AddColumn("Failed")
                .AddColumn("Skipped")
                .AddColumn("Last Executed")
                .AddColumn("Last Failed");
            foreach (var n in value.Nodes.OrderByDescending(n => n.ExecutedCount))
            {
                var executed = n.ExecutedCount == 0 ? "[red]0[/]" : n.ExecutedCount.ToString();
                var failed = n.FailedCount == 0 ? "0" : $"[red]{n.FailedCount}[/]";
                t.AddRow(
                    Markup.Escape(n.StepId),
                    executed,
                    failed,
                    n.SkippedCount.ToString(),
                    n.LastExecutedAt?.ToLocalTime().ToString("g") ?? "-",
                    n.LastFailedAt?.ToLocalTime().ToString("g") ?? "-");
            }
            console.Write(t);
        });
        return ExitCodes.Success;
    }
}

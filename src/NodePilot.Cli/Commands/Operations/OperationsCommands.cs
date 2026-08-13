using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Operations;

// `np operations graph` — the live-ops / NOC graph as an RBAC-scoped snapshot: workflow nodes,
// their call edges (startWorkflow/forEach), and currently-running executions. Read-only.

public sealed class OperationsGraphSettings : GlobalSettings
{
    [CommandOption("--window <MINUTES>")]
    [Description("Look-back window for finished runs, in minutes: 30 (default) or 60. Other values clamp to 30 server-side.")]
    public int WindowMinutes { get; init; } = 20;
}

[SupportedOSPlatform("windows")]
public sealed class OperationsGraphCommand : BaseCommand<OperationsGraphSettings>
{
    public OperationsGraphCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, OperationsGraphSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var graph = await api.GetOperationsGraphAsync(ct, settings.WindowMinutes);
        writer.WriteData(graph, Render);
        return ExitCodes.Success;
    }

    private static void Render(IAnsiConsole console, OperationsGraphResponse graph)
    {
        var runningTotal = graph.Nodes.Sum(n => n.RunningCount);
        console.MarkupLine($"[bold]Workflows:[/] {graph.Nodes.Count}   [bold]Edges:[/] {graph.Edges.Count}   [bold]Running:[/] {runningTotal}   [bold]Recent:[/] {graph.Recent.Count} (last {graph.Meta.WindowMinutes} min)");
        if (graph.Meta.RecentTruncated)
        {
            // Not "omitted" — the runs past the cap come back as density. Report what they add up
            // to, which is the number a `np operations graph` reader actually wants.
            var counted = graph.Density.Sum(l => l.Buckets.Sum(b => b.Total));
            var failed = graph.Density.Sum(l => l.Buckets.Sum(b => b.Failed));
            var prefix = graph.Meta.DensityCapped ? "more than " : "";
            console.MarkupLine(
                $"[yellow]Note:[/] {prefix}{counted} finished runs in this window ({failed} failed) — "
                + $"only the newest {graph.Recent.Count} are listed individually, the rest are aggregated "
                + $"into {graph.Meta.DensityBucketSeconds}s density buckets.");
        }

        var nodes = new Table().Border(TableBorder.Rounded)
            .AddColumn("Workflow").AddColumn("Folder").AddColumn("Enabled").AddColumn("Running").AddColumn("Last");
        foreach (var n in graph.Nodes.OrderByDescending(n => n.RunningCount).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            nodes.AddRow(
                Markup.Escape(n.Name),
                Markup.Escape(n.FolderPath),
                n.IsEnabled ? "yes" : "no",
                n.RunningCount > 0 ? $"[yellow]{n.RunningCount}[/]" : "0",
                Markup.Escape(n.LastStatus ?? "-"));
        }
        console.Write(nodes);

        if (graph.Edges.Count > 0)
        {
            var nameById = graph.Nodes.ToDictionary(n => n.WorkflowId, n => n.Name);
            var edges = new Table().Border(TableBorder.Rounded)
                .AddColumn("From").AddColumn("To").AddColumn("Kind").AddColumn("Ref").AddColumn("Calls");
            foreach (var e in graph.Edges)
            {
                var to = e.Target is { } t && nameById.TryGetValue(t, out var name)
                    ? name
                    : $"{e.RefStatus}: {e.RawRef}";
                edges.AddRow(
                    Markup.Escape(nameById.GetValueOrDefault(e.Source, e.Source.ToString())),
                    Markup.Escape(to),
                    Markup.Escape(e.Kind),
                    Markup.Escape(e.RefStatus),
                    e.CallCount.ToString());
            }
            console.Write(edges);
        }
    }
}

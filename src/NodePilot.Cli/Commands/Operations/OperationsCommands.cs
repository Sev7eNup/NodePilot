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

[SupportedOSPlatform("windows")]
public sealed class OperationsGraphCommand : BaseCommand<GlobalSettings>
{
    public OperationsGraphCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var graph = await api.GetOperationsGraphAsync(ct);
        writer.WriteData(graph, Render);
        return ExitCodes.Success;
    }

    private static void Render(IAnsiConsole console, OperationsGraphResponse graph)
    {
        var runningTotal = graph.Nodes.Sum(n => n.RunningCount);
        console.MarkupLine($"[bold]Workflows:[/] {graph.Nodes.Count}   [bold]Edges:[/] {graph.Edges.Count}   [bold]Running:[/] {runningTotal}   [bold]Recent:[/] {graph.Recent.Count}");

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

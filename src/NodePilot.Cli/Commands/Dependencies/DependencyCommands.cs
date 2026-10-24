using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Dependencies;

public class DependencyListSettings : GlobalSettings
{
    [CommandOption("--query <TEXT>")] public string? Query { get; init; }
    [CommandOption("--kind <KIND>")] public string? Kind { get; init; }
    [CommandOption("--state <STATE>")] [Description("problems or unused (unused excludes workflows)")] public string? State { get; init; }
    [CommandOption("--sort <SORT>")] [Description("name (default) or usage")] public string Sort { get; init; } = "name";
    [CommandOption("--cursor <CURSOR>")] public string? Cursor { get; init; }
    [CommandOption("--limit <COUNT>")] public int Limit { get; init; } = 50;
}
public class DependencyShowSettings : GlobalSettings
{
    [CommandArgument(0, "<kind>")] [Description("workflow, machine, credential, variable or customActivity")] public string Kind { get; init; } = "";
    [CommandArgument(1, "<id>")] public Guid Id { get; init; }
    [CommandOption("--direction <DIRECTION>")] public string Direction { get; init; } = "usedBy";
    [CommandOption("--cursor <CURSOR>")] public string? Cursor { get; init; }
    [CommandOption("--limit <COUNT>")] public int Limit { get; init; } = 50;
}
public sealed class DependencyEvidenceSettings : DependencyShowSettings
{
    [CommandArgument(2, "<related-kind>")] public string RelatedKind { get; init; } = "";
    [CommandArgument(3, "<related-id>")] public Guid RelatedId { get; init; }
}

[SupportedOSPlatform("windows")]
public sealed class DependencyListCommand(SessionResolver s, ApiClientFactory f) : BaseCommand<DependencyListSettings>(s, f)
{
    protected override async Task<int> RunAsync(CommandContext _, DependencyListSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var data = await ClientFactory.Create(session).ListDependencyObjectsAsync(settings.Query, settings.Kind, settings.State, settings.Sort, settings.Cursor, settings.Limit, ct);
        writer.WriteData(data, (console, result) =>
        {
            var table = new Table().AddColumn("Kind").AddColumn("Name").AddColumn("Context").AddColumn("Used by (direct)")
                .AddColumn("Used by (indirect)").AddColumn("Problems").AddColumn("ID");
            foreach (var row in result.Items)
                table.AddRow(Markup.Escape(row.Object.Kind), Markup.Escape(row.Object.Name), Markup.Escape(row.Object.Context),
                    row.UsedByDirect.ToString(), row.UsedByIndirect.ToString(), row.Problems.ToString(), row.Object.Id.ToString());
            console.Write(table);
            if (result.NextCursor is not null) console.WriteLine("Next cursor: " + result.NextCursor);
        });
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class DependencyShowCommand(SessionResolver s, ApiClientFactory f) : BaseCommand<DependencyShowSettings>(s, f)
{
    protected override async Task<int> RunAsync(CommandContext _, DependencyShowSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var data = await ClientFactory.Create(session).GetDependenciesAsync(settings.Kind, settings.Id, settings.Direction, settings.Cursor, settings.Limit, ct);
        writer.WriteData(data, (console, result) =>
        {
            console.WriteLine($"{result.Object.Name}: {result.Counts.Direct} direct, {result.Counts.Indirect} indirect, {result.Counts.Possible} possible agent calls. Visible saved configuration only.");
            if (!result.Complete) console.WriteLine("Analysis limited: counts are lower bounds.");
            var table = new Table().AddColumn("Kind").AddColumn("Name").AddColumn("Relationship").AddColumn("Path");
            foreach (var row in result.Items)
                table.AddRow(Markup.Escape(row.Object.Kind), Markup.Escape(row.Object.Name), row.Possible ? "possible" : row.Distance == 1 ? "direct" : "indirect",
                    Markup.Escape(string.Join("; ", row.Paths.FirstOrDefault()?.Links.Select(l => $"{l.Source.Name} --{l.Relation}--> {l.Target.Name}") ?? [])));
            console.Write(table);
            foreach (var problem in result.Problems) console.WriteLine($"{problem.Location.Label}: {problem.Kind} — {problem.Status}");
            foreach (var limitation in result.Limitations) console.WriteLine("Coverage: " + limitation);
            if (result.NextCursor is not null) console.WriteLine("Next cursor: " + result.NextCursor);
        });
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class DependencyEvidenceCommand(SessionResolver s, ApiClientFactory f) : BaseCommand<DependencyEvidenceSettings>(s, f)
{
    protected override async Task<int> RunAsync(CommandContext _, DependencyEvidenceSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var data = await ClientFactory.Create(session).GetDependencyEvidenceAsync(settings.Kind, settings.Id, settings.RelatedKind,
            settings.RelatedId, settings.Direction, settings.Cursor, settings.Limit, ct);
        writer.WriteData(data, (console, result) =>
        {
            var table = new Table().AddColumn("Source").AddColumn("Step").AddColumn("Field").AddColumn("Disabled");
            foreach (var row in result.Items) table.AddRow($"{Markup.Escape(row.SourceKind)}:{row.SourceId}", Markup.Escape(row.Label), Markup.Escape(row.Field), row.Disabled ? "yes" : "no");
            console.Write(table);
            console.WriteLine($"{result.Total} reference locations; revision {result.AnalysisRevision}");
            if (result.NextCursor is not null) console.WriteLine("Next cursor: " + result.NextCursor);
        });
        return ExitCodes.Success;
    }
}

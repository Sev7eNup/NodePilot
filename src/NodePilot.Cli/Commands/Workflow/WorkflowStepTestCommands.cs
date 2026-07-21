using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Workflow;

// `np workflow step-test` — POSTs /api/workflows/{id}/steps/{stepId}/test.
//
// Mock variables come in via repeatable --mock key=value (flat map like
// "checkDisk.output=7"). ConfigOverride is a separate flag — `--config-file <PATH>` —
// because operators iterating on a step are normally NOT going to type a JSON blob
// onto the command line. The file form is the realistic ergonomy.

public sealed class WorkflowStepTestSettings : GlobalSettings
{
    [CommandArgument(0, "<WORKFLOW-ID-OR-NAME>")] public string IdOrName { get; set; } = "";
    [CommandArgument(1, "<STEP-ID>")] [Description("Step id (node id in the workflow JSON).")] public string StepId { get; set; } = "";

    [CommandOption("-m|--mock <KV>")]
    [Description("Mock-variable as `stepName.field=value` (repeatable). E.g. -m checkDisk.output=7 -m checkDisk.param.freeGb=7.")]
    public string[] Mock { get; set; } = Array.Empty<string>();

    [CommandOption("--config-file <PATH>")]
    [Description("Path to unsaved `data.config` JSON. Requires workflow Edit permission and your active edit lock. Use `-` for stdin.")]
    public string? ConfigFile { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowStepTestCommand : BaseCommand<WorkflowStepTestSettings>
{
    public WorkflowStepTestCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, WorkflowStepTestSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);

        Dictionary<string, string>? mock = null;
        if (settings.Mock.Length > 0)
        {
            mock = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in settings.Mock)
            {
                var eq = entry.IndexOf('=');
                if (eq <= 0) { writer.Error($"--mock muss Form `key=value` haben (war: '{entry}')."); return ExitCodes.Error; }
                mock[entry[..eq]] = entry[(eq + 1)..];
            }
        }

        JsonElement? configOverride = null;
        if (!string.IsNullOrWhiteSpace(settings.ConfigFile))
        {
            try
            {
                var json = settings.ConfigFile == "-"
                    ? await Console.In.ReadToEndAsync(ct)
                    : await File.ReadAllTextAsync(settings.ConfigFile, ct);
                using var doc = JsonDocument.Parse(json);
                configOverride = doc.RootElement.Clone();
            }
            catch (IOException ex) { writer.Error($"--config-file: {ex.Message}"); return ExitCodes.Error; }
            catch (JsonException ex) { writer.Error($"--config-file ist kein gültiges JSON: {ex.Message}"); return ExitCodes.Error; }
        }

        var req = new StepTestRequest(mock, configOverride);
        var result = await api.TestStepAsync(w.Id, settings.StepId, req, ct);

        writer.WriteData(result, (console, value) =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("Result", value.Success ? "[green]success[/]" : "[red]failed[/]");
            grid.AddRow("Duration", $"{value.DurationMs:F0} ms");
            if (!string.IsNullOrEmpty(value.ErrorMessage)) grid.AddRow("Error", Markup.Escape(value.ErrorMessage));
            console.Write(grid);

            if (!string.IsNullOrEmpty(value.Output))
            {
                console.WriteLine();
                console.MarkupLine("[grey]── Output ───────────────────────────────[/]");
                console.WriteLine(value.Output);
            }
            if (!string.IsNullOrEmpty(value.ErrorOutput))
            {
                console.WriteLine();
                console.MarkupLine("[red]── ErrorOutput ──────────────────────────[/]");
                console.WriteLine(value.ErrorOutput);
            }
            if (value.OutputParameters.Count > 0)
            {
                console.WriteLine();
                var t = new Table().Title("Output Parameters").Border(TableBorder.Rounded)
                    .AddColumn("Key").AddColumn("Value");
                foreach (var (k, v) in value.OutputParameters)
                    t.AddRow(Markup.Escape(k), Markup.Escape(v));
                console.Write(t);
            }
        });

        return result.Success ? ExitCodes.Success : ExitCodes.RunFailed;
    }
}

// ---- step-test-context (variable schema for a specific step) ---------------

public sealed class WorkflowStepTestContextSettings : GlobalSettings
{
    [CommandArgument(0, "<WORKFLOW-ID-OR-NAME>")] public string IdOrName { get; set; } = "";
    [CommandArgument(1, "<STEP-ID>")] public string StepId { get; set; } = "";

    [CommandOption("--execution <GUID>")]
    [Description("Specific execution id to source values from. Default: latest terminal run.")]
    public Guid? ExecutionId { get; set; }

    [CommandOption("--list-runs")]
    [Description("Instead of the context, list recent executions and whether this step ran in each.")]
    public bool ListRuns { get; set; }

    [CommandOption("--limit <N>")]
    [Description("Cap for --list-runs (default 10).")]
    public int? Limit { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowStepTestContextCommand : BaseCommand<WorkflowStepTestContextSettings>
{
    public WorkflowStepTestContextCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, WorkflowStepTestContextSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);

        if (settings.ListRuns)
        {
            var runs = await api.ListStepTestContextRunsAsync(w.Id, settings.StepId, settings.Limit, ct);
            writer.WriteData(runs, (console, list) =>
            {
                var t = new Table().Border(TableBorder.Rounded)
                    .AddColumn("Execution").AddColumn("Started").AddColumn("Status").AddColumn("By").AddColumn("Step Ran");
                foreach (var r in list)
                    t.AddRow(
                        r.ExecutionId.ToString()[..8],
                        r.StartedAt.ToLocalTime().ToString("u"),
                        Renderers.StatusMarkup(r.Status),
                        Markup.Escape(r.TriggeredBy ?? "-"),
                        r.StepRan ? "[green]yes[/]" : "[grey]no[/]");
                console.Write(t);
            });
            return ExitCodes.Success;
        }

        var ctx = await api.GetStepTestContextAsync(w.Id, settings.StepId, settings.ExecutionId, ct);
        writer.WriteData(ctx, (console, value) =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("Source Execution", value.ExecutionId?.ToString() ?? "-");
            grid.AddRow("Executed At", value.ExecutedAt?.ToLocalTime().ToString("u") ?? "-");
            grid.AddRow("Status", value.Status ?? "-");
            console.Write(grid);

            console.WriteLine();
            var t = new Table().Border(TableBorder.Rounded)
                .AddColumn("Key").AddColumn("Origin").AddColumn("Source").AddColumn("Value");
            foreach (var v in value.Variables)
                t.AddRow(
                    Markup.Escape(v.Key),
                    Markup.Escape(v.Origin),
                    Markup.Escape(v.Source),
                    v.Value is null ? "[grey]<null>[/]" : Markup.Escape(v.Value));
            console.Write(t);
        });
        return ExitCodes.Success;
    }
}

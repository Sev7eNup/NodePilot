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

public sealed class WorkflowExportSettings : GlobalSettings
{
    [CommandArgument(0, "[ID-OR-NAME]")]
    [Description("Workflow GUID or name. Omit when --all is used.")]
    public string? IdOrName { get; set; }

    [CommandOption("--all")]
    [Description("Export all workflows in one envelope.")]
    public bool All { get; set; }

    [CommandOption("--out <PATH>")]
    [Description("Write the envelope to this file (default: stdout).")]
    public string? Out { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowExportCommand : BaseCommand<WorkflowExportSettings>
{
    public WorkflowExportCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowExportSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        WorkflowExportEnvelope envelope;
        if (settings.All)
        {
            envelope = await api.ExportAllAsync(ct);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(settings.IdOrName))
            {
                writer.Error("Entweder ID-OR-NAME angeben oder --all setzen.");
                return ExitCodes.Error;
            }
            var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
            envelope = await api.ExportOneAsync(w.Id, ct);
        }

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(NodePilotApiClient.JsonOptions) { WriteIndented = true });
        if (!string.IsNullOrWhiteSpace(settings.Out))
        {
            await File.WriteAllTextAsync(settings.Out, json, ct);
            writer.Success($"Geschrieben: {settings.Out}");
        }
        else
        {
            await Console.Out.WriteLineAsync(json);
        }
        return ExitCodes.Success;
    }
}

public sealed class WorkflowImportSettings : GlobalSettings
{
    [CommandOption("-f|--file <PATH>")]
    [Description("Envelope JSON to import (or '-' for stdin).")]
    public string File { get; set; } = "";

    [CommandOption("--target-folder <GUID>")]
    [Description("Shared folder the import lands in (default: Root). Requires Edit on that folder.")]
    public Guid? TargetFolder { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowImportCommand : BaseCommand<WorkflowImportSettings>
{
    public WorkflowImportCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowImportSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        string json;
        if (settings.File == "-")
            json = await Console.In.ReadToEndAsync(ct);
        else if (!string.IsNullOrWhiteSpace(settings.File) && File.Exists(settings.File))
            json = await File.ReadAllTextAsync(settings.File, ct);
        else
        {
            writer.Error($"Datei nicht gefunden: {settings.File}");
            return ExitCodes.Error;
        }

        var envelope = JsonSerializer.Deserialize<WorkflowExportEnvelope>(json, NodePilotApiClient.JsonOptions);
        if (envelope is null) { writer.Error("Envelope konnte nicht geparst werden."); return ExitCodes.Error; }

        var api = ClientFactory.Create(session);
        var result = await api.ImportAsync(envelope, settings.TargetFolder, ct);
        writer.Success($"Imported: {result.Created}, Errors: {result.Errors.Count}");
        foreach (var e in result.Errors) writer.Warning($"  - {e}");
        return result.Errors.Count == 0 ? ExitCodes.Success : ExitCodes.Error;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowImportScorchCommand : BaseCommand<WorkflowImportSettings>
{
    public WorkflowImportScorchCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowImportSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        string xml;
        if (settings.File == "-")
            xml = await Console.In.ReadToEndAsync(ct);
        else if (!string.IsNullOrWhiteSpace(settings.File) && File.Exists(settings.File))
            xml = await File.ReadAllTextAsync(settings.File, ct);
        else
        {
            writer.Error($"Datei nicht gefunden: {settings.File}");
            return ExitCodes.Error;
        }

        var api = ClientFactory.Create(session);
        var result = await api.ImportScorchAsync(xml, settings.TargetFolder, ct);
        writer.Success($"SCOrch import: {result.Created} Workflows, {result.Variables.Count(v => v.CreatedNow)} neue Variablen.");
        foreach (var w in result.Workflows)
            writer.Info($"  + [bold]{Markup.Escape(w.Name)}[/] — {w.ActivityCount} Activities ({w.HeuristicCount} heuristisch, {w.FallbackCount} Fallbacks)");
        foreach (var w in result.Warnings) writer.Warning($"  ! {w}");
        foreach (var e in result.Errors) writer.Error($"  - {e}");
        // Errors should fail the command — translation may have produced unusable runbooks.
        return result.Errors.Count == 0 ? ExitCodes.Success : ExitCodes.Error;
    }
}

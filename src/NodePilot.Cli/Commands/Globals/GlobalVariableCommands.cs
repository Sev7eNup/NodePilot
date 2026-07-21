using System.ComponentModel;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Globals;

[SupportedOSPlatform("windows")]
public sealed class GlobalsListCommand : BaseCommand<GlobalSettings>
{
    public GlobalsListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListGlobalVariablesAsync(ct);
        writer.WriteData(rows, (console, list) => Renderers.GlobalVariables(console, list));
        return ExitCodes.Success;
    }
}

public sealed class GlobalsCreateSettings : GlobalSettings
{
    [CommandOption("--name <NAME>")]
    [Description("Variable name (matches [A-Za-z0-9_-]{1,100}, no dots or whitespace).")]
    public string? Name { get; set; }

    [CommandOption("--value <VALUE>")]
    [Description("Initial value. Prefer --value-stdin for secrets.")]
    public string? Value { get; set; }

    [CommandOption("--value-stdin")]
    [Description("Read the value from stdin (recommended for secrets).")]
    public bool ValueStdin { get; set; }

    [CommandOption("--secret")]
    [Description("Mark as secret — value is DPAPI-encrypted at rest and masked on read.")]
    public bool Secret { get; set; }

    [CommandOption("--description <TEXT>")]
    public string? Description { get; set; }

    [CommandOption("--folder <ID-OR-PATH>")]
    [Description("Folder to place the variable in (id, path, or name). Defaults to Root.")]
    public string? Folder { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsCreateCommand : BaseCommand<GlobalsCreateSettings>
{
    public GlobalsCreateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsCreateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            writer.Error("--name ist Pflicht.");
            return ExitCodes.Error;
        }

        var value = settings.Value;
        if (settings.ValueStdin)
            value = (await Console.In.ReadToEndAsync(ct)).TrimEnd('\r', '\n');
        if (value is null)
        {
            writer.Error("Wert fehlt — entweder --value oder --value-stdin.");
            return ExitCodes.Error;
        }

        var api = ClientFactory.Create(session);
        var folderId = await FolderResolver.ResolveAsync(api, settings.Folder, ct);
        var v = await api.CreateGlobalVariableAsync(new CreateGlobalVariableRequest(settings.Name, value, settings.Secret, settings.Description, folderId), ct);
        writer.Success($"Global angelegt: [bold]{Markup.Escape(v.Name)}[/] ({(v.IsSecret ? "[yellow]secret[/]" : "plain")}).");
        return ExitCodes.Success;
    }
}

public sealed class GlobalsUpdateSettings : GlobalSettings
{
    [CommandArgument(0, "<GLOBAL-ID>")]
    public Guid Id { get; set; }

    [CommandOption("--name <NAME>")] public string? Name { get; set; }
    [CommandOption("--value <VALUE>")] public string? Value { get; set; }
    [CommandOption("--value-stdin")] public bool ValueStdin { get; set; }
    [CommandOption("--secret")] public bool? Secret { get; set; }
    [CommandOption("--no-secret")] public bool ClearSecret { get; set; }
    [CommandOption("--description <TEXT>")] public string? Description { get; set; }
    [CommandOption("--folder <ID-OR-PATH>")]
    [Description("Move the variable to this folder (id, path, or name). Omit to keep the current folder.")]
    public string? Folder { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsUpdateCommand : BaseCommand<GlobalsUpdateSettings>
{
    public GlobalsUpdateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsUpdateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var current = (await api.ListGlobalVariablesAsync(ct)).FirstOrDefault(v => v.Id == settings.Id);
        if (current is null)
        {
            writer.Error($"Global Variable {settings.Id} nicht gefunden.");
            return ExitCodes.Error;
        }

        string? value = settings.Value;
        if (settings.ValueStdin)
            value = (await Console.In.ReadToEndAsync(ct)).TrimEnd('\r', '\n');

        var isSecret = !settings.ClearSecret && (settings.Secret ?? current.IsSecret);
        // --folder omitted → keep the variable's current folder.
        var folderId = settings.Folder is not null
            ? await FolderResolver.ResolveAsync(api, settings.Folder, ct)
            : current.FolderId;
        var req = new UpdateGlobalVariableRequest(
            settings.Name ?? current.Name,
            value, // null = "unchanged" on the server
            isSecret,
            settings.Description ?? current.Description,
            folderId);
        await api.UpdateGlobalVariableAsync(settings.Id, req, ct);
        writer.Success($"Global [bold]{Markup.Escape(req.Name)}[/] aktualisiert.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsDeleteCommand : BaseCommand<GlobalsUpdateSettings>
{
    public GlobalsDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsUpdateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"Global Variable [red]{settings.Id}[/] wirklich löschen?", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }
        var api = ClientFactory.Create(session);
        await api.DeleteGlobalVariableAsync(settings.Id, ct);
        writer.Success("Global Variable gelöscht.");
        return ExitCodes.Success;
    }
}

// ---- export ----------------------------------------------------------------

public sealed class GlobalsExportSettings : GlobalSettings
{
    [CommandOption("--file <PATH>")]
    [Description("Write JSON to this file instead of stdout.")]
    public string? File { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsExportCommand : BaseCommand<GlobalsExportSettings>
{
    public GlobalsExportCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsExportSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListGlobalVariablesAsync(ct);
        var exportable = rows.Select(v => new ImportableGlobalVariable(v.Name, v.Value, v.IsSecret, v.Description)).ToList();
        var json = JsonSerializer.Serialize(exportable, new JsonSerializerOptions(NodePilotApiClient.JsonOptions) { WriteIndented = true });

        if (!string.IsNullOrWhiteSpace(settings.File))
        {
            await File.WriteAllTextAsync(settings.File, json, ct);
            writer.Success($"Geschrieben: {settings.File} ({exportable.Count} Globals).");
        }
        else
        {
            await Console.Out.WriteLineAsync(json);
        }
        return ExitCodes.Success;
    }
}

// ---- import ----------------------------------------------------------------

public sealed class GlobalsImportSettings : GlobalSettings
{
    [CommandOption("-f|--file <PATH>")]
    [Description("JSON array to import (or '-' for stdin).")]
    public string File { get; set; } = "";

    [CommandOption("--upsert")]
    [Description("Update existing globals by name instead of skipping them.")]
    public bool Upsert { get; set; }

    [CommandOption("--dry-run")]
    [Description("Print what would happen without making any API calls.")]
    public bool DryRun { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsImportCommand : BaseCommand<GlobalsImportSettings>
{
    public GlobalsImportCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsImportSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
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

        List<ImportableGlobalVariable>? entries;
        try { entries = JsonSerializer.Deserialize<List<ImportableGlobalVariable>>(json, NodePilotApiClient.JsonOptions); }
        catch (JsonException ex) { writer.Error($"JSON-Fehler: {ex.Message}"); return ExitCodes.Error; }
        if (entries is null || entries.Count == 0) { writer.Info("Keine Einträge."); return ExitCodes.Success; }

        var api = ClientFactory.Create(session);
        var existing = settings.DryRun
            ? new Dictionary<string, GlobalVariableResponse>(StringComparer.OrdinalIgnoreCase)
            : (await api.ListGlobalVariablesAsync(ct)).ToDictionary(v => v.Name, v => v, StringComparer.OrdinalIgnoreCase);

        int created = 0, updated = 0, skipped = 0;
        foreach (var entry in entries)
        {
            var value = entry.Value ?? "";
            if (existing.TryGetValue(entry.Name, out var found))
            {
                if (settings.Upsert)
                {
                    writer.Info(settings.DryRun
                        ? $"[dim]dry-run[/] update [bold]{Markup.Escape(entry.Name)}[/]"
                        : $"update [bold]{Markup.Escape(entry.Name)}[/]");
                    if (!settings.DryRun)
                        await api.UpdateGlobalVariableAsync(found.Id, new UpdateGlobalVariableRequest(entry.Name, value, entry.IsSecret, entry.Description), ct);
                    updated++;
                }
                else
                {
                    writer.Info($"skip   [bold]{Markup.Escape(entry.Name)}[/] (exists, use --upsert to overwrite)");
                    skipped++;
                }
            }
            else
            {
                writer.Info(settings.DryRun
                    ? $"[dim]dry-run[/] create [bold]{Markup.Escape(entry.Name)}[/]"
                    : $"create [bold]{Markup.Escape(entry.Name)}[/]");
                if (!settings.DryRun)
                    await api.CreateGlobalVariableAsync(new CreateGlobalVariableRequest(entry.Name, value, entry.IsSecret, entry.Description), ct);
                created++;
            }
        }

        var suffix = settings.DryRun ? " [dim](dry-run)[/]" : "";
        writer.Success($"Created: {created}  Updated: {updated}  Skipped: {skipped}{suffix}");
        return ExitCodes.Success;
    }
}

using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Globals;

/// <summary>
/// Resolves a user-supplied folder token (a folder id GUID, a full path like <c>/Env/Prod</c>,
/// or a bare folder name) to a folder id, using the live folder list. Shared by the folder
/// sub-commands and the <c>--folder</c> option on <c>globals create/update</c>.
/// </summary>
internal static class FolderResolver
{
    public static async Task<Guid?> ResolveAsync(NodePilotApiClient api, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        if (Guid.TryParse(token, out var g)) return g;

        var folders = await api.ListGlobalVariableFoldersAsync(ct);
        var byPath = folders.Where(f => string.Equals(f.Path, token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byPath.Count == 1) return byPath[0].Id;
        var byName = folders.Where(f => string.Equals(f.Name, token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0].Id;

        throw new InvalidOperationException(byPath.Count > 1 || byName.Count > 1
            ? $"Folder '{token}' is ambiguous — use the folder id or full path."
            : $"Folder '{token}' not found — use the folder id, name, or path (see `np globals folder list`).");
    }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsFolderListCommand : BaseCommand<GlobalSettings>
{
    public GlobalsFolderListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListGlobalVariableFoldersAsync(ct);
        writer.WriteData(rows, (console, list) => Renderers.GlobalVariableFolders(console, list));
        return ExitCodes.Success;
    }
}

public sealed class GlobalsFolderCreateSettings : GlobalSettings
{
    [CommandOption("--name <NAME>")]
    [Description("Folder name (max 120 chars, unique among its siblings).")]
    public string? Name { get; set; }

    [CommandOption("--parent <ID-OR-PATH>")]
    [Description("Parent folder id, path, or name. Omit to create under Root.")]
    public string? Parent { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsFolderCreateCommand : BaseCommand<GlobalsFolderCreateSettings>
{
    public GlobalsFolderCreateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsFolderCreateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            writer.Error("--name ist Pflicht.");
            return ExitCodes.Error;
        }
        var api = ClientFactory.Create(session);
        var parentId = await FolderResolver.ResolveAsync(api, settings.Parent, ct);
        var folder = await api.CreateGlobalVariableFolderAsync(new CreateGlobalVariableFolderRequest(parentId, settings.Name), ct);
        writer.Success($"Ordner angelegt: [bold]{Markup.Escape(folder.Path)}[/].");
        return ExitCodes.Success;
    }
}

public sealed class GlobalsFolderRenameSettings : GlobalSettings
{
    [CommandArgument(0, "<FOLDER-ID>")]
    public Guid Id { get; set; }

    [CommandOption("--name <NAME>")]
    public string? Name { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsFolderRenameCommand : BaseCommand<GlobalsFolderRenameSettings>
{
    public GlobalsFolderRenameCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsFolderRenameSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            writer.Error("--name ist Pflicht.");
            return ExitCodes.Error;
        }
        var api = ClientFactory.Create(session);
        await api.RenameGlobalVariableFolderAsync(settings.Id, new UpdateGlobalVariableFolderRequest(settings.Name), ct);
        writer.Success($"Ordner umbenannt: [bold]{Markup.Escape(settings.Name)}[/].");
        return ExitCodes.Success;
    }
}

public sealed class GlobalsFolderMoveSettings : GlobalSettings
{
    [CommandArgument(0, "<FOLDER-ID>")]
    public Guid Id { get; set; }

    [CommandOption("--parent <ID-OR-PATH>")]
    [Description("New parent folder id, path, or name. Omit to move to Root.")]
    public string? Parent { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsFolderMoveCommand : BaseCommand<GlobalsFolderMoveSettings>
{
    public GlobalsFolderMoveCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsFolderMoveSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var parentId = await FolderResolver.ResolveAsync(api, settings.Parent, ct);
        await api.MoveGlobalVariableFolderAsync(settings.Id, new MoveGlobalVariableFolderRequest(parentId), ct);
        writer.Success("Ordner verschoben.");
        return ExitCodes.Success;
    }
}

public sealed class GlobalsFolderDeleteSettings : GlobalSettings
{
    [CommandArgument(0, "<FOLDER-ID>")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsFolderDeleteCommand : BaseCommand<GlobalsFolderDeleteSettings>
{
    public GlobalsFolderDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsFolderDeleteSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"Ordner [red]{settings.Id}[/] wirklich löschen? (muss leer sein)", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }
        var api = ClientFactory.Create(session);
        await api.DeleteGlobalVariableFolderAsync(settings.Id, ct);
        writer.Success("Ordner gelöscht.");
        return ExitCodes.Success;
    }
}

public sealed class GlobalsMoveVariableSettings : GlobalSettings
{
    [CommandArgument(0, "<GLOBAL-ID>")]
    public Guid Id { get; set; }

    [CommandOption("--folder <ID-OR-PATH>")]
    [Description("Target folder id, path, or name. Omit to move to Root.")]
    public string? Folder { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class GlobalsMoveVariableCommand : BaseCommand<GlobalsMoveVariableSettings>
{
    public GlobalsMoveVariableCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalsMoveVariableSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var folderId = await FolderResolver.ResolveAsync(api, settings.Folder, ct) ?? GlobalVariableFolderIds.Root;
        await api.MoveGlobalVariableToFolderAsync(settings.Id, folderId, ct);
        writer.Success("Variable verschoben.");
        return ExitCodes.Success;
    }
}

/// <summary>Fixed, well-known folder id(s) mirrored from the server (not randomly generated) — currently just Root = …0002.</summary>
internal static class GlobalVariableFolderIds
{
    public static readonly Guid Root = Guid.Parse("00000000-0000-0000-0000-000000000002");
}

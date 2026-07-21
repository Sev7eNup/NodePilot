using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.SharedFolders;

// Branch: `np shared-folder ...` — wraps `/api/shared-workflow-folders/*`.
// Naming chosen to mirror the docs (docs/rbac-shared-folders.md).

// ---- list -------------------------------------------------------------------

[SupportedOSPlatform("windows")]
public sealed class SharedFolderListCommand : BaseCommand<GlobalSettings>
{
    public SharedFolderListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListSharedFoldersAsync(ct);
        writer.WriteData(rows, (console, list) =>
        {
            var t = new Table().Border(TableBorder.Rounded)
                .AddColumn("Id").AddColumn("Path").AddColumn("Depth").AddColumn("Workflows").AddColumn("Caps");
            foreach (var f in list.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                var caps = $"{(f.Capabilities.CanRead ? "R" : "-")}{(f.Capabilities.CanRun ? "X" : "-")}{(f.Capabilities.CanEdit ? "W" : "-")}{(f.Capabilities.CanAdmin ? "A" : "-")}";
                t.AddRow(
                    f.Id.ToString()[..8],
                    Markup.Escape(f.Path),
                    f.Depth.ToString(),
                    f.WorkflowCount.ToString(),
                    Markup.Escape(caps));
            }
            console.Write(t);
        });
        return ExitCodes.Success;
    }
}

// ---- create -----------------------------------------------------------------

public sealed class SharedFolderCreateSettings : GlobalSettings
{
    [CommandOption("--name <NAME>")] [Description("Folder name (max 120 chars).")] public string? Name { get; set; }
    [CommandOption("--parent <GUID>")] [Description("Parent folder id (default: Root).")] public Guid? Parent { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SharedFolderCreateCommand : BaseCommand<SharedFolderCreateSettings>
{
    public SharedFolderCreateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SharedFolderCreateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Name)) { writer.Error("--name ist Pflicht."); return ExitCodes.Error; }
        var api = ClientFactory.Create(session);
        var f = await api.CreateSharedFolderAsync(new CreateSharedFolderRequest(settings.Parent, settings.Name), ct);
        writer.Success($"Shared folder angelegt: [bold]{Markup.Escape(f.Path)}[/] ({f.Id}).");
        return ExitCodes.Success;
    }
}

// ---- rename -----------------------------------------------------------------

public sealed class SharedFolderRenameSettings : GlobalSettings
{
    [CommandArgument(0, "<FOLDER-ID>")] public Guid Id { get; set; }
    [CommandOption("--name <NAME>")] [Description("New folder name.")] public string? Name { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SharedFolderRenameCommand : BaseCommand<SharedFolderRenameSettings>
{
    public SharedFolderRenameCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SharedFolderRenameSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Name)) { writer.Error("--name ist Pflicht."); return ExitCodes.Error; }
        var api = ClientFactory.Create(session);
        await api.RenameSharedFolderAsync(settings.Id, new UpdateSharedFolderRequest(settings.Name), ct);
        writer.Success("Folder umbenannt.");
        return ExitCodes.Success;
    }
}

// ---- move (folder into a different parent) ---------------------------------

public sealed class SharedFolderMoveSettings : GlobalSettings
{
    [CommandArgument(0, "<FOLDER-ID>")] public Guid Id { get; set; }
    [CommandOption("--parent <GUID>")] [Description("New parent folder id.")] public Guid? Parent { get; set; }
    [CommandOption("--to-root")] [Description("Move to the Root folder (overrides --parent).")] public bool ToRoot { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SharedFolderMoveCommand : BaseCommand<SharedFolderMoveSettings>
{
    public SharedFolderMoveCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SharedFolderMoveSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!settings.ToRoot && settings.Parent is null) { writer.Error("Entweder --parent <GUID> oder --to-root angeben."); return ExitCodes.Error; }
        var api = ClientFactory.Create(session);
        var req = new MoveSharedFolderRequest(settings.ToRoot ? null : settings.Parent);
        await api.MoveSharedFolderAsync(settings.Id, req, ct);
        writer.Success("Folder verschoben.");
        return ExitCodes.Success;
    }
}

// ---- delete -----------------------------------------------------------------

public sealed class SharedFolderIdSettings : GlobalSettings
{
    [CommandArgument(0, "<FOLDER-ID>")] public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SharedFolderDeleteCommand : BaseCommand<SharedFolderIdSettings>
{
    public SharedFolderDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SharedFolderIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"Shared folder [red]{settings.Id}[/] wirklich löschen? (muss leer sein — keine Workflows, keine Sub-Folders)", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }
        var api = ClientFactory.Create(session);
        await api.DeleteSharedFolderAsync(settings.Id, ct);
        writer.Success("Folder gelöscht.");
        return ExitCodes.Success;
    }
}

// ---- permissions list -------------------------------------------------------

public sealed class SharedFolderPermissionsListSettings : GlobalSettings
{
    [CommandArgument(0, "<FOLDER-ID>")] public Guid FolderId { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SharedFolderPermissionsListCommand : BaseCommand<SharedFolderPermissionsListSettings>
{
    public SharedFolderPermissionsListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SharedFolderPermissionsListSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListSharedFolderPermissionsAsync(settings.FolderId, ct);
        writer.WriteData(rows, (console, list) =>
        {
            var t = new Table().Border(TableBorder.Rounded)
                .AddColumn("Id").AddColumn("Type").AddColumn("Principal").AddColumn("Role").AddColumn("Granted");
            foreach (var p in list)
            {
                var who = p.PrincipalDisplayName is not null
                    ? $"{Markup.Escape(p.PrincipalDisplayName)} [grey]({Markup.Escape(p.PrincipalKey)})[/]"
                    : Markup.Escape(p.PrincipalKey);
                t.AddRow(p.Id.ToString()[..8], Markup.Escape(p.PrincipalType), who, Markup.Escape(p.Role), p.GrantedAt.ToLocalTime().ToString("u"));
            }
            console.Write(t);
        });
        return ExitCodes.Success;
    }
}

// ---- permissions grant ------------------------------------------------------

public sealed class SharedFolderGrantSettings : GlobalSettings
{
    [CommandArgument(0, "<FOLDER-ID>")] public Guid FolderId { get; set; }
    [CommandOption("--principal-type <TYPE>")]
    [Description("User | Group (V1; Role is reserved).")]
    public string? PrincipalType { get; set; }
    [CommandOption("--principal-key <KEY>")]
    [Description("User Guid (for User) or AD SID (for Group, e.g. S-1-5-21-...).")]
    public string? PrincipalKey { get; set; }
    [CommandOption("--role <ROLE>")]
    [Description("FolderViewer | FolderOperator | FolderEditor | FolderAdmin.")]
    public string? Role { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SharedFolderGrantCommand : BaseCommand<SharedFolderGrantSettings>
{
    public SharedFolderGrantCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SharedFolderGrantSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.PrincipalType)) { writer.Error("--principal-type ist Pflicht (User | Group)."); return ExitCodes.Error; }
        if (string.IsNullOrWhiteSpace(settings.PrincipalKey)) { writer.Error("--principal-key ist Pflicht."); return ExitCodes.Error; }
        if (string.IsNullOrWhiteSpace(settings.Role)) { writer.Error("--role ist Pflicht (FolderViewer | FolderOperator | FolderEditor | FolderAdmin)."); return ExitCodes.Error; }

        var api = ClientFactory.Create(session);
        var req = new GrantSharedFolderPermissionRequest(settings.PrincipalType, settings.PrincipalKey, settings.Role);
        var perm = await api.GrantSharedFolderPermissionAsync(settings.FolderId, req, ct);
        writer.Success($"Permission gesetzt: [bold]{Markup.Escape(perm.Role)}[/] für {Markup.Escape(perm.PrincipalKey)} (Id {perm.Id}).");
        return ExitCodes.Success;
    }
}

// ---- permissions revoke -----------------------------------------------------

public sealed class SharedFolderRevokeSettings : GlobalSettings
{
    [CommandArgument(0, "<FOLDER-ID>")] public Guid FolderId { get; set; }
    [CommandArgument(1, "<PERMISSION-ID>")] public Guid PermissionId { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SharedFolderRevokeCommand : BaseCommand<SharedFolderRevokeSettings>
{
    public SharedFolderRevokeCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SharedFolderRevokeSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        await api.RevokeSharedFolderPermissionAsync(settings.FolderId, settings.PermissionId, ct);
        writer.Success("Permission widerrufen.");
        return ExitCodes.Success;
    }
}

// ---- workflow move-folder (lives in shared-folder feature scope) -----------

public sealed class WorkflowMoveFolderSettings : GlobalSettings
{
    [CommandArgument(0, "<WORKFLOW-ID-OR-NAME>")] public string IdOrName { get; set; } = "";
    [CommandOption("--target-folder <GUID>")]
    [Description("Destination shared folder id. Use Root's folder id to move to Root.")]
    public Guid? TargetFolder { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowMoveFolderCommand : BaseCommand<WorkflowMoveFolderSettings>
{
    public WorkflowMoveFolderCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowMoveFolderSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (settings.TargetFolder is null) { writer.Error("--target-folder <GUID> ist Pflicht."); return ExitCodes.Error; }
        var api = ClientFactory.Create(session);
        var w = await NodePilot.Cli.Commands.WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        await api.MoveWorkflowToFolderAsync(w.Id, new MoveWorkflowToFolderRequest(settings.TargetFolder.Value), ct);
        writer.Success($"Workflow [bold]{Markup.Escape(w.Name)}[/] verschoben.");
        return ExitCodes.Success;
    }
}

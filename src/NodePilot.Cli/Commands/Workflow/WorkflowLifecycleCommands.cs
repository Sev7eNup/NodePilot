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

[SupportedOSPlatform("windows")]
public sealed class WorkflowLockCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowLockCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var locked = await api.LockWorkflowAsync(w.Id, ct);
        writer.Success($"Workflow [bold]{Markup.Escape(locked.Name)}[/] gelockt — Bearbeiten freigegeben.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowUnlockCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowUnlockCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var unlocked = await api.UnlockWorkflowAsync(w.Id, ct);
        writer.Success($"Lock entfernt. Workflow bleibt {(unlocked.IsEnabled ? "[green]enabled[/]" : "[grey]disabled[/]")}.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowEnableCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowEnableCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        await api.EnableWorkflowAsync(w.Id, ct);
        writer.Success($"Workflow [bold]{Markup.Escape(w.Name)}[/] enabled.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowDisableCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowDisableCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        await api.DisableWorkflowAsync(w.Id, ct);
        writer.Success($"Workflow [bold]{Markup.Escape(w.Name)}[/] disabled.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowCancelAllCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowCancelAllCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var result = await api.CancelAllAsync(w.Id, ct);
        writer.Success($"Cancelled {result.Signalled} von {result.Total} laufenden Executions.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowDuplicateCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowDuplicateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var src = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var copy = await api.DuplicateWorkflowAsync(src.Id, ct);
        writer.Success($"Dupliziert → [bold]{Markup.Escape(copy.Name)}[/] ({copy.Id})");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowDeleteCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);

        // Destructive — confirm unless stdin is non-interactive (script context).
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"Workflow [red]{Markup.Escape(w.Name)}[/] wirklich löschen?", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }

        await api.DeleteWorkflowAsync(w.Id, ct);
        writer.Success($"Workflow gelöscht.");
        return ExitCodes.Success;
    }
}

public sealed class WorkflowPublishSettings : WorkflowGetSettings
{
    [CommandOption("-f|--file <PATH>")]
    [Description("Path to the workflow definition JSON to publish.")]
    public string File { get; set; } = "";

    [CommandOption("--name <NAME>")]
    [Description("Override the workflow name (default: keep current).")]
    public string? Name { get; set; }

    [CommandOption("--description <DESC>")]
    [Description("Override the workflow description.")]
    public string? Description { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowPublishCommand : BaseCommand<WorkflowPublishSettings>
{
    public WorkflowPublishCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowPublishSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.File) || !File.Exists(settings.File))
        {
            writer.Error($"Datei nicht gefunden: {settings.File}");
            return ExitCodes.Error;
        }

        var json = await File.ReadAllTextAsync(settings.File, ct);
        try { using var _doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            writer.Error($"Definition ist kein gültiges JSON: {ex.Message}");
            return ExitCodes.Error;
        }

        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var req = new PublishWorkflowRequest(settings.Name ?? w.Name, settings.Description ?? w.Description, json);
        var published = await api.PublishWorkflowAsync(w.Id, req, ct);
        writer.Success($"Workflow [bold]{Markup.Escape(published.Name)}[/] published (Version {published.Version}, Enabled).");
        return ExitCodes.Success;
    }
}

public sealed class WorkflowRollbackSettings : WorkflowGetSettings
{
    [CommandArgument(1, "<VERSION>")]
    [Description("Target version number to roll back to.")]
    public int Version { get; set; }

    [CommandOption("--reason <TEXT>")]
    [Description("Optional reason recorded in the audit log.")]
    public string? Reason { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowRollbackCommand : BaseCommand<WorkflowRollbackSettings>
{
    public WorkflowRollbackCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowRollbackSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var rolled = await api.RollbackAsync(w.Id, settings.Version, new RollbackRequest(settings.Reason), ct);
        writer.Success($"Rollback auf Version {settings.Version} → neue Version {rolled.Version}.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowForceUnlockCommand : BaseCommand<WorkflowGetSettings>
{
    public WorkflowForceUnlockCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);

        // Admin-only and disruptive: prompt unless stdin is non-interactive (so scripts work).
        if (!Console.IsInputRedirected)
        {
            var owner = w.CheckedOutByUserName ?? "?";
            var ok = AnsiConsole.Confirm(
                $"Workflow [yellow]{Markup.Escape(w.Name)}[/] ist gelockt von [yellow]{Markup.Escape(owner)}[/]. Wirklich force-unlocken?",
                defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }

        var unlocked = await api.ForceUnlockWorkflowAsync(w.Id, ct);
        writer.Success($"Lock von [yellow]{Markup.Escape(w.CheckedOutByUserName ?? "?")}[/] gebrochen. Workflow ist [grey]disabled[/] (Admin re-publish nötig).");
        writer.WriteData(unlocked, (console, value) => Renderers.WorkflowDetail(console, value));
        return ExitCodes.Success;
    }
}

public sealed class WorkflowVersionGetSettings : WorkflowGetSettings
{
    [CommandArgument(1, "<VERSION>")]
    [Description("Version number to fetch.")]
    public int Version { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowVersionGetCommand : BaseCommand<WorkflowVersionGetSettings>
{
    public WorkflowVersionGetCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowVersionGetSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        var detail = await api.GetVersionAsync(w.Id, settings.Version, ct);
        writer.WriteData(detail, (console, value) => Renderers.WorkflowVersionDetail(console, value));
        return ExitCodes.Success;
    }
}

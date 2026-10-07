using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
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
        writer.Success($"Workflow [bold]{Markup.Escape(locked.Name)}[/] locked. You can edit it now.");
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
        writer.Success($"Lock released. Workflow stays {(unlocked.IsEnabled ? "[green]enabled[/]" : "[grey]disabled[/]")}.");
        return ExitCodes.Success;
    }
}

public sealed class WorkflowConcurrencyLimitSettings : WorkflowGetSettings
{
    [CommandOption("-m|--max <COUNT>")]
    [Description("Maximum concurrent executions (1-1000). Omit or pass 'none' for unlimited.")]
    public string? Max { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowConcurrencyLimitCommand : BaseCommand<WorkflowConcurrencyLimitSettings>
{
    public WorkflowConcurrencyLimitCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, WorkflowConcurrencyLimitSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        int? limit = null;
        if (!string.IsNullOrWhiteSpace(settings.Max)
            && !settings.Max.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(settings.Max, out var parsed))
            {
                writer.Error($"'{settings.Max}' is not a number. Pass a count or 'none' for unlimited.");
                return ExitCodes.Error;
            }
            limit = parsed;
        }

        var api = ClientFactory.Create(session);
        var w = await WorkflowResolver.ResolveAsync(api, settings.IdOrName, ct);
        await api.SetWorkflowConcurrencyLimitAsync(w.Id, limit, ct);
        writer.Success(limit is { } value
            ? $"Workflow [bold]{Markup.Escape(w.Name)}[/] limited to {value} concurrent execution(s); further runs queue."
            : $"Concurrency limit removed from [bold]{Markup.Escape(w.Name)}[/] — unlimited.");
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
        writer.Success($"Cancelled {result.Signalled} of {result.Total} running execution(s).");
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
        writer.Success($"Duplicated → [bold]{Markup.Escape(copy.Name)}[/] ({copy.Id})");
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
            var ok = await AnsiConsole.ConfirmAsync($"Delete workflow [red]{Markup.Escape(w.Name)}[/]?", defaultValue: false);
            if (!ok) { writer.Info("Aborted."); return ExitCodes.Success; }
        }

        await api.DeleteWorkflowAsync(w.Id, ct);
        writer.Success("Workflow deleted.");
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
            writer.Error($"File not found: {settings.File}");
            return ExitCodes.Error;
        }

        var json = await File.ReadAllTextAsync(settings.File, ct);
        try { using var _doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            writer.Error($"The definition is not valid JSON: {ex.Message}");
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
        writer.Success($"Rolled back to version {settings.Version} → new version {rolled.Version}.");
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
            var ok = await AnsiConsole.ConfirmAsync(
                $"Workflow [yellow]{Markup.Escape(w.Name)}[/] is locked by [yellow]{Markup.Escape(owner)}[/]. Force-unlock it?",
                defaultValue: false);
            if (!ok) { writer.Info("Aborted."); return ExitCodes.Success; }
        }

        var unlocked = await api.ForceUnlockWorkflowAsync(w.Id, ct);
        writer.Success($"Broke the lock held by [yellow]{Markup.Escape(w.CheckedOutByUserName ?? "?")}[/]. Workflow is [grey]disabled[/] (an Admin has to re-publish it).");
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

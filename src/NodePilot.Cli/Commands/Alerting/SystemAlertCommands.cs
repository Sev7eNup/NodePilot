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

namespace NodePilot.Cli.Commands.Alerting;

// `np system-alert ...` — CLI commands for system-alert policies: built-in checks on
// infrastructure/service health (e.g. backlog, stuck executions, unreachable machines),
// kept as a separate feature from the user-defined event rules under `np alerting`
// (see ADR 0008 for the reasoning). Mirrors the custom-alerting command shape. The
// complex condition/params payload for create/update is passed as a
// SaveSystemAlertPolicyRequest JSON file (--file) rather than a wide option surface.

[SupportedOSPlatform("windows")]
public sealed class SystemAlertCatalogCommand : BaseCommand<GlobalSettings>
{
    public SystemAlertCatalogCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var catalog = await api.GetSystemAlertCatalogAsync(ct);
        writer.WriteData(catalog.Sources, (console, list) => Renderers.SystemAlertSources(console, list));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemAlertListCommand : BaseCommand<GlobalSettings>
{
    public SystemAlertListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListSystemAlertPoliciesAsync(ct);
        writer.WriteData(rows, (console, list) => Renderers.SystemAlertPolicies(console, list));
        return ExitCodes.Success;
    }
}

public sealed class SystemAlertIdSettings : GlobalSettings
{
    [CommandArgument(0, "<POLICY-ID>")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SystemAlertGetCommand : BaseCommand<SystemAlertIdSettings>
{
    public SystemAlertGetCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SystemAlertIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var p = await api.GetSystemAlertPolicyAsync(settings.Id, ct);
        writer.WriteData(new[] { p }, (console, list) => Renderers.SystemAlertPolicies(console, list));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemAlertEnableCommand : BaseCommand<SystemAlertIdSettings>
{
    public SystemAlertEnableCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SystemAlertIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        await api.EnableSystemAlertPolicyAsync(settings.Id, ct);
        writer.Success("System-alert policy aktiviert.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemAlertDisableCommand : BaseCommand<SystemAlertIdSettings>
{
    public SystemAlertDisableCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SystemAlertIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        await api.DisableSystemAlertPolicyAsync(settings.Id, ct);
        writer.Success("System-alert policy deaktiviert.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemAlertDeleteCommand : BaseCommand<SystemAlertIdSettings>
{
    public SystemAlertDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SystemAlertIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"System-alert policy [red]{settings.Id}[/] wirklich löschen?", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }
        var api = ClientFactory.Create(session);
        await api.DeleteSystemAlertPolicyAsync(settings.Id, ct);
        writer.Success("System-alert policy gelöscht.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemAlertTestFireCommand : BaseCommand<SystemAlertIdSettings>
{
    public SystemAlertTestFireCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SystemAlertIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var result = await api.TestFireSystemAlertPolicyAsync(settings.Id, ct);
        writer.WriteData(result.Results, (console, list) =>
        {
            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Channel").AddColumn("Target").AddColumn("Result").AddColumn("Error");
            foreach (var r in list)
                table.AddRow(
                    Markup.Escape(r.Channel), Markup.Escape(r.Target),
                    r.Success ? "[green]ok[/]" : "[red]failed[/]",
                    Markup.Escape(r.Error ?? "-"));
            console.Write(table);
        });
        return result.AllSucceeded ? ExitCodes.Success : ExitCodes.Error;
    }
}

// Not sealed — SystemAlertUpdateSettings adds the id arg (mirrors AlertingWriteSettings).
public class SystemAlertCreateSettings : GlobalSettings
{
    [CommandOption("-f|--file <PATH>")]
    [Description("Path to a SaveSystemAlertPolicyRequest JSON file (camelCase).")]
    public string File { get; set; } = "";
}

[SupportedOSPlatform("windows")]
public sealed class SystemAlertCreateCommand : BaseCommand<SystemAlertCreateSettings>
{
    public SystemAlertCreateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SystemAlertCreateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var (req, error) = await SystemAlertCommandHelpers.ReadRequestAsync(settings.File, ct);
        if (req is null) { writer.Error(error!); return ExitCodes.Error; }

        var api = ClientFactory.Create(session);
        var created = await api.CreateSystemAlertPolicyAsync(req, ct);
        writer.Success($"System-alert policy angelegt: [bold]{Markup.Escape(created.Name)}[/].");
        return ExitCodes.Success;
    }
}

public sealed class SystemAlertUpdateSettings : SystemAlertCreateSettings
{
    [CommandArgument(0, "<POLICY-ID>")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class SystemAlertUpdateCommand : BaseCommand<SystemAlertUpdateSettings>
{
    public SystemAlertUpdateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, SystemAlertUpdateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var (req, error) = await SystemAlertCommandHelpers.ReadRequestAsync(settings.File, ct);
        if (req is null) { writer.Error(error!); return ExitCodes.Error; }

        var api = ClientFactory.Create(session);
        await api.UpdateSystemAlertPolicyAsync(settings.Id, req, ct);
        writer.Success($"System-alert policy [bold]{Markup.Escape(req.Name)}[/] aktualisiert.");
        return ExitCodes.Success;
    }
}

internal static class SystemAlertCommandHelpers
{
    // Reads + deserializes a SaveSystemAlertPolicyRequest from a JSON file. Returns (null, error)
    // on any problem so the caller surfaces a clean CLI error instead of throwing.
    public static async Task<(SaveSystemAlertPolicyRequest? Req, string? Error)> ReadRequestAsync(string file, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            return (null, $"Datei nicht gefunden: {file}");

        var json = await File.ReadAllTextAsync(file, ct);
        try
        {
            var req = JsonSerializer.Deserialize<SaveSystemAlertPolicyRequest>(json, NodePilotApiClient.JsonOptions);
            if (req is null) return (null, "Datei enthält kein gültiges SaveSystemAlertPolicyRequest-JSON.");
            return (req, null);
        }
        catch (JsonException ex)
        {
            return (null, $"Datei ist kein gültiges JSON: {ex.Message}");
        }
    }
}

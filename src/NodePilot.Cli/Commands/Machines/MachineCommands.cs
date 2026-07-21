using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Machines;

public class MachineIdSettings : GlobalSettings
{
    [CommandArgument(0, "<MACHINE-ID>")]
    [Description("Machine GUID.")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class MachineListCommand : BaseCommand<GlobalSettings>
{
    public MachineListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListMachinesAsync(ct);
        writer.WriteData(rows, (console, list) => Renderers.Machines(console, list));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class MachineGetCommand : BaseCommand<MachineIdSettings>
{
    public MachineGetCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, MachineIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var m = await api.GetMachineAsync(settings.Id, ct);
        writer.WriteData(m, (console, value) => Renderers.MachineDetail(console, value));
        return ExitCodes.Success;
    }
}

public class MachineWriteSettings : GlobalSettings
{
    [CommandOption("--name <NAME>")]
    [Description("Display name (required on create).")]
    public string? Name { get; set; }

    [CommandOption("--hostname <HOST>")]
    [Description("Resolvable hostname (required on create).")]
    public string? Hostname { get; set; }

    [CommandOption("--port <PORT>")]
    [Description("WinRM port (default 5985 on create).")]
    public int? Port { get; set; }

    [CommandOption("--ssl")]
    [Description("Use SSL / port 5986.")]
    public bool? UseSsl { get; set; }

    [CommandOption("--credential <GUID>")]
    [Description("Default credential GUID for this machine.")]
    public Guid? CredentialId { get; set; }

    [CommandOption("--tags <TAGS>")]
    [Description("Free-form tag string (CSV by convention).")]
    public string? Tags { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class MachineCreateCommand : BaseCommand<MachineWriteSettings>
{
    public MachineCreateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, MachineWriteSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Name) || string.IsNullOrWhiteSpace(settings.Hostname))
        {
            writer.Error("--name und --hostname sind Pflicht.");
            return ExitCodes.Error;
        }

        var api = ClientFactory.Create(session);
        var req = new CreateMachineRequest(
            settings.Name, settings.Hostname,
            settings.Port ?? 5985, settings.UseSsl ?? false,
            settings.CredentialId, settings.Tags);
        var created = await api.CreateMachineAsync(req, ct);
        writer.Success($"Machine angelegt: [bold]{Markup.Escape(created.Name)}[/] ({created.Id}).");
        writer.WriteData(created, (console, value) => Renderers.MachineDetail(console, value));
        return ExitCodes.Success;
    }
}

public sealed class MachineUpdateSettings : MachineIdSettings
{
    [CommandOption("--name <NAME>")] public string? Name { get; set; }
    [CommandOption("--hostname <HOST>")] public string? Hostname { get; set; }
    [CommandOption("--port <PORT>")] public int? Port { get; set; }
    [CommandOption("--ssl")] public bool? UseSsl { get; set; }
    [CommandOption("--credential <GUID>")] public Guid? CredentialId { get; set; }
    [CommandOption("--tags <TAGS>")] public string? Tags { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class MachineUpdateCommand : BaseCommand<MachineUpdateSettings>
{
    public MachineUpdateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, MachineUpdateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var current = await api.GetMachineAsync(settings.Id, ct);

        // Patch semantics — server endpoint requires the full body (not PATCH), so merge the
        // partial CLI flags onto the current machine and resubmit.
        var req = new UpdateMachineRequest(
            settings.Name ?? current.Name,
            settings.Hostname ?? current.Hostname,
            settings.Port ?? current.WinRmPort,
            settings.UseSsl ?? current.UseSsl,
            settings.CredentialId ?? current.DefaultCredentialId,
            settings.Tags ?? current.Tags);
        await api.UpdateMachineAsync(settings.Id, req, ct);
        writer.Success($"Machine [bold]{Markup.Escape(req.Name)}[/] aktualisiert.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class MachineDeleteCommand : BaseCommand<MachineIdSettings>
{
    public MachineDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, MachineIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var m = await api.GetMachineAsync(settings.Id, ct);
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"Machine [red]{Markup.Escape(m.Name)}[/] ({m.Hostname}) wirklich löschen?", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }
        await api.DeleteMachineAsync(settings.Id, ct);
        writer.Success($"Machine gelöscht.");
        return ExitCodes.Success;
    }
}

public sealed class MachineTestSettings : MachineIdSettings
{
    [CommandOption("--credential <GUID>")]
    [Description("Override the machine's default credential just for this test (does not persist).")]
    public Guid? CredentialId { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class MachineTestCommand : BaseCommand<MachineTestSettings>
{
    public MachineTestCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, MachineTestSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var req = settings.CredentialId.HasValue ? new TestConnectionRequest(settings.CredentialId) : null;
        var result = await api.TestMachineAsync(settings.Id, req, ct);
        writer.WriteData(result, (console, value) =>
        {
            console.MarkupLine(value.Success
                ? $"[green]✓ Erreichbar[/] (Credential: {Markup.Escape(value.CredentialUsed ?? "-")})"
                : $"[red]✗ Fehlgeschlagen[/] (Credential: {Markup.Escape(value.CredentialUsed ?? "-")})");
            if (!string.IsNullOrEmpty(value.ComputerName)) console.MarkupLine($"  ComputerName: {Markup.Escape(value.ComputerName)}");
            if (!string.IsNullOrEmpty(value.Error)) console.MarkupLine($"  [red]Error:[/] {Markup.Escape(value.Error)}");
        });
        return result.Success ? ExitCodes.Success : ExitCodes.Error;
    }
}

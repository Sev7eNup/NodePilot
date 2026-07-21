using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Secrets;

public sealed class SecretsReencryptSettings : GlobalSettings
{
    [CommandOption("--yes")]
    [Description("Skip the interactive confirmation prompt.")]
    public bool Yes { get; set; }
}

/// <summary>
/// Triggers the bulk re-encrypt sweep after rotating the AES-GCM master key or
/// migrating between secret protectors. Admin-only on the server. Returns exit code 0
/// on a clean sweep, 1 when the server reported partial success (some rows could
/// not be migrated — see the printed skip-detail list).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecretsReencryptCommand : BaseCommand<SecretsReencryptSettings>
{
    public SecretsReencryptCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }

    protected override async Task<int> RunAsync(CommandContext _, SecretsReencryptSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!settings.Yes && !Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm(
                "Re-encrypt sweep über alle Credentials + Global-Secrets ausführen?\n  " +
                "[grey](Empfohlen nur direkt nach AES-GCM-Key-Rotation oder Provider-Migration.)[/]",
                defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }

        var api = ClientFactory.Create(session);
        var result = await api.ReencryptSecretsAsync(ct);

        writer.WriteData(result, (console, value) =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("Credentials Rewritten", value.CredentialsRewritten.ToString());
            grid.AddRow("Credentials Skipped",
                value.CredentialsSkipped == 0 ? "0" : $"[yellow]{value.CredentialsSkipped}[/]");
            grid.AddRow("Global Secrets Rewritten", value.GlobalSecretsRewritten.ToString());
            grid.AddRow("Global Secrets Skipped",
                value.GlobalSecretsSkipped == 0 ? "0" : $"[yellow]{value.GlobalSecretsSkipped}[/]");
            grid.AddRow("Status", value.PartialSuccess
                ? "[yellow]partial — some rows need manual re-entry[/]"
                : "[green]clean[/]");
            console.Write(grid);

            if (value.CredentialSkipDetails.Count > 0)
            {
                console.WriteLine();
                var t = new Table().Title("Credential skips").Border(TableBorder.Rounded)
                    .AddColumn("Id").AddColumn("Name").AddColumn("Reason");
                foreach (var s in value.CredentialSkipDetails)
                    t.AddRow(s.Id.ToString()[..8], Markup.Escape(s.Name), Markup.Escape(s.Reason));
                console.Write(t);
            }
            if (value.GlobalSecretSkipDetails.Count > 0)
            {
                console.WriteLine();
                var t = new Table().Title("Global-secret skips").Border(TableBorder.Rounded)
                    .AddColumn("Id").AddColumn("Name").AddColumn("Reason");
                foreach (var s in value.GlobalSecretSkipDetails)
                    t.AddRow(s.Id.ToString()[..8], Markup.Escape(s.Name), Markup.Escape(s.Reason));
                console.Write(t);
            }
        });

        return result.PartialSuccess ? ExitCodes.Error : ExitCodes.Success;
    }
}

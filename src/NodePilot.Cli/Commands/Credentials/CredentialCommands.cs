using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Credentials;

public class CredentialIdSettings : GlobalSettings
{
    [CommandArgument(0, "<CREDENTIAL-ID>")]
    [Description("Credential GUID.")]
    public Guid Id { get; set; }
}

internal static class CredentialExpiry
{
    /// <summary>
    /// A bare date like "2026-12-31" parses with Kind=Unspecified; ToUniversalTime would
    /// treat that as LOCAL and shift it across midnight. Expiry dates are calendar dates —
    /// pin Unspecified to UTC, convert only genuinely offset-carrying inputs.
    /// </summary>
    public static DateTime? AsUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        { } v => v.ToUniversalTime(),
    };
}

[SupportedOSPlatform("windows")]
public sealed class CredentialListCommand : BaseCommand<GlobalSettings>
{
    public CredentialListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListCredentialsAsync(ct);
        writer.WriteData(rows, (console, list) => Renderers.Credentials(console, list));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class CredentialGetCommand : BaseCommand<CredentialIdSettings>
{
    public CredentialGetCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, CredentialIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var c = await api.GetCredentialAsync(settings.Id, ct);
        writer.WriteData(new[] { c }, (console, list) => Renderers.Credentials(console, list));
        return ExitCodes.Success;
    }
}

public sealed class CredentialCreateSettings : GlobalSettings
{
    [CommandOption("--name <NAME>")] public string? Name { get; set; }
    [CommandOption("--username <USER>")] public string? Username { get; set; }
    [CommandOption("--password <PW>")]
    [Description("Service-account password (min 8 chars). Prefer --password-stdin in scripts.")]
    public string? Password { get; set; }
    [CommandOption("--password-stdin")]
    [Description("Read the password from stdin (recommended for scripts so it doesn't leak via process listing).")]
    public bool PasswordStdin { get; set; }
    [CommandOption("--domain <DOMAIN>")] public string? Domain { get; set; }
    [CommandOption("--expires <ISO-DATE>")]
    [Description("Optional account-expiry timestamp (ISO 8601, e.g. 2026-12-31). Feeds the CredentialExpiring alert signal.")]
    public DateTime? Expires { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class CredentialCreateCommand : BaseCommand<CredentialCreateSettings>
{
    public CredentialCreateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, CredentialCreateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Name) || string.IsNullOrWhiteSpace(settings.Username))
        {
            writer.Error("--name und --username sind Pflicht.");
            return ExitCodes.Error;
        }

        var pw = settings.Password;
        if (settings.PasswordStdin)
            pw = (await Console.In.ReadToEndAsync(ct)).TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(pw))
        {
            writer.Error("Passwort fehlt — entweder --password oder --password-stdin.");
            return ExitCodes.Error;
        }

        var api = ClientFactory.Create(session);
        var created = await api.CreateCredentialAsync(new CreateCredentialRequest(settings.Name, settings.Username, pw, settings.Domain, CredentialExpiry.AsUtc(settings.Expires)), ct);
        writer.Success($"Credential angelegt: [bold]{Markup.Escape(created.Name)}[/] ({created.Id}).");
        return ExitCodes.Success;
    }
}

public sealed class CredentialUpdateSettings : CredentialIdSettings
{
    [CommandOption("--name <NAME>")] public string? Name { get; set; }
    [CommandOption("--username <USER>")] public string? Username { get; set; }
    [CommandOption("--password <PW>")] public string? Password { get; set; }
    [CommandOption("--password-stdin")] public bool PasswordStdin { get; set; }
    [CommandOption("--domain <DOMAIN>")] public string? Domain { get; set; }
    [CommandOption("--expires <ISO-DATE>")]
    [Description("Set/replace the account-expiry timestamp (ISO 8601).")]
    public DateTime? Expires { get; set; }
    [CommandOption("--no-expires")]
    [Description("Clear a previously set expiry timestamp.")]
    public bool NoExpires { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class CredentialUpdateCommand : BaseCommand<CredentialUpdateSettings>
{
    public CredentialUpdateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, CredentialUpdateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (settings.Expires is not null && settings.NoExpires)
        {
            // Silently letting one win would discard the other flag's intent — templated
            // automation that appends both conditionally must fail loudly instead.
            writer.Error("--expires und --no-expires schließen sich aus.");
            return ExitCodes.Error;
        }

        var api = ClientFactory.Create(session);
        var current = await api.GetCredentialAsync(settings.Id, ct);

        var pw = settings.Password;
        if (settings.PasswordStdin)
            pw = (await Console.In.ReadToEndAsync(ct)).TrimEnd('\r', '\n');

        var expires = settings.NoExpires ? null : (CredentialExpiry.AsUtc(settings.Expires) ?? current.ExpiresAt);
        var req = new UpdateCredentialRequest(
            settings.Name ?? current.Name,
            settings.Username ?? current.Username,
            // null/empty means "keep" on the server side; we don't read the stored password
            // back so omitted-here = unchanged-there.
            string.IsNullOrEmpty(pw) ? null : pw,
            settings.Domain ?? current.Domain,
            expires);
        await api.UpdateCredentialAsync(settings.Id, req, ct);
        writer.Success($"Credential [bold]{Markup.Escape(req.Name)}[/] aktualisiert" + (string.IsNullOrEmpty(pw) ? " (Passwort unverändert)." : " (Passwort rotiert)."));
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class CredentialDeleteCommand : BaseCommand<CredentialIdSettings>
{
    public CredentialDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, CredentialIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var c = await api.GetCredentialAsync(settings.Id, ct);
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"Credential [red]{Markup.Escape(c.Name)}[/] ({Markup.Escape(c.Username)}) wirklich löschen?", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }
        await api.DeleteCredentialAsync(settings.Id, ct);
        writer.Success("Credential gelöscht.");
        return ExitCodes.Success;
    }
}

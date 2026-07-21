using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Users;

public class UserIdSettings : GlobalSettings
{
    [CommandArgument(0, "<USER-ID>")]
    [Description("User GUID.")]
    public Guid Id { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class UserListCommand : BaseCommand<GlobalSettings>
{
    public UserListCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var api = ClientFactory.Create(session);
        var rows = await api.ListUsersAsync(ct);
        writer.WriteData(rows, (console, list) => Renderers.Users(console, list));
        return ExitCodes.Success;
    }
}

public sealed class UserCreateSettings : GlobalSettings
{
    [CommandOption("--username <NAME>")] public string? Username { get; set; }
    [CommandOption("--password <PW>")]
    [Description("New password (subject to server password policy). Prefer --password-stdin.")]
    public string? Password { get; set; }
    [CommandOption("--password-stdin")] public bool PasswordStdin { get; set; }
    [CommandOption("--role <ROLE>")]
    [Description("Admin | Operator | Viewer (case-insensitive).")]
    public string? Role { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class UserCreateCommand : BaseCommand<UserCreateSettings>
{
    public UserCreateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, UserCreateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Role))
        {
            writer.Error("--username und --role sind Pflicht.");
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
        var u = await api.CreateUserAsync(new CreateUserRequest(settings.Username, pw, settings.Role), ct);
        writer.Success($"User angelegt: [bold]{Markup.Escape(u.Username)}[/] ({u.Role}).");
        return ExitCodes.Success;
    }
}

public sealed class UserUpdateSettings : UserIdSettings
{
    [CommandOption("--role <ROLE>")] public string? Role { get; set; }
    [CommandOption("--active <BOOL>")]
    [Description("true | false — deactivating the last active admin is rejected by the server.")]
    public bool? Active { get; set; }
    [CommandOption("--password <PW>")] public string? Password { get; set; }
    [CommandOption("--password-stdin")] public bool PasswordStdin { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class UserUpdateCommand : BaseCommand<UserUpdateSettings>
{
    public UserUpdateCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, UserUpdateSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        var pw = settings.Password;
        if (settings.PasswordStdin)
            pw = (await Console.In.ReadToEndAsync(ct)).TrimEnd('\r', '\n');

        if (settings.Role is null && settings.Active is null && string.IsNullOrEmpty(pw))
        {
            writer.Error("Mindestens eine Änderung angeben (--role / --active / --password).");
            return ExitCodes.Error;
        }

        var api = ClientFactory.Create(session);
        await api.UpdateUserAsync(settings.Id, new UpdateUserRequest(settings.Role, settings.Active, string.IsNullOrEmpty(pw) ? null : pw), ct);
        writer.Success($"User {settings.Id} aktualisiert.");
        return ExitCodes.Success;
    }
}

[SupportedOSPlatform("windows")]
public sealed class UserDeleteCommand : BaseCommand<UserIdSettings>
{
    public UserDeleteCommand(SessionResolver s, ApiClientFactory f) : base(s, f) { }
    protected override async Task<int> RunAsync(CommandContext _, UserIdSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!Console.IsInputRedirected)
        {
            var ok = AnsiConsole.Confirm($"User [red]{settings.Id}[/] wirklich löschen?", defaultValue: false);
            if (!ok) { writer.Info("Abgebrochen."); return ExitCodes.Success; }
        }
        var api = ClientFactory.Create(session);
        await api.DeleteUserAsync(settings.Id, ct);
        writer.Success("User gelöscht.");
        return ExitCodes.Success;
    }
}

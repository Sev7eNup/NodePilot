using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Auth;

public sealed class LoginSettings : GlobalSettings
{
    [CommandOption("--username <NAME>")]
    [Description("Username (prompted interactively if omitted).")]
    public string? Username { get; set; }

    [CommandOption("--password <PASSWORD>")]
    [Description("Password literal — prefer --password-stdin in scripts.")]
    public string? Password { get; set; }

    [CommandOption("--password-stdin")]
    [Description("Read password from stdin (one line).")]
    public bool PasswordStdin { get; set; }

    [CommandOption("--setup-token <TOKEN>")]
    [Description("Bootstrap-only first-admin setup token (contents of admin-setup.token).")]
    public string? SetupToken { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class LoginCommand : AsyncCommand<LoginSettings>
{
    private readonly ConfigStore _config;
    private readonly TokenStore _tokens;
    private readonly ApiClientFactory _factory;

    public LoginCommand(ConfigStore config, TokenStore tokens, ApiClientFactory factory)
    {
        _config = config;
        _tokens = tokens;
        _factory = factory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings)
    {
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor);

        var cfg = _config.Load();
        var profile = _config.ResolveProfileName(settings.Profile, cfg);
        var server = _config.ResolveServer(settings.Server, profile, cfg);
        if (string.IsNullOrWhiteSpace(server))
        {
            writer.Error("Kein Server konfiguriert. `np config set server <URL>` oder --server angeben.");
            return ExitCodes.Error;
        }

        var username = settings.Username ?? AnsiConsole.Ask<string>("Username:");
        string password;
        if (settings.PasswordStdin)
            password = ((await Console.In.ReadLineAsync()) ?? "").Trim();
        else if (!string.IsNullOrEmpty(settings.Password))
            password = settings.Password;
        else
            password = AnsiConsole.Prompt(new TextPrompt<string>("Password:").Secret());

        try
        {
            var api = _factory.CreateAnonymous(server, settings.AllowInsecureLoopback);
            var response = await api.LoginAsync(new LoginRequest(username, password), settings.SetupToken, CancellationToken.None);

            // Persist server URL into the active profile so subsequent calls don't need --server.
            cfg.Profiles[profile] = new ProfileEntry { Server = server };
            if (string.IsNullOrWhiteSpace(cfg.DefaultProfile)) cfg.DefaultProfile = profile;
            _config.Save(cfg);

            _tokens.Save(profile, new StoredSession
            {
                Server = server,
                Token = response.Token,
                Username = response.Username,
                UserId = response.UserId,
                Role = response.Role,
                ExpiresAt = DateTime.UtcNow.AddHours(12),
            });

            writer.Success($"Eingeloggt als [bold]{response.Username}[/] ({response.Role}) → {server}");
            return ExitCodes.Success;
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            writer.Error("Login fehlgeschlagen: ungültige Credentials.");
            return ExitCodes.AuthRequired;
        }
        catch (ApiException ex)
        {
            writer.Error($"Login fehlgeschlagen: {ex.Message}");
            return ExitCodes.Error;
        }
        catch (HttpRequestException ex)
        {
            writer.Error($"Netzwerk-Fehler: {ex.Message}");
            return ExitCodes.Error;
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class LogoutCommand : BaseCommand<GlobalSettings>
{
    private readonly TokenStore _tokens;
    public LogoutCommand(SessionResolver sessions, ApiClientFactory factory, TokenStore tokens)
        : base(sessions, factory) => _tokens = tokens;

    protected override async Task<int> RunAsync(CommandContext context, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!session.HasSession)
        {
            writer.Info("Keine aktive Session zum Abmelden.");
            return ExitCodes.Success;
        }

        try
        {
            var api = ClientFactory.Create(session);
            await api.LogoutAsync(ct);
        }
        catch (ApiException) { /* server already invalidated → still wipe local */ }
        catch (HttpRequestException) { /* server unreachable → still wipe local */ }

        _tokens.Delete(session.Profile);
        writer.Success($"Abgemeldet (Profil '{session.Profile}').");
        return ExitCodes.Success;
    }
}

/// <summary>
/// Anonymous discovery — reports which login methods the server has enabled
/// (Local always; LDAP and Windows-SSO opt-in via Authentication:* config).
/// Bypasses the session-resolver because the endpoint is AllowAnonymous and
/// the user typically runs this BEFORE picking a login method.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AuthMethodsCommand : AsyncCommand<GlobalSettings>
{
    private readonly ConfigStore _config;
    private readonly ApiClientFactory _factory;

    public AuthMethodsCommand(ConfigStore config, ApiClientFactory factory)
    {
        _config = config;
        _factory = factory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings)
    {
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor || Console.IsOutputRedirected);

        var cfg = _config.Load();
        var profile = _config.ResolveProfileName(settings.Profile, cfg);
        var server = _config.ResolveServer(settings.Server, profile, cfg);
        if (string.IsNullOrWhiteSpace(server))
        {
            writer.Error("Kein Server konfiguriert. `np config set server <URL>` oder --server angeben.");
            return ExitCodes.Error;
        }

        try
        {
            var api = _factory.CreateAnonymous(server, settings.AllowInsecureLoopback);
            var methods = await api.GetAuthMethodsAsync(CancellationToken.None);
            writer.WriteData(methods, (console, value) =>
            {
                var grid = new Grid().AddColumn().AddColumn();
                grid.AddRow("Server", server);
                grid.AddRow("Local Username/Password", value.Local ? "[green]enabled[/]" : "[grey]disabled[/]");
                grid.AddRow("LDAP Simple-Bind", value.Ldap ? "[green]enabled[/]" : "[grey]disabled[/]");
                grid.AddRow("Windows Negotiate (SSO)", value.Windows ? "[green]enabled[/]" : "[grey]disabled[/]");
                if (value.Windows && !string.IsNullOrEmpty(value.WindowsEndpoint))
                    grid.AddRow("  Endpoint", Markup.Escape(value.WindowsEndpoint));
                console.Write(grid);
            });
            return ExitCodes.Success;
        }
        catch (ApiException ex)
        {
            writer.Error($"API-Fehler: {ex.Message}");
            return ExitCodes.Error;
        }
        catch (HttpRequestException ex)
        {
            writer.Error($"Network error: {ex.Message}");
            return ExitCodes.Error;
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WhoamiCommand : BaseCommand<GlobalSettings>
{
    public WhoamiCommand(SessionResolver sessions, ApiClientFactory factory) : base(sessions, factory) { }

    protected override async Task<int> RunAsync(CommandContext context, GlobalSettings settings, SessionContext session, OutputWriter writer, CancellationToken ct)
    {
        if (!session.HasSession)
        {
            writer.Error("Nicht angemeldet.");
            return ExitCodes.AuthRequired;
        }

        var api = ClientFactory.Create(session);
        var me = await api.MeAsync(ct);
        writer.WriteData(new
        {
            session.Profile,
            session.Server,
            me.Username,
            me.Role,
            UserId = me.Id,
            session.Session!.ExpiresAt,
        }, (console, value) =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("Profile", value.Profile);
            grid.AddRow("Server", value.Server ?? "-");
            grid.AddRow("Username", value.Username);
            grid.AddRow("Role", value.Role);
            grid.AddRow("UserId", value.UserId.ToString());
            grid.AddRow("ExpiresAt", value.ExpiresAt.ToLocalTime().ToString("u"));
            console.Write(grid);
        });
        return ExitCodes.Success;
    }
}

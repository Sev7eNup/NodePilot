using System.ComponentModel;
using System.Net;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using NodePilot.Core.Clients;
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

    [CommandOption("--windows")]
    [Description("Sign in with the current Windows identity (Kerberos/Negotiate) instead of a password.")]
    public bool Windows { get; set; }

    public override ValidationResult Validate()
    {
        if (!Windows) return ValidationResult.Success();
        // Negotiate authenticates the process' own identity; any password-side option would be
        // silently ignored, which is worse than refusing the combination.
        if (Username is not null || Password is not null || PasswordStdin || SetupToken is not null)
            return ValidationResult.Error(
                "--windows cannot be combined with --username, --password, --password-stdin or --setup-token.");
        return ValidationResult.Success();
    }
}

[SupportedOSPlatform("windows")]
public sealed class LoginCommand : AsyncCommand<LoginSettings>
{
    /// <summary>Session cookie set by the API (<c>AuthController.AuthCookieName</c>). The Windows
    /// login path returns the JWT here and nowhere else.</summary>
    private const string AuthCookieName = "np_auth";

    private readonly ConfigStore _config;
    private readonly TokenStore _tokens;
    private readonly ApiClientFactory _factory;

    public LoginCommand(ConfigStore config, TokenStore tokens, ApiClientFactory factory)
    {
        _config = config;
        _tokens = tokens;
        _factory = factory;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, LoginSettings settings, CancellationToken ct)
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

        var pin = _config.ResolveTlsThumbprint(settings.TlsThumbprint, profile, server, cfg);
        var tls = new ClientTlsOptions(pin.Value, ConfigStore.ResolveSkipTlsVerification(settings.InsecureTls));
        TlsNotices.WriteBefore(writer, tls, pin.IgnoredReason);
        // Only a pin the caller named on this command line is stored; an environment pin stays a
        // property of that shell.
        var pinToStore = string.IsNullOrWhiteSpace(settings.TlsThumbprint) ? null : pin.Value;

        if (settings.Windows)
            return await LoginWithWindowsIdentityAsync(settings, cfg, profile, server, tls, pinToStore, writer, ct);

        var username = settings.Username ?? await AnsiConsole.AskAsync<string>("Username:");
        string password;
        if (settings.PasswordStdin)
            password = ((await Console.In.ReadLineAsync()) ?? "").Trim();
        else if (!string.IsNullOrEmpty(settings.Password))
            password = settings.Password;
        else
            password = await AnsiConsole.PromptAsync(new TextPrompt<string>("Password:").Secret());

        try
        {
            PresentedCertificateInfo? presented = null;
            var api = _factory.CreateAnonymous(
                server, settings.AllowInsecureLoopback, tls, observation => presented = observation);
            var response = await api.LoginAsync(new LoginRequest(username, password), settings.SetupToken, ct);
            if (!ClientSessionSecurity.TryResolveExpiration(
                    response.Token, response.ExpiresAt, out var expiresAt)
                || expiresAt <= DateTimeOffset.UtcNow)
            {
                writer.Error("Login fehlgeschlagen: Serverantwort enthält keine gültige Token-Ablaufzeit.");
                return ExitCodes.Error;
            }

            PersistSession(cfg, profile, server, new StoredSession
            {
                Server = server,
                Token = response.Token,
                Username = response.Username,
                UserId = response.UserId,
                Role = response.Role,
                ExpiresAt = expiresAt,
            }, pinToStore);

            TlsNotices.WriteAfter(writer, presented);
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
            writer.Error($"Login fehlgeschlagen: {Markup.Escape(ex.Message)}");
            return ExitCodes.Error;
        }
        catch (HttpRequestException ex)
        {
            writer.ErrorBlock(NetworkErrorRenderer.Render(ex, server));
            return ExitCodes.Error;
        }
    }

    /// <summary>
    /// Signs in with the process' own Windows identity. The server answers this path with identity
    /// only and puts the JWT in the httpOnly <c>np_auth</c> cookie, so the token is read from the
    /// cookie jar rather than the body; the stored session is identical to the password path.
    /// </summary>
    private async Task<int> LoginWithWindowsIdentityAsync(
        LoginSettings settings,
        CliConfig cfg,
        string profile,
        string server,
        ClientTlsOptions tls,
        string? pinToStore,
        OutputWriter writer,
        CancellationToken ct)
    {
        try
        {
            // Ask before knocking: with Authentication:Windows:Enabled off, the endpoint's auth
            // scheme is not registered and ASP.NET answers 500 with an internal handler message.
            // The anonymous discovery endpoint gives a clean answer instead.
            PresentedCertificateInfo? presented = null;
            var methods = await _factory
                .CreateAnonymous(server, settings.AllowInsecureLoopback, tls, observation => presented = observation)
                .GetAuthMethodsAsync(ct);
            if (!methods.Windows)
            {
                writer.Error("Windows-Anmeldung nicht verfügbar: Der Server hat Authentication:Windows:Enabled nicht gesetzt.");
                return ExitCodes.AuthRequired;
            }

            var sso = _factory.CreateForWindowsSso(server, settings.AllowInsecureLoopback, tls);
            var identity = await sso.Api.WindowsLoginAsync(ct);
            var token = sso.Cookies.GetCookies(sso.Api.BaseAddress!)[AuthCookieName]?.Value;
            if (string.IsNullOrEmpty(token))
            {
                writer.Error("Windows-Anmeldung fehlgeschlagen: Der Server hat kein Sitzungs-Cookie gesetzt.");
                return ExitCodes.Error;
            }

            // The Windows path carries no expiry in the body; the JWT's own exp claim is the only
            // source. ClientSessionSecurity parses it without treating it as authorization.
            if (!ClientSessionSecurity.TryResolveExpiration(token, advertisedExpiration: null, out var expiresAt)
                || expiresAt <= DateTimeOffset.UtcNow)
            {
                writer.Error("Windows-Anmeldung fehlgeschlagen: Token ohne gültige Ablaufzeit.");
                return ExitCodes.Error;
            }

            PersistSession(cfg, profile, server, new StoredSession
            {
                Server = server,
                Token = token,
                Username = identity.Username,
                UserId = identity.UserId,
                Role = identity.Role,
                ExpiresAt = expiresAt,
            }, pinToStore);

            TlsNotices.WriteAfter(writer, presented);
            writer.Success(
                $"Per Windows-Anmeldung eingeloggt als [bold]{identity.Username}[/] ({identity.Role}) → {server}");
            return ExitCodes.Success;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            writer.Error("Windows-Anmeldung nicht verfügbar: Der Server hat Authentication:Windows:Enabled nicht gesetzt.");
            return ExitCodes.AuthRequired;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            writer.Error($"Windows-Anmeldung nicht konfiguriert: {Markup.Escape(ex.Message)}");
            return ExitCodes.AuthRequired;
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            // No Kerberos ticket, missing SPN, or the server refused an NTLM fallback. The server
            // message names which, so it is passed through verbatim.
            writer.Error($"Windows-Anmeldung abgelehnt: {Markup.Escape(ex.Message)}");
            return ExitCodes.AuthRequired;
        }
        catch (ApiException ex)
        {
            writer.Error($"Windows-Anmeldung fehlgeschlagen: {Markup.Escape(ex.Message)}");
            return ExitCodes.Error;
        }
        catch (HttpRequestException ex)
        {
            writer.ErrorBlock(NetworkErrorRenderer.Render(ex, server));
            return ExitCodes.Error;
        }
    }

    /// <summary>Writes the server URL into the active profile and stores the session.</summary>
    private void PersistSession(
        CliConfig cfg, string profile, string server, StoredSession session, string? pinToStore)
    {
        // Persist server URL into the active profile so subsequent calls don't need --server.
        // Read-modify-write: replacing the entry would drop a stored pin on every login.
        if (!cfg.Profiles.TryGetValue(profile, out var entry)) entry = new ProfileEntry();
        // A pin authenticates one certificate for one origin; pointing the profile at another
        // server drops it rather than letting it vouch for the new one.
        if (!ClientSessionSecurity.HasSameServerOrigin(entry.Server, server)) entry.TlsThumbprint = null;
        entry.Server = server;
        if (pinToStore is not null) entry.TlsThumbprint = pinToStore;
        cfg.Profiles[profile] = entry;
        if (string.IsNullOrWhiteSpace(cfg.DefaultProfile)) cfg.DefaultProfile = profile;
        _config.Save(cfg);
        _tokens.Save(profile, session);
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
        catch (ApiException) { /* server already invalidated; still wipe local */ }
        catch (HttpRequestException) { /* server unreachable; still wipe local */ }

        _tokens.Delete(session.Profile);
        writer.Success($"Abgemeldet (Profil '{session.Profile}').");
        return ExitCodes.Success;
    }
}

/// <summary>
/// Anonymous discovery — reports which login methods the server has enabled
/// (Local always; LDAP and Windows-SSO opt-in via Authentication:* config).
/// Bypasses the session-resolver because the endpoint is AllowAnonymous and
/// the user typically runs this before picking a login method.
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

    protected override async Task<int> ExecuteAsync(CommandContext context, GlobalSettings settings, CancellationToken ct)
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

        var pin = _config.ResolveTlsThumbprint(settings.TlsThumbprint, profile, server, cfg);
        var tls = new ClientTlsOptions(pin.Value, ConfigStore.ResolveSkipTlsVerification(settings.InsecureTls));
        TlsNotices.WriteBefore(writer, tls, pin.IgnoredReason);

        try
        {
            var api = _factory.CreateAnonymous(server, settings.AllowInsecureLoopback, tls);
            var methods = await api.GetAuthMethodsAsync(ct);
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
            writer.ErrorBlock(NetworkErrorRenderer.Render(ex, server));
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

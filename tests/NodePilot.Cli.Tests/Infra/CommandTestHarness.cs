using Microsoft.Extensions.DependencyInjection;
using NodePilot.Cli;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Settings;
using Spectre.Console.Cli;
using Spectre.Console.Testing;
using WireMock.Server;

namespace NodePilot.Cli.Tests.Infra;

/// <summary>
/// End-to-end harness for CLI commands. Spins up a fresh WireMock server, an isolated
/// %APPDATA%\NodePilot directory, and a Spectre <see cref="CommandAppTester"/> wired
/// with the same DI graph as production <c>Program.cs</c>. Tests run real commands and
/// assert on exit code + captured stdout — no mocks of internal command logic.
///
/// <para>The command graph itself is registered via <see cref="CommandRegistration.Register"/>
/// — the same method <c>Program.cs</c> calls — so any new <c>np</c> verb is reachable from
/// tests without a parallel registration to keep in sync.</para>
/// </summary>
public sealed class CommandTestHarness : IDisposable
{
    private readonly bool _autoAllowInsecure;
    public WireMockServer Server { get; }
    public string ConfigDir { get; }
    public ConfigStore Config { get; }
    public TokenStore Tokens { get; }
    public CommandAppTester App { get; }

    public CommandTestHarness(bool authenticated = true, bool autoAllowInsecure = true)
    {
        _autoAllowInsecure = autoAllowInsecure;
        Server = WireMockServer.Start();
        ConfigDir = Path.Combine(Path.GetTempPath(), "np-cmdtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ConfigDir);

        Config = new ConfigStore(ConfigDir);
        Tokens = new TokenStore(ConfigDir);

        var cfg = new CliConfig { DefaultProfile = "default" };
        cfg.Profiles["default"] = new ProfileEntry { Server = Server.Url };
        Config.Save(cfg);

        if (authenticated)
        {
            Tokens.Save("default", new StoredSession
            {
                Server = Server.Url!,
                Token = "test-jwt",
                Username = "tester",
                UserId = Guid.NewGuid(),
                Role = "Admin",
                ExpiresAt = DateTime.UtcNow.AddHours(12),
            });
        }

        var services = new ServiceCollection();
        services.AddSingleton(Config);
        services.AddSingleton(Tokens);
        services.AddSingleton<SessionResolver>();
        // Use the production DI graph. Run() passes the real explicit --allow-insecure flag
        // because WireMock listens on an ephemeral HTTP loopback port.
        services.AddSingleton<ApiClientFactory>();

        var registrar = new TypeRegistrar(services);
        App = new CommandAppTester(registrar);
        App.Configure(c =>
        {
            c.PropagateExceptions();
            CommandRegistration.Register(c);
        });
    }

    public void Dispose()
    {
        Server.Stop();
        Server.Dispose();
        try { Directory.Delete(ConfigDir, recursive: true); } catch { /* best-effort */ }
    }

    public RunResult Run(params string[] args)
    {
        // Inject -o json so the test reads predictable, parseable output rather than
        // ANSI tables that change shape with terminal width.
        var fullArgs = new List<string>(args);
        if (!args.Contains("-o") && !args.Contains("--output"))
        {
            fullArgs.Add("-o");
            fullArgs.Add("json");
        }
        if (_autoAllowInsecure && !args.Contains("--allow-insecure"))
            fullArgs.Add("--allow-insecure");

        // CommandAppTester captures Spectre's IAnsiConsole, but JSON/YAML output goes
        // through Console.Out directly (no Markup). OutputWriter ALSO writes its log-style
        // messages (.Info/.Warning/.Error/.Success) to a Spectre console that wraps
        // Console.Error. Capture both streams so tests can assert on either — exit codes
        // matter most, but command-side validation errors only show up on stderr.
        var origOut = Console.Out;
        var origErr = Console.Error;
        using var stdoutCapture = new StringWriter();
        using var stderrCapture = new StringWriter();
        Console.SetOut(stdoutCapture);
        Console.SetError(stderrCapture);
        CommandAppResult inner;
        try
        {
            inner = App.Run(fullArgs.ToArray());
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetError(origErr);
        }

        return new RunResult(
            inner.ExitCode,
            inner.Output + stdoutCapture.ToString(),
            stderrCapture.ToString());
    }
}

/// <summary>
/// Result of a harnessed command run. <see cref="Output"/> is the union of Spectre's
/// captured AnsiConsole output plus anything written to <see cref="Console.Out"/>
/// (JSON/YAML payloads). <see cref="StdErr"/> is the captured <see cref="Console.Error"/>
/// stream — where OutputWriter sends its log-style helpers (Info/Warning/Error/Success).
/// </summary>
public sealed record RunResult(int ExitCode, string Output, string StdErr = "")
{
    /// <summary>Convenience for older tests: union of stdout + stderr.</summary>
    public string AnyOutput => Output + StdErr;
}

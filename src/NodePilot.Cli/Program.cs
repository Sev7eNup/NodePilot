using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Cli;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Settings;
using Spectre.Console.Cli;

[assembly: SupportedOSPlatform("windows")]

var services = new ServiceCollection();
services.AddSingleton<ConfigStore>();
services.AddSingleton<TokenStore>();
services.AddSingleton<SessionResolver>();
services.AddSingleton<ApiClientFactory>();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("np");
    // Read the version from the assembly rather than a literal, since Directory.Build.props
    // is the single source of the product version. Strip the "+<commit>" source-revision
    // suffix the SDK appends to the informational version.
    config.SetApplicationVersion(CliVersion.Current);
    config.UseStrictParsing();
    config.PropagateExceptions();

    // Command tree lives in CommandRegistration so the test harness can reuse the same
    // graph. Anything registered here must flow through that method, or tests report a
    // new command as covered when really only its API client is exercised.
    CommandRegistration.Register(config);
});

try
{
    return await app.RunAsync(args);
}
catch (CommandRuntimeException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return ExitCodes.Error;
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"Error: {ex.Message}");
    return ExitCodes.Error;
}

// Coverage: process entry point — Spectre command-app wiring plus the top-level try/catch.
// The commands it registers are covered individually in NodePilot.Cli.Tests.
[ExcludeFromCodeCoverage(Justification = "Process entry point.")]
internal partial class Program;

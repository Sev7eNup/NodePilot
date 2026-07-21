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
    config.SetApplicationVersion("1.0.0");
    config.UseStrictParsing();
    config.PropagateExceptions();

    // Command tree lives in CommandRegistration so the test harness can re-use the same
    // graph. Anything we register here MUST flow through that method — otherwise tests
    // think the new command is covered when really only its API-client is exercised.
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

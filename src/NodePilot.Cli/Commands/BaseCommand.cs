using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands;

/// <summary>
/// Base for every command that talks to the API. Handles session resolution,
/// HttpClient construction, output writer wiring and a top-level try/catch that
/// turns API errors into the right exit code without dumping a stack trace.
/// </summary>
[SupportedOSPlatform("windows")]
public abstract class BaseCommand<TSettings> : AsyncCommand<TSettings>
    where TSettings : GlobalSettings
{
    protected SessionResolver Sessions { get; }
    protected ApiClientFactory ClientFactory { get; }

    protected BaseCommand(SessionResolver sessions, ApiClientFactory clientFactory)
    {
        Sessions = sessions;
        ClientFactory = clientFactory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, TSettings settings)
    {
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor || Console.IsOutputRedirected);
        try
        {
            var session = Sessions.Resolve(settings);
            return await RunAsync(context, settings, session, writer, CancellationToken.None);
        }
        catch (NotAuthenticatedException ex)
        {
            writer.Error(ex.Message);
            return ExitCodes.AuthRequired;
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            writer.Error("Session abgelaufen. Bitte `np auth login` erneut ausführen.");
            return ExitCodes.AuthRequired;
        }
        catch (ApiException ex) when (ex.IsForbidden)
        {
            writer.Error($"Zugriff verweigert: {ex.Detail ?? ex.Title ?? "die aktuelle Rolle erlaubt diese Aktion nicht."}");
            return ExitCodes.PermissionDenied;
        }
        catch (ApiException ex) when (ex.IsLocked)
        {
            writer.Error($"Workflow ist gelockt ({ex.Detail ?? "von einem anderen User ausgecheckt"}). Nutze `np workflow lock` (oder `force-unlock` als Admin).");
            return ExitCodes.Error;
        }
        catch (ApiException ex)
        {
            writer.Error($"API-Fehler: {ex.Message}");
            return ExitCodes.Error;
        }
        catch (HttpRequestException ex)
        {
            writer.Error($"Netzwerk-Fehler: {ex.Message}");
            return ExitCodes.Error;
        }
        catch (InvalidOperationException ex)
        {
            writer.Error(ex.Message);
            return ExitCodes.Error;
        }
    }

    protected abstract Task<int> RunAsync(
        CommandContext context, TSettings settings, SessionContext session,
        OutputWriter writer, CancellationToken ct);
}

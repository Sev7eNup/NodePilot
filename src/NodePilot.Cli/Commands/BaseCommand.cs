using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using NodePilot.Core.Clients;
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

    protected override async Task<int> ExecuteAsync(CommandContext context, TSettings settings, CancellationToken ct)
    {
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor || Console.IsOutputRedirected);
        SessionContext? resolved = null;
        try
        {
            var session = Sessions.Resolve(settings);
            resolved = session;
            TlsNotices.WriteBefore(writer, session.Tls, session.TlsPinNotice);
            return await RunAsync(context, settings, session, writer, ct);
        }
        catch (NotAuthenticatedException ex)
        {
            writer.Error(ex.Message);
            return ExitCodes.AuthRequired;
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            writer.Error("Session expired. Run `np auth login` again.");
            return ExitCodes.AuthRequired;
        }
        catch (ApiException ex) when (ex.IsForbidden)
        {
            writer.Error($"Access denied: {ex.Detail ?? ex.Title ?? "your role does not allow this action."}");
            return ExitCodes.PermissionDenied;
        }
        catch (ApiException ex) when (ex.IsLocked)
        {
            writer.Error($"Workflow is locked ({ex.Detail ?? "checked out by another user"}). Use `np workflow lock` (or `force-unlock` as Admin).");
            return ExitCodes.Error;
        }
        catch (ApiException ex)
        {
            writer.Error($"API error: {ex.Message}");
            return ExitCodes.Error;
        }
        catch (HttpRequestException ex)
        {
            writer.ErrorBlock(NetworkErrorRenderer.Render(ex, resolved?.Server ?? settings.Server));
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

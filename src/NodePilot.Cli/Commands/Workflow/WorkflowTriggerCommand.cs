using System.ComponentModel;
using System.Runtime.Versioning;
using NodePilot.Cli.Api;
using NodePilot.Cli.Auth;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console.Cli;

namespace NodePilot.Cli.Commands.Workflow;

/// <summary>
/// External-trigger command: POSTs to <c>/api/trigger/{name}</c> with an X-Api-Key header.
/// Deliberately session-independent — the endpoint is anonymous, gated only by the API key.
/// Operators on jump hosts can fire workflows without `np auth login`. The key may be
/// supplied via:
/// <list type="bullet">
///   <item><c>--api-key &lt;K&gt;</c> — literal flag (visible in history/ps output).</item>
///   <item><c>--api-key-stdin</c> — first line of stdin. Safe for scripted use.</item>
///   <item><c>NODEPILOT_TRIGGER_API_KEY</c> env var — also visible to subprocesses but
///   never lands in shell history.</item>
/// </list>
/// </summary>
public sealed class WorkflowTriggerSettings : GlobalSettings
{
    [CommandArgument(0, "<NAME-OR-ID>")]
    [Description("Workflow name (exact-case wins, else case-insensitive) or GUID.")]
    public string NameOrId { get; set; } = "";

    [CommandOption("--api-key <KEY>")]
    [Description("X-Api-Key value. Prefer --api-key-stdin or NODEPILOT_TRIGGER_API_KEY env in scripts.")]
    public string? ApiKey { get; set; }

    [CommandOption("--api-key-stdin")]
    [Description("Read the API key from the first line of stdin.")]
    public bool ApiKeyStdin { get; set; }

    [CommandOption("-p|--params <KV>")]
    [Description("Parameter as key=value (repeatable).")]
    public string[] Params { get; set; } = Array.Empty<string>();

    [CommandOption("--idempotency-key <KEY>")]
    [Description("Optional Idempotency-Key header. Replays of the same key return the original execution.")]
    public string? IdempotencyKey { get; set; }

    [CommandOption("--timeout <SECONDS>")]
    [Description("Cap the whole run at N seconds.")]
    public int? TimeoutSeconds { get; set; }

    [CommandOption("--wait")]
    [Description("Block until the run reaches a terminal status (poll-based — requires JWT session for /api/executions/{id} polling).")]
    public bool Wait { get; set; }
}

[SupportedOSPlatform("windows")]
public sealed class WorkflowTriggerCommand : AsyncCommand<WorkflowTriggerSettings>
{
    private readonly SessionResolver _sessions;
    private readonly ApiClientFactory _factory;

    public WorkflowTriggerCommand(SessionResolver sessions, ApiClientFactory factory)
    {
        _sessions = sessions;
        _factory = factory;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, WorkflowTriggerSettings settings)
    {
        var format = OutputFormatParser.Resolve(settings.Output);
        var writer = new OutputWriter(format, settings.NoColor || Console.IsOutputRedirected);
        var ct = CancellationToken.None;

        var session = _sessions.Resolve(settings);
        if (!session.HasServer)
        {
            writer.Error("Kein Server konfiguriert. `np config set server <URL>` oder --server angeben.");
            return ExitCodes.Error;
        }

        var apiKey = ResolveApiKey(settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            writer.Error("Kein API-Key übergeben. --api-key, --api-key-stdin oder NODEPILOT_TRIGGER_API_KEY env setzen.");
            return ExitCodes.Error;
        }

        // --wait implies "block until terminal" — but the polling endpoint /api/executions/{id}
        // is JWT-only, the API key alone can't read it. A late warning AFTER firing would let
        // CI scripts see exit 0 + "started OK" and assume the run finished, when really only
        // the trigger fired. Fail BEFORE firing so the workflow isn't kicked off in a state
        // the caller can't observe.
        if (settings.Wait && !session.HasSession)
        {
            writer.Error("--wait braucht eine gültige JWT-Session (`np auth login`) für das Polling. Trigger wurde NICHT gefeuert.");
            return ExitCodes.AuthRequired;
        }

        Dictionary<string, string>? parameters;
        try { parameters = RunParameterParser.Parse(settings.Params); }
        catch (ArgumentException ex)
        {
            writer.Error(ex.Message);
            return ExitCodes.Error;
        }

        // Anonymous client: the endpoint accepts X-Api-Key only, no JWT. We do not want
        // to leak the operator's bearer to the trigger URL — that would survive in proxy/
        // gateway logs and is unnecessary for this auth path.
        var api = _factory.CreateAnonymous(session.Server!, settings.AllowInsecureLoopback);
        try
        {
            var (execution, replayed) = await api.TriggerExternalAsync(
                settings.NameOrId, apiKey, parameters, settings.TimeoutSeconds, settings.IdempotencyKey, ct);

            if (replayed)
                writer.Info($"[grey]Idempotent-Replayed[/]: returning original execution [bold]{execution.Id}[/].");
            else
                writer.Info($"Execution gestartet: [bold]{execution.Id}[/]");

            if (!settings.Wait)
            {
                writer.WriteData(execution, (console, value) => Renderers.ExecutionDetail(console, value));
                return ExitCodes.Success;
            }

            // Pre-flight above guaranteed session.HasSession == true; safe to build the
            // authenticated client and poll /api/executions/{id} until terminal.
            var authedApi = _factory.Create(session);
            return await WorkflowRunCommand.PollUntilTerminalAsync(authedApi, execution.Id, writer, ct);
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            writer.Error("Trigger abgelehnt: ungültiger oder fehlender X-Api-Key.");
            return ExitCodes.AuthRequired;
        }
        catch (ApiException ex) when (ex.StatusCode == global::System.Net.HttpStatusCode.NotFound)
        {
            writer.Error($"Workflow '{settings.NameOrId}' nicht gefunden oder disabled.");
            return ExitCodes.Error;
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

    internal static string? ResolveApiKey(WorkflowTriggerSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ApiKey)) return settings.ApiKey;
        if (settings.ApiKeyStdin)
        {
            // First non-empty line wins. We trim trailing CR/LF only — leading whitespace
            // is preserved because some operators paste a key whose first byte is a space
            // (real-world: copy-paste from a vault UI) and a silent trim could mask
            // "wrong key" with a confusing 401.
            var line = Console.In.ReadLine();
            return string.IsNullOrEmpty(line) ? null : line.TrimEnd('\r', '\n');
        }
        var env = Environment.GetEnvironmentVariable("NODEPILOT_TRIGGER_API_KEY");
        return string.IsNullOrWhiteSpace(env) ? null : env;
    }
}

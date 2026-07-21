using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

public class RunScriptActivity : IActivityExecutor
{
    private readonly PowerShellEngineFactory _engineFactory;
    private readonly IRemoteSessionFactory? _sessionFactory;
    private readonly ICredentialStore? _credentialStore;
    private readonly NodePilot.Data.NodePilotDbContext? _db;
    private readonly ILogger<RunScriptActivity> _logger;
    // Only the transcript markers remain here — they belong to WrapWithTranscript (below), which
    // has no counterpart in PowerShellActivitySupport. Marker parsing + exit-code semantics live
    // in PowerShellActivitySupport, shared with CustomActivityExecutor.
    private const string TranscriptStartMarker = "###NODEPILOT_TRANSCRIPT_START###";
    private const string TranscriptEndMarker = "###NODEPILOT_TRANSCRIPT_END###";

    public string ActivityType => "runScript";

    public RunScriptActivity(PowerShellEngineFactory engineFactory, ILogger<RunScriptActivity> logger)
    {
        _engineFactory = engineFactory;
        _logger = logger;
    }

    public RunScriptActivity(
        PowerShellEngineFactory engineFactory,
        IRemoteSessionFactory sessionFactory,
        ICredentialStore credentialStore,
        NodePilot.Data.NodePilotDbContext db,
        ILogger<RunScriptActivity> logger)
    {
        _engineFactory = engineFactory;
        _sessionFactory = sessionFactory;
        _credentialStore = credentialStore;
        _db = db;
        _logger = logger;
    }

    public async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var script = config.GetStringOrNull("script");
        if (string.IsNullOrWhiteSpace(script))
            return new ActivityResult { Success = false, ErrorOutput = "No script specified" };

        var engineType = config.GetString("engine", "auto");
        var timeoutSeconds = config.GetOptionalPositiveInt("timeoutSeconds");
        var transcriptEnabled = config.TryGetProperty("transcript", out var tr)
            && tr.ValueKind == JsonValueKind.True;

        // Opt-in process isolation (local execution only). Caps are read only when isolation is on.
        var isolated = config.GetBool("isolated", false);
        var isolationLimits = isolated
            ? new ProcessIsolationLimits
            {
                MemoryLimitMb = config.GetOptionalPositiveInt("memoryLimitMb"),
                MaxProcesses = config.GetOptionalPositiveInt("maxProcesses"),
            }
            : null;

        // Opt-in exit-code gate. null = unset = pure error-based (only a throw fails the step;
        // `exit N` is ignored). When set, success additionally requires the captured exit code to
        // be in the set. Gates on $LASTEXITCODE (native command) consistently across all engines;
        // a script-level `exit N` is only observable on the process/isolated path.
        var successExitCodes = PowerShellActivitySupport.ParseSuccessExitCodes(config.GetStringOrNull("successExitCodes"));

        // Resolve {{variable}} expressions in the script text with PowerShell-safe quoting
        // This wraps resolved values in single quotes so PowerShell treats them as strings
        script = PowerShellActivitySupport.ResolveScriptVariables(script, context.Variables);

        // Wrap with Start-Transcript / Stop-Transcript when the user opted in via
        // config.transcript: true. Captures interleaved command + output as PowerShell
        // would have produced it on a console. The wrapped script reads the transcript
        // file in `finally` so even thrown user scripts surface their tracing data.
        if (transcriptEnabled)
            script = WrapWithTranscript(script);

        var (machine, credential, targetError) = await ResolveTargetAsync(context, ct);
        if (targetError is not null)
            return new ActivityResult { Success = false, ErrorOutput = targetError };

        if (machine is not null && (credential is not null || !BaseRemoteActivity.IsLoopbackHostname(machine.Hostname)))
            return await ExecuteRemoteAsync(machine, credential, script, timeoutSeconds, successExitCodes, context, ct);

        return await ExecuteLocalAsync(script, engineType, timeoutSeconds, isolated, isolationLimits, successExitCodes, context, ct);
    }

    private async Task<ActivityResult> ExecuteLocalAsync(
        string script,
        string engineType,
        int? timeoutSeconds,
        bool isolated,
        ProcessIsolationLimits? isolationLimits,
        HashSet<int>? successExitCodes,
        StepExecutionContext context,
        CancellationToken ct)
    {
        IPowerShellExecutionEngine engine;
        try
        {
            engine = _engineFactory.GetEngine(engineType, isolated);
        }
        catch (InvalidOperationException ex)
        {
            // Isolation requested but no out-of-process host available — surface as a clean step
            // failure rather than letting the throw bubble up (would mark the whole run failed
            // with an opaque message).
            return new ActivityResult { Success = false, ErrorOutput = ex.Message };
        }

        var request = new PowerShellExecutionRequest
        {
            ScriptText = script,
            Engine = engineType,
            Parameters = context.Variables,
            Timeout = timeoutSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null,
            Isolated = isolated,
            IsolationLimits = isolationLimits,
        };

        var result = await engine.ExecuteAsync(request, ct);

        var (cleanOutput, transcript, outputParams) = PowerShellActivitySupport.ExtractMarkers(result.Output, context.StepId, _logger);
        var (success, finalParams) = PowerShellActivitySupport.ApplyExitCodeSemantics(result.Success, outputParams, result.ExitCode, successExitCodes);

        return new ActivityResult
        {
            Success = success,
            Output = cleanOutput,
            ErrorOutput = result.Error,
            Duration = result.Duration,
            OutputParameters = finalParams,
            TraceOutput = transcript,
        };
    }

    private async Task<ActivityResult> ExecuteRemoteAsync(
        ManagedMachine machine,
        Credential? credential,
        string script,
        int? timeoutSeconds,
        HashSet<int>? successExitCodes,
        StepExecutionContext context,
        CancellationToken ct)
    {
        if (_sessionFactory is null)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = "Remote execution infrastructure is unavailable for runScript"
            };
        }

        var wrappedScript = PowerShellScriptWrapper.Wrap(script, context.Variables, _logger);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var session = await _sessionFactory.CreateSessionAsync(machine, credential, ct);
        var result = await session.ExecuteScriptAsync(wrappedScript, timeoutSeconds, ct);
        sw.Stop();

        var (cleanOutput, transcript, outputParams) = PowerShellActivitySupport.ExtractMarkers(result.Output, context.StepId, _logger);
        // Remote has no real exit code (RemoteExecutionResult), so the wrapper-captured
        // $LASTEXITCODE is the only source; fall back to 0 when capture was skipped (`exit N`).
        var (success, finalParams) = PowerShellActivitySupport.ApplyExitCodeSemantics(result.Success, outputParams, null, successExitCodes);

        return new ActivityResult
        {
            Success = success,
            Output = cleanOutput,
            ErrorOutput = result.ErrorOutput,
            Duration = sw.Elapsed,
            OutputParameters = finalParams,
            TraceOutput = transcript,
        };
    }

    private async Task<(ManagedMachine? machine, Credential? credential, string? error)> ResolveTargetAsync(
        StepExecutionContext context,
        CancellationToken ct)
    {
        var machine = context.ResolvedMachine;
        if (machine is null && context.TargetMachineId is not null && _db is not null)
            machine = await _db.ManagedMachines.FindAsync([context.TargetMachineId.Value], ct);

        if (machine is null)
            return (null, null, null);

        var credentialId = context.CredentialId ?? machine.DefaultCredentialId;
        if (credentialId is null)
            return (machine, null, null);

        if (_credentialStore is null)
            return (machine, null, "Credential store is unavailable for runScript remote execution");

        var credential = await _credentialStore.GetAsync(credentialId.Value, ct);
        return (machine, credential, null);
    }

    /// <summary>
    /// Wraps the user script with a Start-Transcript/Stop-Transcript block. The transcript
    /// file lives in the executing host's <c>$env:TEMP</c>: the API host for local runs,
    /// or the WinRM target for remote runs. A pre-cleanup pass deletes orphan
    /// NodePilot-Transcript-*.log files older than 24h - covers the case where Stop-Transcript
    /// did not run (hard cancel mid-script). The finally-block reads the transcript content
    /// and emits it between markers BEFORE any unhandled exception propagates, so even failing
    /// scripts surface their command-by-command log.
    /// </summary>
    internal static string WrapWithTranscript(string userScript)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# === NODEPILOT TRANSCRIPT WRAPPER ===");
        sb.AppendLine("$__npTranscriptPath = Join-Path $env:TEMP (\"NodePilot-Transcript-\" + [guid]::NewGuid().ToString() + \".log\")");
        // Pre-cleanup: own past leaks (>24h). Cheap, runs locally, makes the system
        // self-healing without a separate cron.
        sb.AppendLine("try {");
        sb.AppendLine("    Get-ChildItem (Join-Path $env:TEMP 'NodePilot-Transcript-*.log') -ErrorAction SilentlyContinue |");
        sb.AppendLine("        Where-Object { $_.LastWriteTime -lt (Get-Date).AddHours(-24) } |");
        sb.AppendLine("        Remove-Item -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("} catch {}");
        // Defensive: if a previous Start-Transcript is still active in this runspace
        // (user-started, leftover from a parent script block, …), Start-Transcript
        // throws "Transcription has already been started". Stop it first, ignore errors.
        sb.AppendLine("try { Stop-Transcript -ErrorAction Stop | Out-Null } catch {}");
        sb.AppendLine("Start-Transcript -Path $__npTranscriptPath -IncludeInvocationHeader -Force | Out-Null");
        sb.AppendLine("$__npTranscriptContent = $null");
        sb.AppendLine("try {");
        sb.AppendLine("# === USER SCRIPT START ===");
        sb.AppendLine(userScript);
        sb.AppendLine("# === USER SCRIPT END ===");
        sb.AppendLine("} finally {");
        sb.AppendLine("    try { Stop-Transcript | Out-Null } catch {}");
        sb.AppendLine("    try { $__npTranscriptContent = Get-Content -LiteralPath $__npTranscriptPath -Raw -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("    try { Remove-Item -LiteralPath $__npTranscriptPath -Force -ErrorAction SilentlyContinue } catch {}");
        // Emit between markers to stdout so the activity can extract the transcript
        // separately from the user's regular Output stream. PowerShell's `try/finally`
        // re-throws the original exception after this block, so failing scripts still
        // get their transcript surfaced before the error propagates upward.
        sb.AppendLine("    Write-Output '" + TranscriptStartMarker + "'");
        sb.AppendLine("    if ($null -ne $__npTranscriptContent) { Write-Output $__npTranscriptContent }");
        sb.AppendLine("    Write-Output '" + TranscriptEndMarker + "'");
        sb.AppendLine("}");
        return sb.ToString();
    }

}

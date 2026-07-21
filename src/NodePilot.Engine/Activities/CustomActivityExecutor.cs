using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Activities;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

/// <summary>
/// The single executor for every user-authored custom activity. Workflow nodes carry the routing
/// type <c>custom:&lt;key&gt;</c> and reference the definition via <c>config.__customDefinitionId</c>
/// (authoritative) + <c>config.__customKey</c> (drift cross-check). This class is a thin adapter that
/// turns a <see cref="CustomActivityDefinition"/> into a runScript-equivalent invocation: it resolves
/// the declared input values, injects them as PowerShell variables, runs the script template through
/// the shared <see cref="PowerShellActivitySupport"/> primitives, and captures ONLY the declared
/// outputs via the wrapper allow-list. A missing/disabled/mismatched definition is a clean step
/// failure (Success=false), never a thrown run-aborting exception.
///
/// <para>Dispatch: <see cref="ActivityRegistry"/> resolves any <c>custom:*</c> type to this executor;
/// <see cref="ActivityType"/> is the reserved sentinel under which it registers.</para>
/// </summary>
public sealed class CustomActivityExecutor : IActivityExecutor
{
    private readonly ICustomActivityDefinitionStore _store;
    private readonly PowerShellEngineFactory _engineFactory;
    private readonly IRemoteSessionFactory? _sessionFactory;
    private readonly ICredentialStore? _credentialStore;
    private readonly NodePilot.Data.NodePilotDbContext? _db;
    private readonly ILogger<CustomActivityExecutor> _logger;

    public string ActivityType => CustomActivityType.ExecutorSentinel;

    public CustomActivityExecutor(
        ICustomActivityDefinitionStore store,
        PowerShellEngineFactory engineFactory,
        ILogger<CustomActivityExecutor> logger,
        IRemoteSessionFactory? sessionFactory = null,
        ICredentialStore? credentialStore = null,
        NodePilot.Data.NodePilotDbContext? db = null)
    {
        _store = store;
        _engineFactory = engineFactory;
        _logger = logger;
        _sessionFactory = sessionFactory;
        _credentialStore = credentialStore;
        _db = db;
    }

    public async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        // 1. Resolve the definition (authoritative link = the GUID).
        var defIdRaw = config.GetStringOrNull("__customDefinitionId");
        if (!Guid.TryParse(defIdRaw, out var defId))
            return Fail("Custom activity node is missing its definition reference (__customDefinitionId).");

        var def = await _store.GetByIdAsync(defId, ct);
        if (def is null)
            return Fail("The referenced custom activity definition no longer exists (deleted or never imported).");
        if (!def.IsEnabled)
            return Fail($"Custom activity '{def.Name}' is disabled and cannot run until an administrator enables it.");

        // 2. Drift guard: the node's embedded key (custom:<key>) must match the definition's key.
        var expectedKey = config.GetStringOrNull("__customKey");
        if (expectedKey is not null && !string.Equals(expectedKey, def.Key, StringComparison.Ordinal))
            return Fail($"Custom activity reference drift: node points at key '{expectedKey}' but definition {defId} now has key '{def.Key}'.");

        // 3. Honor RunsRemote explicitly (routing is otherwise data-driven and would silently run local).
        var machine = def.RunsRemote ? context.ResolvedMachine : null;
        if (def.RunsRemote && machine is null && context.TargetMachineId is { } tid && _db is not null)
            machine = await _db.ManagedMachines.FindAsync([tid], ct);
        if (def.RunsRemote && machine is null)
            return Fail($"Custom activity '{def.Name}' requires a target machine but none was provided.");

        // 4. Resolve declared inputs to raw values (against the step variables) and inject them as
        //    PowerShell variables. Routing through the variables dict means the wrapper injects them as
        //    $name; the capture allow-list (step 6) keeps them out of the outputs.
        var variables = new Dictionary<string, string>(context.Variables);
        foreach (var p in CustomActivityParameters.ParseInputs(def.InputParametersJson))
        {
            var raw = config.GetStringOrNull(p.Name) ?? p.Default;
            if (raw is null)
            {
                if (p.Required)
                    return Fail($"Required input '{p.Name}' of custom activity '{def.Name}' is not set.");
                continue;
            }
            variables[p.Name] = PowerShellActivitySupport.ResolveTemplateRaw(raw, context.Variables);
        }

        // 5. Resolve {{globals.X}} / upstream refs the author embedded in the template itself.
        var script = PowerShellActivitySupport.ResolveScriptVariables(def.ScriptTemplate, variables);

        // 6. Build options. Allow-list = declared output names; exitCode is emitted separately anyway.
        var allowlist = CustomActivityParameters.ParseOutputs(def.OutputParametersJson)
            .Select(o => o.Name).ToArray();
        var timeoutSeconds = config.GetOptionalPositiveInt("timeoutSeconds") ?? def.DefaultTimeoutSeconds;
        var successExitCodes = PowerShellActivitySupport.ParseSuccessExitCodes(def.SuccessExitCodes);
        var provenance = CustomActivityHashing.ProvenanceOf(def);

        // 7. Dispatch. Localhost target with no credential falls through to in-process (bypass).
        var credentialId = context.CredentialId ?? machine?.DefaultCredentialId;
        Credential? credential = null;
        if (credentialId is { } cid && _credentialStore is not null)
            credential = await _credentialStore.GetAsync(cid, ct);

        var goRemote = machine is not null
            && (credential is not null || !BaseRemoteActivity.IsLoopbackHostname(machine.Hostname));

        var result = goRemote
            ? await ExecuteRemoteAsync(machine!, credential, script, variables, timeoutSeconds, allowlist, successExitCodes, context.StepId, ct)
            : await ExecuteLocalAsync(script, variables, def, timeoutSeconds, allowlist, successExitCodes, context.StepId, ct);

        result.CustomActivity = provenance;
        return result;
    }

    private async Task<ActivityResult> ExecuteLocalAsync(
        string script, Dictionary<string, string> variables, CustomActivityDefinition def,
        int? timeoutSeconds, IReadOnlyCollection<string> allowlist, HashSet<int>? successExitCodes,
        string stepId, CancellationToken ct)
    {
        IPowerShellExecutionEngine engine;
        try
        {
            engine = _engineFactory.GetEngine(def.Engine, def.Isolated);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }

        var request = new PowerShellExecutionRequest
        {
            ScriptText = script,
            Engine = def.Engine,
            Parameters = variables,
            Timeout = timeoutSeconds is { } secs ? TimeSpan.FromSeconds(secs) : null,
            Isolated = def.Isolated,
            IsolationLimits = def.Isolated
                ? new ProcessIsolationLimits { MemoryLimitMb = def.MemoryLimitMb, MaxProcesses = def.MaxProcesses }
                : null,
            OutputCaptureAllowlist = allowlist,
        };

        var result = await engine.ExecuteAsync(request, ct);
        var (clean, transcript, outputParams) = PowerShellActivitySupport.ExtractMarkers(result.Output, stepId, _logger);
        var (success, finalParams) = PowerShellActivitySupport.ApplyExitCodeSemantics(result.Success, outputParams, result.ExitCode, successExitCodes);
        finalParams = CanonicalizeDeclaredOutputParameters(finalParams, allowlist);

        return new ActivityResult
        {
            Success = success,
            Output = clean,
            ErrorOutput = result.Error,
            Duration = result.Duration,
            OutputParameters = finalParams,
            TraceOutput = transcript,
        };
    }

    private async Task<ActivityResult> ExecuteRemoteAsync(
        ManagedMachine machine, Credential? credential, string script, Dictionary<string, string> variables,
        int? timeoutSeconds, IReadOnlyCollection<string> allowlist, HashSet<int>? successExitCodes,
        string stepId, CancellationToken ct)
    {
        if (_sessionFactory is null)
            return Fail("Remote execution infrastructure is unavailable for this custom activity.");

        var wrapped = PowerShellScriptWrapper.Wrap(script, variables, _logger, allowlist);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var session = await _sessionFactory.CreateSessionAsync(machine, credential, ct);
        var result = await session.ExecuteScriptAsync(wrapped, timeoutSeconds, ct);
        sw.Stop();

        var (clean, transcript, outputParams) = PowerShellActivitySupport.ExtractMarkers(result.Output, stepId, _logger);
        var (success, finalParams) = PowerShellActivitySupport.ApplyExitCodeSemantics(result.Success, outputParams, null, successExitCodes);
        finalParams = CanonicalizeDeclaredOutputParameters(finalParams, allowlist);

        return new ActivityResult
        {
            Success = success,
            Output = clean,
            ErrorOutput = result.ErrorOutput,
            Duration = sw.Elapsed,
            OutputParameters = finalParams,
            TraceOutput = transcript,
        };
    }

    private static Dictionary<string, string> CanonicalizeDeclaredOutputParameters(
        IReadOnlyDictionary<string, string> parameters,
        IReadOnlyCollection<string> declaredOutputs)
    {
        var captured = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var name in declaredOutputs)
        {
            if (captured.TryGetValue(name, out var value))
                canonical[name] = value;
        }

        if (captured.TryGetValue("exitCode", out var exitCode))
            canonical["exitCode"] = exitCode;

        return canonical;
    }

    private static ActivityResult Fail(string message) => new() { Success = false, ErrorOutput = message };
}

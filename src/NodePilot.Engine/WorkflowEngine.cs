using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Constants;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Debug;
using NodePilot.Engine.Execution;
using NodePilot.Core.Telemetry;

namespace NodePilot.Engine;

public class WorkflowEngine : IWorkflowEngine
{
    private readonly NodePilotDbContext _db;
    private readonly ActivityRegistry _registry;
    private readonly ILogger<WorkflowEngine> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IExecutionNotifier _notifier;
    private readonly IConfiguration? _configuration;
    private readonly NodePilot.Engine.Security.OutputRedactor _redactor;
    private readonly StepRunner _stepRunner;
    private readonly string? _dbProviderTag;
    private readonly bool _deferRunningStateWrite;
    private readonly NodePilot.Core.Interfaces.IClusterStateProvider? _cluster;

    internal static readonly ActivitySource EngineActivitySource = new(TelemetryConstants.Sources.Engine);
    public static readonly ActivitySource ActivitiesSource = new(TelemetryConstants.Sources.EngineActivities);

    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningExecutions = new();
    // Cancel attribution: a caller (e.g. the manual-cancel controller) records WHO cancelled here
    // BEFORE tripping the token, so the engine's single OperationCanceledException catch — which is
    // also reached by timeouts and host shutdown — can stamp the reason onto the execution row.
    // Absent entry ⇒ "system" (timeout / shutdown / token cancel without an explicit reason).
    private static readonly ConcurrentDictionary<Guid, string> _cancelReasons = new();
    private static readonly object _capacityGate = new();
    private static int _reservedExecutionSlots;

    // H-3 (security-audit finding): per-user counter running alongside the
    // _runningExecutions dict. Only top-level runs (callDepth==0 with StartedByUserId
    // set) count — sub-workflows are covered by their parent run and shouldn't be
    // charged to the user a second time. An entry is removed once it decrements to 0
    // so the dict doesn't grow unbounded across many different users.
    private static readonly ConcurrentDictionary<Guid, int> _userExecutionCounts = new();

    /// <summary>
    /// Tries to obtain the <see cref="CancellationToken"/> of a currently-running execution
    /// (fire-and-forget sub-workflows use this to inherit parent cancellation). Returns
    /// <see cref="CancellationToken.None"/> and false when the execution is unknown to this
    /// process instance.
    /// </summary>
    public static bool TryGetExecutionCancellation(Guid executionId, out CancellationToken token)
    {
        if (!_runningExecutions.TryGetValue(executionId, out var cts))
        {
            token = CancellationToken.None;
            return false;
        }
        token = cts.Token;
        return true;
    }

    /// <summary>
    /// Apply the redactor to untrusted text and cap the length so a single leaked blob
    /// can't blow the DB row or the audit log. Internal helper used for ErrorMessage,
    /// InputParametersJson, ReturnData.
    /// </summary>
    private string? RedactAndCap(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = _redactor.Redact(value) ?? value;
        return redacted.Length > maxChars
            ? redacted.Substring(0, maxChars) + "... [truncated]"
            : redacted;
    }

    // Per-execution debug state: breakpoint pauses + step-over control. Runs parallel to
    // the _runningExecutions dict (same executionId key), so the cancel path can also
    // reach any paused steps (otherwise the engine thread would hang forever if the user
    // clicks Cancel while paused at a breakpoint). Only populated while the execution is
    // running with debugEnabled=true; removed from memory in the finally block.
    private static readonly ConcurrentDictionary<Guid, DebugHandle> _debugHandles = new();

    public WorkflowEngine(
        NodePilotDbContext db,
        ActivityRegistry registry,
        ILogger<WorkflowEngine> logger,
        IServiceProvider serviceProvider,
        IExecutionNotifier notifier,
        IConfiguration? configuration = null)
    {
        _db = db;
        _registry = registry;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _notifier = notifier;
        // Debug mode: per-step detail logging is now *opt-in* (default false). In
        // production with many workflows running per minute, the Serilog emit + the
        // OutputRedactor regex noticeably add up in cost; ops teams that want step
        // output in the log file for debugging turn the flag on explicitly.
        var stepDetailEnabled = configuration?.GetValue("Logging:StepDetail:Enabled", false) ?? false;
        var stepDetailMaxChars = configuration?.GetValue("Logging:StepDetail:MaxOutputChars", 10_000) ?? 10_000;
        // StepExecution write strategy: default is "defer" (only *one* SaveChanges per
        // step, at its terminal state). The earlier double-write (an insert when the step
        // went Running, then an update at completion) doubled the DB round-trip cost per
        // step. Deployments that need to see the Running row live via REST polling
        // (instead of SignalR) can switch back to the old two-phase behavior via
        // Engine:DeferRunningStateWrite=false. The SignalR StepStarted event still fires
        // in both modes.
        var deferRunningStateWrite = configuration?.GetValue("Engine:DeferRunningStateWrite", true) ?? true;
        _deferRunningStateWrite = deferRunningStateWrite;
        _configuration = configuration;
        // Resolve cluster state provider lazily — registered in single-node mode as a no-op
        // (NodeId = MachineName) and in cluster mode as the real ClusterLeaderService.
        // GetService (not GetRequiredService) so test harnesses without DI for cluster work.
        _cluster = serviceProvider.GetService(typeof(NodePilot.Core.Interfaces.IClusterStateProvider))
            as NodePilot.Core.Interfaces.IClusterStateProvider;
        _redactor = new NodePilot.Engine.Security.OutputRedactor(configuration);
        _stepRunner = new StepRunner(
            _serviceProvider,
            _notifier,
            _logger,
            _redactor,
            stepDetailEnabled,
            stepDetailMaxChars,
            deferRunningStateWrite);
        // Snap the active EF provider into a short tag so we don't pay the EF reflection
        // cost on every workflow.execute span. The app's DB provider in production is
        // "sqlserver" or "postgres" (matches the Database:Provider config key); "sqlite"
        // only shows up in tests (the in-memory backend) — the mapping stays in place for
        // test consistency.
        _dbProviderTag = MapProviderName(_db.Database.ProviderName);
    }

    /// <summary>
    /// Enforces the global and per-user capacity caps (Audit H-3) before a slot is reserved
    /// for a new run. Throws <see cref="NodePilot.Core.Exceptions.ExecutionCapacityException"/>
    /// with an actionable message when either cap is exceeded; the controller layer maps
    /// that to a 503/429 response.
    ///
    /// Returns whether this run was counted against the per-user quota so the caller can
    /// decrement the counter symmetrically in the <c>finally</c> block.
    /// </summary>
    private bool CheckCapacityCaps(Guid? startedByUserId, int callDepth, bool interactiveRun)
    {
        const int DefaultMaxGlobal = 500;
        const int DefaultMaxPerUser = 200;
        var maxGlobal = _configuration?.GetValue("Engine:MaxConcurrentExecutions:Global", DefaultMaxGlobal) ?? DefaultMaxGlobal;
        var maxPerUser = _configuration?.GetValue("Engine:MaxConcurrentExecutions:PerUser", DefaultMaxPerUser) ?? DefaultMaxPerUser;

        lock (_capacityGate)
        {
            if (maxGlobal > 0 && _reservedExecutionSlots >= maxGlobal)
            {
                EngineMetrics.ExecutionsRejected.Add(1,
                    new KeyValuePair<string, object?>("reason", "global_cap"));
                throw new NodePilot.Core.Exceptions.ExecutionCapacityException(
                    $"Maximum concurrent workflow executions ({maxGlobal}) reached. " +
                    "Wait for in-flight runs to complete or raise Engine:MaxConcurrentExecutions:Global.");
            }

            // Interactive runs (user-clicked Test/Debug from the editor) are explicit, single-shot
            // actions gated by Admin/Operator role. They must not be blocked by the per-user cap,
            // which exists to throttle automated bursts, but they still count toward the global cap.
            var perUserCounted = !interactiveRun
                && callDepth == 0
                && startedByUserId is { } uid && uid != Guid.Empty
                && maxPerUser > 0;
            if (perUserCounted)
            {
                var current = _userExecutionCounts.GetValueOrDefault(startedByUserId!.Value);
                if (current >= maxPerUser)
                {
                    EngineMetrics.ExecutionsRejected.Add(1,
                        new KeyValuePair<string, object?>("reason", "per_user_cap"));
                    throw new NodePilot.Core.Exceptions.ExecutionCapacityException(
                        $"User has {current} concurrent executions in flight (limit: {maxPerUser}). " +
                        "Cancel running workflows or raise Engine:MaxConcurrentExecutions:PerUser.");
                }

                _userExecutionCounts[startedByUserId!.Value] = current + 1;
            }

            _reservedExecutionSlots++;
            return perUserCounted;
        }
    }

    private static void ReleaseCapacitySlot(Guid? startedByUserId, bool perUserCounted)
    {
        lock (_capacityGate)
        {
            if (_reservedExecutionSlots > 0)
                _reservedExecutionSlots--;

            if (perUserCounted && startedByUserId is { } uid)
            {
                var current = _userExecutionCounts.GetValueOrDefault(uid);
                if (current <= 1)
                    _userExecutionCounts.TryRemove(uid, out _);
                else
                    _userExecutionCounts[uid] = current - 1;
            }
        }
    }

    /// <summary>
    /// Resolves global variables for a single run. Optimised for the common case where the
    /// workflow definition does not reference <c>{{globals.…}}</c> at all — a cheap substring
    /// scan skips the DB hit + decryption entirely. Returns both the resolved dict and the
    /// set of variables that exist in the DB but couldn't be decrypted; the caller decides
    /// whether to fail loudly when the workflow actually references one of the broken ones.
    /// Infrastructure errors (DB unreachable, etc.) are still tolerated as "no globals" so
    /// a brief outage in the variables table can't poison unrelated workflows that don't
    /// use them.
    /// </summary>
    private async Task<NodePilot.Core.Interfaces.GlobalVariableResolutionResult> ResolveGlobalVariablesAsync(
        string definitionJson,
        Guid executionId,
        CancellationToken ct)
    {
        var empty = new NodePilot.Core.Interfaces.GlobalVariableResolutionResult(
            new Dictionary<string, string>(0), new HashSet<string>(StringComparer.Ordinal));

        if (!definitionJson.Contains("{{globals.", StringComparison.Ordinal))
            return empty;

        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var store = scope.ServiceProvider.GetService<IGlobalVariableStore>();
            return store is null
                ? empty
                : await store.GetAllResolvedDetailedAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to resolve global variables for execution {ExecutionId} — workflow will run without them.",
                executionId);
            return empty;
        }
    }

    /// <summary>
    /// Pre-flight check: scans the workflow definition for <c>{{globals.NAME}}</c>
    /// references and fails the run loudly if any referenced NAME is in the
    /// <paramref name="unresolvable"/> set (i.e. exists in the DB but couldn't be
    /// decrypted on this host). Without this check, the workflow would run with the
    /// literal placeholder text in place of the secret — which the activity then
    /// happily passes to whatever downstream system expects e.g. an API key.
    /// Returns null when everything is fine; otherwise an actionable error message
    /// identifying every broken reference.
    /// </summary>
    private static string? FindUnresolvableGlobalReferences(string definitionJson, IReadOnlySet<string> unresolvable)
    {
        if (unresolvable.Count == 0) return null;
        var matches = NodePilot.Engine.Execution.VariableResolver.GlobalsPattern.Matches(definitionJson);
        if (matches.Count == 0) return null;

        var hit = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var name = m.Groups[1].Value;
            if (unresolvable.Contains(name)) hit.Add(name);
        }
        if (hit.Count == 0) return null;

        return "Workflow references global variable(s) that exist in the database but " +
            "could not be decrypted on this host: " + string.Join(", ", hit) +
            ". Likely cause: DPAPI scope mismatch (e.g. clustered HA with DPAPI provider, " +
            "Service Account changed, DB restored from a different host). Re-enter the " +
            "value(s) or switch to a portable secret provider (Secrets:Provider=AesGcm).";
    }

    /// <summary>
    /// Emits a compact line to the support log (a second Serilog sink) when a run
    /// starts. Format: <c>EXECUTION_STARTED workflow=… exec=… trigger=… user=…</c>.
    /// Only called for top-level runs (callDepth==0).
    /// </summary>
    private void LogExecutionStartedAsSupport(WorkflowExecution execution, Workflow workflow, string triggeredBy)
    {
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["workflow_execution_id"] = execution.Id,
            ["workflow_id"] = workflow.Id,
            ["workflow_name"] = workflow.Name,
            ["trigger"] = triggeredBy,
            ["SupportLog"] = true,
            ["support.event_type"] = "EXECUTION_STARTED",
            // Clean message for the DB projection — no duplicate workflow/exec info.
            ["support.message"] = $"trigger={triggeredBy} user={execution.StartedByUserId?.ToString() ?? "-"}",
        }))
        {
            _logger.LogInformation(
                "EXECUTION_STARTED workflow={WorkflowName} exec={ExecutionShort} trigger={Trigger} user={UserId}",
                workflow.Name,
                execution.Id.ToString("N")[..8],
                triggeredBy,
                execution.StartedByUserId?.ToString() ?? "-");
        }
    }

    /// <summary>
    /// Emits a compact line to the support log when a run reaches a terminal state. The
    /// status suffix carries the outcome, plus duration and step counts. Format:
    /// <c>EXECUTION_&lt;STATUS&gt; workflow=… exec=… duration=…s steps=ok:N/fail:M/skip:K</c>.
    /// </summary>
    private void LogExecutionTerminatedAsSupport(
        WorkflowExecution execution, Workflow workflow, TimeSpan duration,
        int okCount, int failCount, int skipCount)
    {
        var statusLabel = execution.Status switch
        {
            ExecutionStatus.Succeeded => "EXECUTION_SUCCEEDED",
            ExecutionStatus.Failed    => "EXECUTION_FAILED",
            ExecutionStatus.Cancelled => "EXECUTION_CANCELLED",
            _                         => "EXECUTION_COMPLETED",
        };
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["workflow_execution_id"] = execution.Id,
            ["workflow_id"] = workflow.Id,
            ["workflow_name"] = workflow.Name,
            ["SupportLog"] = true,
            ["support.event_type"] = statusLabel,
            ["duration_sec"] = duration.TotalSeconds,
            ["steps_ok"] = okCount,
            ["steps_failed"] = failCount,
            ["steps_skipped"] = skipCount,
            // Clean message: duration + step counters (workflow/exec are their own columns).
            ["support.message"] = $"duration={duration.TotalSeconds:F1}s steps=ok:{okCount}/fail:{failCount}/skip:{skipCount}",
        }))
        {
            if (execution.Status == ExecutionStatus.Succeeded)
            {
                _logger.LogInformation(
                    "{StatusLabel} workflow={WorkflowName} exec={ExecutionShort} duration={DurationSec:F1}s steps=ok:{Ok}/fail:{Fail}/skip:{Skip}",
                    statusLabel, workflow.Name, execution.Id.ToString("N")[..8],
                    duration.TotalSeconds, okCount, failCount, skipCount);
            }
            else
            {
                _logger.LogWarning(
                    "{StatusLabel} workflow={WorkflowName} exec={ExecutionShort} duration={DurationSec:F1}s steps=ok:{Ok}/fail:{Fail}/skip:{Skip}",
                    statusLabel, workflow.Name, execution.Id.ToString("N")[..8],
                    duration.TotalSeconds, okCount, failCount, skipCount);
            }
        }
    }

    private static string? MapProviderName(string? efProviderName)
    {
        if (string.IsNullOrEmpty(efProviderName)) return null;
        if (efProviderName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return "sqlite";
        if (efProviderName.Contains("SqlServer", StringComparison.OrdinalIgnoreCase)) return "sqlserver";
        if (efProviderName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)) return "postgres";
        return efProviderName;
    }

    /// <summary>
    /// Per-run bundle handed between the <see cref="ExecuteAsync"/> phases so the phase
    /// methods don't each carry the same six telemetry/identity parameters. Owns nothing —
    /// the disposables (Activity, CTS, logger scope) stay owned by the orchestrator.
    /// </summary>
    private sealed record ExecutionRun(
        Workflow Workflow,
        WorkflowExecution Execution,
        Activity? Activity,
        Stopwatch Stopwatch,
        KeyValuePair<string, object?> WorkflowIdTag,
        KeyValuePair<string, object?> WorkflowNameTag,
        int CallDepth,
        string? ExpectedOwnerNodeId,
        long ExpectedLeaseEpoch);

    /// <summary>
    /// Orchestrates one workflow run. The phases live in dedicated methods (create/reset row,
    /// globals fail-fast, cancellation ceiling, debug handle, graph run, terminal handlers,
    /// runtime-state cleanup); this method owns the disposables, the capacity reservation and
    /// the single try/catch/finally that guarantees symmetric cleanup.
    /// </summary>
    public async Task<WorkflowExecution> ExecuteAsync(Workflow workflow, string triggeredBy, CancellationToken ct,
        Dictionary<string, string>? inputParameters = null,
        int? timeoutSeconds = null,
        bool debugEnabled = false,
        Guid? startedByUserId = null,
        Guid? parentExecutionId = null,
        int callDepth = 0,
        Guid? executionIdOverride = null,
        bool interactiveRun = false)
    {
        var executionId = executionIdOverride ?? Guid.NewGuid();
        var expectedOwnerNodeId = _cluster?.NodeId;
        var expectedLeaseEpoch = _cluster?.LeaseEpoch ?? 0;
        WorkflowExecution? existingExecution = null;
        using var cts = CreateRunCancellation(ct, timeoutSeconds);
        using var dispatchClaimRegistration = executionIdOverride is not null
            ? TryRegisterDispatchClaim(executionId, cts)
            : null;
        if (executionIdOverride is not null)
        {
            existingExecution = await _db.WorkflowExecutions.FindAsync([executionId], ct);
            if (existingExecution is not null)
            {
                if (existingExecution.WorkflowId != workflow.Id)
                    throw new InvalidOperationException("Execution id override belongs to a different workflow.");

                if (dispatchClaimRegistration is null)
                    return existingExecution;

                // Claim the queued row with a database-side compare-and-set. A tracked
                // Pending entity may be stale when AD/SCIM/admin offboarding has already
                // cancelled the row through another DbContext. Saving that stale entity
                // used to overwrite Cancelled with Running and execute the workflow.
                var claimCandidates = _db.WorkflowExecutions
                    .Where(candidate => candidate.Id == executionId
                                     && candidate.WorkflowId == workflow.Id
                                     && candidate.Status == ExecutionStatus.Pending);
                claimCandidates = ApplyExecutionWriteFence(
                    _db, claimCandidates, expectedOwnerNodeId, expectedLeaseEpoch);
                var claimed = await claimCandidates
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(candidate => candidate.Status, ExecutionStatus.Running), ct);
                await _db.Entry(existingExecution).ReloadAsync(ct);
                if (claimed == 0)
                    return existingExecution;
            }
        }

        // H-3 (security-audit finding): concurrent-execution capacity caps. The slot is
        // reserved as the first guarded execution step so every reservation is released
        // by the finally path below.
        var perUserCounted = false;

        using var activity = EngineActivitySource.StartActivity("workflow.execute", ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.Attributes.WorkflowId, workflow.Id.ToString());
        activity?.SetTag(TelemetryConstants.Attributes.WorkflowName, workflow.Name);
        activity?.SetTag(TelemetryConstants.Attributes.ExecutionId, executionId.ToString());
        activity?.SetTag(TelemetryConstants.Attributes.ExecutionTrigger, triggeredBy);
        activity?.SetTag(TelemetryConstants.Attributes.WorkflowCallDepth, callDepth);
        if (parentExecutionId is { } pid)
            activity?.SetTag(TelemetryConstants.Attributes.WorkflowParentExecutionId, pid.ToString());
        if (_dbProviderTag is { } dbProv)
            activity?.SetTag(TelemetryConstants.Attributes.DatabaseProvider, dbProv);

        // Push run-wide properties into the logger scope so every nested step log (and
        // any user-authored log activity) carries them. The short run_id is the first 8
        // hex chars of the execution GUID — enough to eyeball-group entries in CMTrace
        // without the full GUID noise. workflow_name gives the human-readable handle in
        // the property dump.
        var runIdShort = executionId.ToString("N")[..8];
        using var runScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["workflow_name"] = workflow.Name,
            ["run_id"] = runIdShort,
        });

        var executionStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var workflowIdTag = new KeyValuePair<string, object?>("workflow_id", workflow.Id.ToString());
        var workflowNameTag = new KeyValuePair<string, object?>("workflow_name", workflow.Name);
        var triggerTag = new KeyValuePair<string, object?>("trigger_type", triggeredBy);

        EngineMetrics.ExecutionsStarted.Add(1, workflowIdTag, workflowNameTag, triggerTag);
        EngineMetrics.ExecutionsActive.Add(1, workflowIdTag, workflowNameTag);

        var execution = CreateOrResetExecution(existingExecution, workflow, executionId, triggeredBy,
            inputParameters, startedByUserId, parentExecutionId, callDepth, activity);
        var run = new ExecutionRun(workflow, execution, activity, executionStopwatch,
            workflowIdTag, workflowNameTag, callDepth, expectedOwnerNodeId, expectedLeaseEpoch);

        // Interactive runs need the Pending→Running transition to land in the DB immediately
        // so dashboard/list views don't show "queued" while the engine is already executing.
        var persistExecutionStart = existingExecution is null || debugEnabled || !_deferRunningStateWrite || interactiveRun;
        if (persistExecutionStart)
            await _db.SaveChangesMeasuredAsync("execution.start", ct);

        // Support log: one line per run start. Only for top-level runs (callDepth==0) —
        // sub-workflows are already covered by their parent and would otherwise double up
        // in the file. The format is human-readable; exec=<8-hex> lets you visually group
        // the start and terminal lines for the same run.
        if (callDepth == 0)
            LogExecutionStartedAsSupport(execution, workflow, triggeredBy);

        // Resolve global variables ONCE per run — one DB hit for all steps. The plaintext
        // values flow into every step's Variables dict as `globals.NAME`; OutputRedactor
        // still masks them before Output/ErrorOutput leaves the activity. See
        // ResolveGlobalVariablesAsync for the optimisation that skips the DB + DPAPI work
        // when the workflow definition contains no `{{globals.` reference.
        var globalsResult = await ResolveGlobalVariablesAsync(workflow.DefinitionJson, execution.Id, ct);

        if (await FailIfUnresolvableGlobalsAsync(run, globalsResult.Unresolvable, ct))
            return execution;

        var debug = CreateDebugHandle(debugEnabled, timeoutSeconds);

        // H-4: Reserve capacity before registering runtime state. The following try/finally
        // is then the single cleanup path for capacity slots, running executions and debug handles.
        perUserCounted = CheckCapacityCaps(startedByUserId, callDepth, interactiveRun);
        try
        {
            _runningExecutions[execution.Id] = cts;
            if (debug is not null)
                _debugHandles[execution.Id] = debug;

            return await RunGraphAsync(run, inputParameters, globalsResult.Resolved, debug, cts);
        }
        catch (OperationCanceledException)
        {
            return await CompleteAsCancelledAsync(run);
        }
        catch (Exception ex)
        {
            return await CompleteAsFailedAsync(run, ex);
        }
        finally
        {
            CleanupRuntimeState(run, startedByUserId, perUserCounted);
        }
    }

    /// <summary>
    /// Phase 1: builds the execution row for a fresh run, or resets the reused Pending row
    /// (dispatch-queue path with <c>executionIdOverride</c>) back to a clean Running state.
    /// </summary>
    private WorkflowExecution CreateOrResetExecution(
        WorkflowExecution? existingExecution, Workflow workflow, Guid executionId, string triggeredBy,
        Dictionary<string, string>? inputParameters, Guid? startedByUserId, Guid? parentExecutionId,
        int callDepth, Activity? activity)
    {
        var execution = existingExecution ?? new WorkflowExecution
        {
            Id = executionId,
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = triggeredBy,
            TraceId = activity?.TraceId.ToString(),
            SpanId = activity?.SpanId.ToString(),
            // H-8 (security-audit finding): input parameter JSON may contain values
            // resolved from secrets/globals — run it through the redactor and cap it to
            // 32 KiB so a runaway caller can't blow up the DB row or the audit log.
            InputParametersJson = RedactAndCap(SerializeInputParameters(inputParameters), 32 * 1024),
            StartedByUserId = startedByUserId,
            ParentExecutionId = parentExecutionId,
            CallDepth = callDepth,
            // Cluster-failover bookkeeping: stamp the owning node so the recovery sweep
            // can distinguish our own in-flight runs from a dead leader's. Null in tests
            // that wire no cluster provider — recovery still works (NULL != self → recovered).
            OwnerNodeId = _cluster?.NodeId,
        };

        if (existingExecution is not null)
        {
            execution.WorkflowId = workflow.Id;
            execution.Status = ExecutionStatus.Running;
            execution.StartedAt = DateTime.UtcNow;
            execution.CompletedAt = null;
            execution.TriggeredBy = triggeredBy;
            execution.ErrorMessage = null;
            execution.TraceId = activity?.TraceId.ToString();
            execution.SpanId = activity?.SpanId.ToString();
            execution.InputParametersJson = RedactAndCap(SerializeInputParameters(inputParameters), 32 * 1024);
            execution.StartedByUserId = startedByUserId;
            execution.ParentExecutionId = parentExecutionId;
            execution.CallDepth = callDepth;
            execution.ReturnData = null;
        }
        else
        {
            _db.WorkflowExecutions.Add(execution);
        }

        return execution;
    }

    /// <summary>
    /// Phase 2: fail loudly when the workflow references a global that exists in the DB but
    /// can't be decrypted on this host — without this check the literal placeholder would
    /// silently leak into the activity's request payload (e.g. as the literal string
    /// "{{globals.STRIPE_KEY}}" in an Authorization header). Returns true when the run was
    /// failed and persisted (caller returns the execution as-is).
    /// </summary>
    private async Task<bool> FailIfUnresolvableGlobalsAsync(
        ExecutionRun run, IReadOnlySet<string> unresolvable, CancellationToken ct)
    {
        var brokenRefError = FindUnresolvableGlobalReferences(run.Workflow.DefinitionJson, unresolvable);
        if (brokenRefError is null) return false;

        var execution = run.Execution;
        _logger.LogError(
            "Workflow {WorkflowName} ({WorkflowId}) references unresolvable global variables in execution {ExecutionId}. {Detail}",
            run.Workflow.Name, run.Workflow.Id, execution.Id, brokenRefError);
        await PersistTerminalStateResilientAsync(
            run,
            ExecutionStatus.Failed,
            brokenRefError,
            cancelledBy: null,
            "execution.fail.unresolvable-globals");
        EngineMetrics.ExecutionsActive.Add(-1, run.WorkflowIdTag, run.WorkflowNameTag);
        EngineMetrics.ExecutionsCompleted.Add(1, run.WorkflowIdTag, run.WorkflowNameTag,
            new KeyValuePair<string, object?>("status", execution.Status.ToString()));
        return true;
    }

    /// <summary>
    /// Persists a terminal execution state with a database-side compare-and-set. The
    /// predicate is deliberately evaluated by the database, not against the tracked entity:
    /// an external offboarding writer or a failover recovery scope may already have made the
    /// row Cancelled while this DbContext still sees Running.
    /// </summary>
    private async Task<bool> PersistTerminalStateAsync(
        ExecutionRun run,
        ExecutionStatus desiredStatus,
        DateTime completedAt,
        string? errorMessage,
        string? cancelledBy,
        string operation,
        CancellationToken ct)
    {
        var execution = run.Execution;

        // Persist unrelated tracked rows (notably Skipped StepExecutions) before the CAS,
        // but never let a stale tracked execution state participate in this SaveChanges.
        var executionEntry = _db.Entry(execution);
        if (executionEntry.State == EntityState.Modified)
        {
            executionEntry.Property(candidate => candidate.Status).IsModified = false;
            executionEntry.Property(candidate => candidate.CompletedAt).IsModified = false;
            executionEntry.Property(candidate => candidate.ErrorMessage).IsModified = false;
            executionEntry.Property(candidate => candidate.CancelledBy).IsModified = false;
        }
        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesMeasuredAsync($"{operation}.related", ct);

        var candidates = _db.WorkflowExecutions
            .Where(candidate => candidate.Id == execution.Id
                             && candidate.WorkflowId == execution.WorkflowId
                             && (candidate.Status == ExecutionStatus.Running
                                 || candidate.Status == ExecutionStatus.Paused));
        candidates = ApplyExecutionWriteFence(
            _db,
            candidates,
            run.ExpectedOwnerNodeId,
            run.ExpectedLeaseEpoch);

        var updated = await WorkflowDbWriteMetrics.ExecuteMeasuredAsync(
            operation,
            () => candidates.ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, desiredStatus)
                .SetProperty(candidate => candidate.CompletedAt, completedAt)
                .SetProperty(candidate => candidate.ErrorMessage, errorMessage)
                .SetProperty(candidate => candidate.CancelledBy, cancelledBy), ct));

        // ExecuteUpdate bypasses the change tracker. Reload on both outcomes: on success it
        // makes the returned object reflect the committed values; on CAS loss it imports the
        // externally committed Cancelled state and prevents a later SaveChanges from reviving it.
        await executionEntry.ReloadAsync(CancellationToken.None);
        return updated == 1;
    }

    /// <summary>
    /// Writes the terminal execution state and GUARANTEES it lands — this is what makes an
    /// execution's completion reliable by construction. Two properties:
    ///  1) it is cancellation-independent (<see cref="CancellationToken.None"/>) — finalization is
    ///     exactly the operation that must complete even for a cancelled/timed-out run, so it must
    ///     never ride the run's own (possibly-tripped) token;
    ///  2) if the run's own <see cref="_db"/> is unusable (a prior write was cancelled/faulted and
    ///     poisoned the pooled connection, or the scope is torn down at host shutdown), the CAS is
    ///     retried on a FRESH DI scope + fresh <see cref="NodePilotDbContext"/>.
    /// The single-node CAS predicate and the cluster write-fence are preserved on BOTH attempts,
    /// so this never clobbers a row a new leader legitimately owns. The only path that can still
    /// leave a Running row is total DB unavailability on both attempts — logged loudly, and the
    /// boot reconciler terminalizes it on the next startup.
    /// </summary>
    private async Task PersistTerminalStateResilientAsync(
        ExecutionRun run, ExecutionStatus desiredStatus, string? errorMessage, string? cancelledBy, string operation)
    {
        var completedAt = DateTime.UtcNow;

        // Fast path: the run's own context (also flushes tracked Skipped StepExecutions).
        try
        {
            await PersistTerminalStateAsync(run, desiredStatus, completedAt, errorMessage, cancelledBy, operation, CancellationToken.None);
            // Committed, OR lost the CAS to an authoritative external terminal write (already terminal).
            if (IsTerminalStatus(run.Execution.Status))
                return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Terminal write for execution {ExecutionId} failed on the run context; retrying on a fresh scope",
                run.Execution.Id);
        }

        // Resilient path: a standalone CAS on a brand-new context, independent of _db's state.
        // Only the execution row is written here — Skipped-step rows are cosmetic UI state and are
        // best-effort on the fast path; the invariant that matters is that the run reaches terminal.
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var freshDb = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            var candidates = freshDb.WorkflowExecutions
                .Where(c => c.Id == run.Execution.Id
                         && c.WorkflowId == run.Execution.WorkflowId
                         && (c.Status == ExecutionStatus.Running || c.Status == ExecutionStatus.Paused));
            candidates = ApplyExecutionWriteFence(freshDb, candidates, run.ExpectedOwnerNodeId, run.ExpectedLeaseEpoch);
            var updated = await candidates.ExecuteUpdateAsync(setters => setters
                .SetProperty(c => c.Status, desiredStatus)
                .SetProperty(c => c.CompletedAt, completedAt)
                .SetProperty(c => c.ErrorMessage, errorMessage)
                .SetProperty(c => c.CancelledBy, cancelledBy), CancellationToken.None);
            if (updated == 1)
            {
                // Reflect the committed values on the tracked entity so the caller's notify + logs
                // see the true terminal state.
                run.Execution.Status = desiredStatus;
                run.Execution.CompletedAt = completedAt;
                run.Execution.ErrorMessage = errorMessage;
                run.Execution.CancelledBy = cancelledBy;
            }
            else
            {
                // 0 rows: the row is already terminal (a concurrent authoritative write won) or the
                // fence legitimately rejected us (another cluster owner). Import the committed state.
                var current = await freshDb.WorkflowExecutions.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == run.Execution.Id, CancellationToken.None);
                if (current is not null)
                {
                    run.Execution.Status = current.Status;
                    run.Execution.CompletedAt = current.CompletedAt;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Guaranteed terminal write for execution {ExecutionId} failed on a fresh scope too; the row may remain Running until the next startup reconcile",
                run.Execution.Id);
        }
    }

    private static IQueryable<WorkflowExecution> ApplyExecutionWriteFence(
        NodePilotDbContext db,
        IQueryable<WorkflowExecution> candidates,
        string? expectedOwnerNodeId,
        long expectedLeaseEpoch)
    {
        if (expectedOwnerNodeId is not null)
            candidates = candidates.Where(candidate => candidate.OwnerNodeId == expectedOwnerNodeId);

        // Epoch 0 is the single-node sentinel. In HA, validate owner, epoch and lease expiry
        // through an EXISTS predicate in the same UPDATE statement as the execution write.
        // A process paused by GC across a handoff therefore cannot commit after it resumes,
        // even if its in-memory IClusterStateProvider has not observed leadership loss yet.
        if (expectedLeaseEpoch > 0 && expectedOwnerNodeId is not null)
        {
            candidates = candidates.Where(_ => db.ClusterLeaders.Any(leader =>
                leader.Resource == "primary"
                && leader.OwnerNodeId == expectedOwnerNodeId
                && leader.LeaseEpoch == expectedLeaseEpoch
                // Keep UtcNow inside the expression tree. EF translates it to the
                // provider's database clock (GETUTCDATE/current_timestamp/strftime),
                // avoiding host-clock skew in the fencing predicate.
                && leader.ExpiresAt > DateTime.UtcNow));
        }

        return candidates;
    }

    /// <summary>
    /// Phase 3: linked CTS for the whole run, with the optional caller timeout as a ceiling.
    /// Trips the same token the engine uses for graceful step cancellation — step activities
    /// finish their current I/O, then the main loop observes the cancel and aggregates status
    /// as "Cancelled".
    /// </summary>
    private static CancellationTokenSource CreateRunCancellation(CancellationToken ct, int? timeoutSeconds)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // M-10 (security-audit finding): clamp to a 7-day maximum. Otherwise an untrusted
        // caller (webhook / external trigger) could pin a cancellation token forever and
        // leak tokens + DB rows.
        const int MaxTimeoutSeconds = 7 * 24 * 60 * 60; // 604800 = 7d
        if (timeoutSeconds is > 0)
        {
            var effectiveTimeout = timeoutSeconds.Value > MaxTimeoutSeconds
                ? MaxTimeoutSeconds : timeoutSeconds.Value;
            cts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeout));
        }
        return cts;
    }

    /// <summary>
    /// Makes an execution cancellable before its Pending row is claimed. This closes the
    /// in-memory/database hand-off gap: an offboarding writer either cancels Pending before
    /// the compare-and-set, or observes/signals this token after the engine starts claiming it.
    /// </summary>
    private static DispatchClaimRegistration? TryRegisterDispatchClaim(
        Guid executionId,
        CancellationTokenSource cts)
    {
        return _runningExecutions.TryAdd(executionId, cts)
            ? new DispatchClaimRegistration(executionId, cts)
            : null;
    }

    private sealed class DispatchClaimRegistration(
        Guid executionId,
        CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            if (_runningExecutions.TryGetValue(executionId, out var current)
                && ReferenceEquals(current, cts))
                _runningExecutions.TryRemove(executionId, out _);
        }
    }

    /// <summary>
    /// Phase 4: only creates a debug handle when the caller explicitly passes
    /// debugEnabled=true — breakpoints in the workflow JSON are consistently ignored on
    /// regular runs (Test/Scheduler/Webhook). The max-pause window comes from config;
    /// default 10 minutes, as a guard against zombie executions (e.g. the user closing
    /// their browser tab).
    /// </summary>
    private DebugHandle? CreateDebugHandle(bool debugEnabled, int? timeoutSeconds)
    {
        if (!debugEnabled) return null;

        // L-9 (security-audit finding): cap concurrent debug sessions so a malicious or
        // buggy caller can't exhaust memory by starting unlimited paused runs.
        const int MaxConcurrentDebugSessions = 50;
        if (_debugHandles.Count >= MaxConcurrentDebugSessions)
        {
            throw new InvalidOperationException(
                $"Maximum concurrent debug sessions ({MaxConcurrentDebugSessions}) reached. " +
                "Resume or cancel pending debug runs before starting a new one.");
        }
        return new DebugHandle
        {
            MaxPauseMinutes = _configuration?.GetValue("Engine:Debug:MaxPauseMinutes", 10) ?? 10,
            OriginalTimeoutSeconds = timeoutSeconds,
        };
    }

    /// <summary>
    /// Phase 5 (the run itself): compile the definition, verify the graph has an entry point,
    /// drive the event-driven scheduler, then persist the skipped/terminal state and emit the
    /// SignalR + telemetry signals. Cancellation and unexpected failures propagate to the
    /// orchestrator's catch handlers.
    /// </summary>
    private async Task<WorkflowExecution> RunGraphAsync(
        ExecutionRun run,
        Dictionary<string, string>? inputParameters,
        IReadOnlyDictionary<string, string> globalVariables,
        DebugHandle? debug,
        CancellationTokenSource cts)
    {
        var workflow = run.Workflow;
        var execution = run.Execution;
        var activity = run.Activity;

        var compiledDefinition = WorkflowDefinitionCache.GetOrCompile(workflow);
        var nodes = compiledDefinition.Nodes;

        // id → node lookup built ONCE per execution. Scheduling, variable resolution,
        // and edge-condition evaluation all need "give me the node with id X"; the
        // previous List.FirstOrDefault loop was O(nodes) per lookup and dominated the
        // CPU time of wide fan-outs. The dict is a single pass + dict allocation.
        var nodesById = compiledDefinition.NodesById;
        var outputNameByStepId = compiledDefinition.OutputNameByStepId;
        var outputVariableToStepId = compiledDefinition.OutputVariableToStepId;

        // Reuse the compiled graph shape for this workflow version. Per-run state
        // stays below in results/completed/skipped; these graph collections are read-only.
        var adjacency = compiledDefinition.Adjacency;
        var reverseAdjacency = compiledDefinition.ReverseAdjacency;
        var incomingEdgesByTarget = compiledDefinition.IncomingEdgesByTarget;
        var activeEdgeByEndpoints = compiledDefinition.ActiveEdgeByEndpoints;
        var rootNodes = compiledDefinition.RootNodes;

        // Root selection and disabled-node/edge semantics are owned by
        // WorkflowDefinitionDocument. The engine only handles the execution-specific
        // failure mode when the compiled graph has no entry point.
        if (nodes.Count > 0 && rootNodes.Count == 0)
        {
            _logger.LogWarning(
                "Workflow {WorkflowId} has no root nodes — no enabled trigger/start activity (or only cycles). Marking execution as Failed.",
                workflow.Id);
            var errorMessage = "Workflow graph has no root nodes — it has no trigger / start activity (or the only trigger is disabled, or the remaining nodes form a cycle). Add a manual, schedule, webhook, file-watcher, database, or event-log trigger as the entry point.";
            await PersistTerminalStateResilientAsync(
                run,
                ExecutionStatus.Failed,
                errorMessage,
                cancelledBy: null,
                "execution.no_roots");
            if (IsTerminalStatus(execution.Status))
            {
                await _notifier.ExecutionStatusChangedAsync(execution.Id, execution.WorkflowId,
                    execution.Status, execution.ErrorMessage, execution.CompletedAt);
            }
            else
            {
                LogTerminalWriteFenced(run);
            }
            return execution;
        }

        // Execute using event-driven scheduling: as soon as ONE in-flight step completes,
        // evaluate its successors and enqueue ready ones. This enables true waitAny racing
        // (vs. the previous batch-WhenAll approach that waited for the slowest sibling).
        //
        // ConcurrentDictionary (not Dictionary): parallel branches can simultaneously read
        // results (via VariableResolver in StepRunner) while WorkflowScheduler writes the
        // result of a freshly-completed step. Plain Dictionary throws "Collection was
        // modified" on the reader side under that race; ConcurrentDictionary's enumerator
        // returns a moment-in-time snapshot that's safe under concurrent mutation.
        var results = new ConcurrentDictionary<string, ActivityResult>();
        var completed = new HashSet<string>();
        var skipped = new HashSet<string>();

        // Initial "Running" signal. The engine otherwise only notifies on terminal states, so
        // the live-ops feed could only ever REMOVE a workflow, never add one — short-lived runs
        // (especially sub-workflow children that start+finish between snapshot polls) would never
        // appear. Fired for top-level AND child executions (execution.WorkflowId is the child's
        // id for sub-workflows), right before scheduling so every path out of the try below still
        // emits a matching terminal event. Dropped cheaply by the notifier when nobody subscribes.
        await _notifier.ExecutionStatusChangedAsync(execution.Id, execution.WorkflowId, ExecutionStatus.Running, null, null);

        await WorkflowScheduler.RunAsync(rootNodes, nodesById, adjacency, reverseAdjacency,
            incomingEdgesByTarget, activeEdgeByEndpoints, outputVariableToStepId,
            results, completed, skipped,
            (node, stepCt) => _stepRunner.ExecuteAsync(execution, workflow.Name, node, results,
                outputNameByStepId, outputVariableToStepId,
                inputParameters, globalVariables, compiledDefinition.RetryPolicies, debug, cts, stepCt),
            _logger,
            cts.Token,
            globalVariables,
            inputParameters);

        // The scheduler can return normally even when the run was cancelled (it lets in-flight
        // steps observe the token and wind down rather than always throwing). Surface that cancel
        // here so it routes to the Cancelled terminal handler instead of being aggregated as
        // Succeeded/Failed. This was previously IMPLICIT — the verdict COUNT query below ran on
        // cts.Token and threw on a cancelled token — but finalization is now cancellation-
        // independent (None), so the cancel check must be explicit.
        cts.Token.ThrowIfCancellationRequested();

        // Anything that wasn't completed and isn't already in `skipped` is unreachable
        // (typically: only reachable via a disabled edge). Mark it as skipped so the UI
        // and downstream logic see a definitive state.
        foreach (var n in nodes)
        {
            if (!completed.Contains(n.Id)) skipped.Add(n.Id);
        }

        // Persist Skipped status for nodes that never ran + emit SignalR events so the
        // UI can color them grey/dashed live.
        var skippedForNotify = new List<(string id, string? label, string? type)>();
        foreach (var skippedId in skipped.Where(id => !completed.Contains(id)))
        {
            var skippedNode = nodesById[skippedId];
            var stepExec = new StepExecution
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = execution.Id,
                StepId = skippedNode.Id,
                StepName = skippedNode.Data.Label ?? skippedNode.Id,
                StepType = skippedNode.Type ?? string.Empty,
                Status = ExecutionStatus.Skipped,
                StartedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
            };
            _db.StepExecutions.Add(stepExec);
            skippedForNotify.Add((skippedNode.Id, skippedNode.Data.Label, skippedNode.Type));
        }
        // Determine overall status. Using Count instead of Any: it uses the same query
        // plan (an index seek on WorkflowExecutionId), but also gives us the exact
        // failure count for the support log without a second round-trip.
        var failedStepCount = await _db.StepExecutions
            .AsNoTracking()
            .CountAsync(s => s.WorkflowExecutionId == execution.Id && s.Status == ExecutionStatus.Failed, CancellationToken.None);

        var desiredStatus = failedStepCount > 0 ? ExecutionStatus.Failed : ExecutionStatus.Succeeded;
        await PersistTerminalStateResilientAsync(
            run,
            desiredStatus,
            errorMessage: null,
            cancelledBy: null,
            "execution.terminal");

        if (run.CallDepth == 0 && IsTerminalStatus(execution.Status))
            LogExecutionTerminatedAsSupport(execution, workflow,
                run.Stopwatch.Elapsed,
                okCount: Math.Max(0, completed.Count - failedStepCount),
                failCount: failedStepCount,
                skipCount: skippedForNotify.Count);

        // Notify SignalR after the skipped rows and terminal execution state are persisted.
        foreach (var (id, label, type) in skippedForNotify)
        {
            await _notifier.StepCompletedAsync(execution.Id, execution.WorkflowId, id, label,
                ExecutionStatus.Skipped, null, null, DateTime.UtcNow, stepType: type);
        }

        if (IsTerminalStatus(execution.Status))
        {
            await _notifier.ExecutionStatusChangedAsync(
                execution.Id, execution.WorkflowId, execution.Status, execution.ErrorMessage, execution.CompletedAt);
        }
        else
        {
            LogTerminalWriteFenced(run);
        }

        activity?.SetTag(TelemetryConstants.Attributes.ExecutionStatus, execution.Status.ToString());
        if (execution.Status == ExecutionStatus.Failed)
            activity?.SetStatus(ActivityStatusCode.Error, "one or more steps failed");
        else if (execution.Status == ExecutionStatus.Succeeded)
            activity?.SetStatus(ActivityStatusCode.Ok);
        else
            activity?.SetStatus(ActivityStatusCode.Error, "terminal write fenced by execution ownership/leader lease");

        var executedCount = completed.Count;
        var skippedCount = skippedForNotify.Count;
        EngineMetrics.ExecutionNodesExecuted.Record(executedCount, run.WorkflowIdTag, run.WorkflowNameTag);
        EngineMetrics.ExecutionNodesSkipped.Record(skippedCount, run.WorkflowIdTag, run.WorkflowNameTag);

        return execution;
    }

    /// <summary>
    /// Terminal handler for a cancelled run (user cancel, timeout ceiling, host shutdown).
    /// </summary>
    private async Task<WorkflowExecution> CompleteAsCancelledAsync(ExecutionRun run)
    {
        var execution = run.Execution;
        // Attribute the cancel: an explicit reason recorded by CancelAsync (e.g. "user") wins;
        // otherwise this is a timeout / host-shutdown / bare-token cancel → "system".
        var cancelledBy = _cancelReasons.TryRemove(execution.Id, out var cancelReason) ? cancelReason : "system";
        await PersistTerminalStateResilientAsync(
            run,
            ExecutionStatus.Cancelled,
            errorMessage: null,
            cancelledBy,
            "execution.cancelled");
        if (IsTerminalStatus(execution.Status))
        {
            await _notifier.ExecutionStatusChangedAsync(
                execution.Id, execution.WorkflowId, execution.Status, execution.ErrorMessage, execution.CompletedAt);
        }
        else
        {
            LogTerminalWriteFenced(run);
        }
        run.Activity?.SetTag(TelemetryConstants.Attributes.ExecutionStatus, execution.Status.ToString());
        run.Activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
        EngineMetrics.Cancellations.Add(1, run.WorkflowIdTag, run.WorkflowNameTag, new KeyValuePair<string, object?>("reason", "user_or_token"));
        if (run.CallDepth == 0 && IsTerminalStatus(execution.Status))
            LogExecutionTerminatedAsSupport(execution, run.Workflow, run.Stopwatch.Elapsed, 0, 0, 0);
        return execution;
    }

    /// <summary>
    /// Terminal handler for an unexpected engine-level failure (step failures are aggregated
    /// in <see cref="RunGraphAsync"/>; this catches everything that escaped the scheduler).
    /// </summary>
    private async Task<WorkflowExecution> CompleteAsFailedAsync(ExecutionRun run, Exception ex)
    {
        var execution = run.Execution;
        _logger.LogError(ex, "Workflow execution {ExecutionId} failed", execution.Id);
        // H-8/H-9 (security-audit findings): redact + cap — the exception may carry a
        // leaked secret from a child activity (e.g. an HTTP body echoed back in a
        // deserialization error).
        var errorMessage = RedactAndCap(ex.Message, 32 * 1024);
        await PersistTerminalStateResilientAsync(
            run,
            ExecutionStatus.Failed,
            errorMessage,
            cancelledBy: null,
            "execution.failed");
        if (IsTerminalStatus(execution.Status))
        {
            await _notifier.ExecutionStatusChangedAsync(
                execution.Id, execution.WorkflowId, execution.Status, execution.ErrorMessage, execution.CompletedAt);
        }
        else
        {
            LogTerminalWriteFenced(run);
        }
        run.Activity?.SetTag(TelemetryConstants.Attributes.ExecutionStatus, execution.Status.ToString());
        run.Activity?.SetStatus(ActivityStatusCode.Error,
            execution.Status == ExecutionStatus.Cancelled ? "cancelled" : ex.Message);
        run.Activity?.AddException(ex);
        if (run.CallDepth == 0 && IsTerminalStatus(execution.Status))
            LogExecutionTerminatedAsSupport(execution, run.Workflow, run.Stopwatch.Elapsed, 0, 1, 0);
        return execution;
    }

    private static bool IsTerminalStatus(ExecutionStatus status) =>
        status is ExecutionStatus.Succeeded or ExecutionStatus.Failed or ExecutionStatus.Cancelled;

    private void LogTerminalWriteFenced(ExecutionRun run)
    {
        _logger.LogWarning(
            "Terminal write for execution {ExecutionId} was fenced; persisted status remains {Status}, expectedOwner={ExpectedOwner}, expectedLeaseEpoch={ExpectedEpoch}",
            run.Execution.Id,
            run.Execution.Status,
            run.ExpectedOwnerNodeId,
            run.ExpectedLeaseEpoch);
    }

    /// <summary>
    /// The single cleanup path (finally): releases the capacity slot, drops the per-run
    /// runtime state and records the run-duration metrics with the final status.
    /// </summary>
    private static void CleanupRuntimeState(ExecutionRun run, Guid? startedByUserId, bool perUserCounted)
    {
        var execution = run.Execution;
        _runningExecutions.TryRemove(execution.Id, out _);
        // Cancel-reason hint is single-use; drop any leftover (e.g. cancel signalled but the run
        // finished normally first) so the dict can't grow unbounded across many executions.
        _cancelReasons.TryRemove(execution.Id, out _);
        // Clean up the debug handle together with the cancellation token source —
        // otherwise memory leaks per execution. Steps that are still paused (edge case:
        // an engine exception while paused) are not automatically released by this
        // TryRemove, but they're already dead anyway because the parent try block has
        // aborted.
        _debugHandles.TryRemove(execution.Id, out _);
        // H-3 (security-audit finding): roll the per-user counter back to its value
        // before this run. With multiple parallel runs from the same user we count down;
        // on the last run, the entry that reaches 0 is explicitly removed from the dict
        // so it doesn't grow unbounded across many distinct users (TryRemove on the
        // key/value pair is atomic against a concurrent increment that might have
        // already bumped a 0-entry to 1).
        ReleaseCapacitySlot(startedByUserId, perUserCounted);

        run.Stopwatch.Stop();
        var statusTag = new KeyValuePair<string, object?>("status", execution.Status.ToString());
        EngineMetrics.ExecutionsActive.Add(-1, run.WorkflowIdTag, run.WorkflowNameTag);
        EngineMetrics.ExecutionsCompleted.Add(1, run.WorkflowIdTag, run.WorkflowNameTag, statusTag);
        EngineMetrics.ExecutionDuration.Record(run.Stopwatch.Elapsed.TotalMilliseconds, run.WorkflowIdTag, run.WorkflowNameTag, statusTag);
    }

    public Task<bool> CancelAsync(Guid executionId, string? cancelledBy = null, CancellationToken ct = default)
    {
        if (!_runningExecutions.TryGetValue(executionId, out var cts))
            return Task.FromResult(false);
        // Record attribution BEFORE cancelling so the engine's OCE catch (which runs on the engine
        // thread, possibly before this method returns) sees it. Default "user": CancelAsync's only
        // callers are explicit human/API cancels; timeouts trip the token directly (no reason set).
        _cancelReasons[executionId] = cancelledBy ?? "user";
        // If the execution is currently paused in a debug session: first resolve every
        // paused step's signal as Stop, otherwise the engine thread stays blocked in its
        // await until the pause guard's timeout eventually fires. Cancel normally after
        // that — the main loop takes care of propagating Skipped status downstream.
        if (_debugHandles.TryGetValue(executionId, out var debug))
            debug.ReleaseAllAsStop();
        // ReleaseAllAsStop unblocks the engine-thread, which may race ahead and dispose the
        // CTS (in the finally-block of ExecuteAsync) before we get here. Treat that as
        // "cancellation already handled" — caller still gets true because the engine WILL
        // wind down to Cancelled status either way.
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* CTS already disposed by ExecuteAsync's finally — race is benign */ }
        return Task.FromResult(true);
    }

    /// <summary>
    /// Cancels every workflow execution this process is currently running. Used by the
    /// cluster-leader fencing path: when this node loses the lease, its in-flight runs must
    /// stop touching the DB so the new leader's recovery sweep can adopt the orphaned rows
    /// safely. Returns the number of executions whose CTS was actually triggered.
    /// <para>
    /// Static because <see cref="_runningExecutions"/> is process-wide static state — the
    /// caller (singleton hosted service) doesn't need a scoped engine instance to fence.
    /// </para>
    /// </summary>
    public static Task<int> CancelAllLocalAsync(CancellationToken ct = default)
    {
        var snapshot = _runningExecutions.ToArray();
        var count = 0;
        foreach (var kvp in snapshot)
        {
            if (_debugHandles.TryGetValue(kvp.Key, out var debug))
                debug.ReleaseAllAsStop();
            try { kvp.Value.Cancel(); count++; }
            catch (ObjectDisposedException) { /* CTS already disposed by ExecuteAsync's finally — race is benign */ }
        }
        return Task.FromResult(count);
    }

    /// <summary>
    /// Resume command for an execution paused at a breakpoint. Delegates to the
    /// <see cref="DebugHandle"/>, which holds a per-step signal — in parallel branches,
    /// each branch can be resumed independently of the others.
    /// Returns false when a) the execution isn't in memory (e.g. after an API restart —
    /// the row was persisted in the meantime and is now orphaned), or b) no step with
    /// that ID is currently paused and waiting.
    /// </summary>
    public bool Resume(Guid executionId, string stepId, DebugResumeCommand command,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (!_debugHandles.TryGetValue(executionId, out var debug)) return false;
        var engineCmd = command switch
        {
            DebugResumeCommand.Continue => ResumeCommand.Continue,
            DebugResumeCommand.StepOver => ResumeCommand.StepOver,
            DebugResumeCommand.Stop => ResumeCommand.Stop,
            _ => ResumeCommand.Continue,
        };
        return debug.Resume(stepId, new ResumeRequest(engineCmd, overrides));
    }

    /// <summary>Returns the IDs of all steps of an execution that are currently paused.
    /// Used by the REST controller to release every pending step at once for a
    /// "Resume all" action.</summary>
    public IReadOnlyCollection<string> GetPausedSteps(Guid executionId)
        => _debugHandles.TryGetValue(executionId, out var debug)
            ? debug.PendingSteps.ToList()
            : Array.Empty<string>();
    private static string? SerializeInputParameters(Dictionary<string, string>? inputParameters)
    {
        if (inputParameters is null || inputParameters.Count == 0) return null;
        var filtered = inputParameters
            .Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (filtered.Count == 0) return null;
        return JsonSerializer.Serialize(filtered);
    }
}

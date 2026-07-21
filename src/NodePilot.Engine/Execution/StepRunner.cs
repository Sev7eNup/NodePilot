using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Activities;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Debug;
using NodePilot.Engine.Security;
using NodePilot.Engine.Telemetry;
using NodePilot.Core.Telemetry;

namespace NodePilot.Engine.Execution;

internal sealed class StepRunner
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IExecutionNotifier _notifier;
    private readonly ILogger _logger;
    private readonly OutputRedactor _redactor;
    private readonly DebugCoordinator _debugCoordinator;
    private readonly bool _stepDetailEnabled;
    private readonly int _stepDetailMaxChars;
    private readonly bool _deferRunningStateWrite;

    // H-1 (security-audit finding): top-level config fields that must NOT be touched by the {{var}} resolver.
    // The activity executor then enforces "no {{...}}" on the passthrough text so an
    // upstream change to the field's contract still fails closed instead of leaking
    // attacker-controlled substrings into a raw CommandText.
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> FieldsNotToResolve =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sql"] = new HashSet<string>(StringComparer.Ordinal) { "query" },
            ["databaseTrigger"] = new HashSet<string>(StringComparer.Ordinal) { "query" },
            // waitForCondition embeds the raw `script` text into a PowerShell `[bool](...)`
            // cast. A template-resolved value would land unquoted inside a PS expression,
            // letting an upstream output close the cast and inject arbitrary PS. The
            // activity rejects `{{...}}` residue itself — same model as sql.query.
            ["waitForCondition"] = new HashSet<string>(StringComparer.Ordinal) { "script" },
        };

    internal static JsonElement ResolveConfigForExecution(
        string? activityType,
        JsonElement config,
        IReadOnlyDictionary<string, ActivityResult> previousResults,
        IReadOnlyDictionary<string, string> outputVariableToStepId,
        IReadOnlyDictionary<string, string> globalVariables)
    {
        // runScript AND custom:<key> activities resolve {{...}} inside their own executor with
        // PowerShell-safe single-quote quoting, so the generic resolver must leave their config
        // verbatim (a custom activity is a runScript preset).
        if (string.Equals(activityType, "runScript", StringComparison.OrdinalIgnoreCase)
            || CustomActivityType.IsCustomType(activityType))
        {
            return config;
        }

        if (FieldsNotToResolve.TryGetValue(activityType ?? string.Empty, out var protectedFields))
        {
            return VariableResolver.ResolveVariablesExcept(
                config, previousResults, outputVariableToStepId, globalVariables, protectedFields);
        }

        return VariableResolver.ResolveVariables(
            config, previousResults, outputVariableToStepId, globalVariables);
    }

    internal StepRunner(
        IServiceProvider serviceProvider,
        IExecutionNotifier notifier,
        ILogger logger,
        OutputRedactor redactor,
        bool stepDetailEnabled,
        int stepDetailMaxChars,
        bool deferRunningStateWrite)
    {
        _serviceProvider = serviceProvider;
        _notifier = notifier;
        _logger = logger;
        _redactor = redactor;
        _debugCoordinator = new DebugCoordinator(redactor, notifier);
        _stepDetailEnabled = stepDetailEnabled;
        _stepDetailMaxChars = stepDetailMaxChars;
        _deferRunningStateWrite = deferRunningStateWrite;
    }

    internal async Task<ActivityResult> ExecuteAsync(
        WorkflowExecution execution,
        string workflowName,
        WorkflowNode node,
        IReadOnlyDictionary<string, ActivityResult> previousResults,
        IReadOnlyDictionary<string, string> outputNameByStepId,
        IReadOnlyDictionary<string, string> outputVariableToStepId,
        Dictionary<string, string>? inputParameters,
        IReadOnlyDictionary<string, string> globalVariables,
        IReadOnlyDictionary<string, RetryPolicy> retryPolicies,
        DebugHandle? debug,
        CancellationTokenSource executionCts,
        CancellationToken ct)
    {
        var stepStopwatch = Stopwatch.StartNew();
        var activityTypeTag = new KeyValuePair<string, object?>("activity_type", node.Type);
        var finalStatus = "Failed";

        using var stepActivity = WorkflowEngine.EngineActivitySource.StartActivity("workflow.step", ActivityKind.Internal);
        stepActivity?.SetTag(TelemetryConstants.Attributes.StepId, node.Id);
        stepActivity?.SetTag(TelemetryConstants.Attributes.StepName, node.Data.Label ?? node.Id);
        stepActivity?.SetTag(TelemetryConstants.Attributes.StepActivityType, node.Type);
        stepActivity?.SetTag(TelemetryConstants.Attributes.ExecutionId, execution.Id.ToString());
        stepActivity?.SetTag(TelemetryConstants.Attributes.WorkflowId, execution.WorkflowId.ToString());

        using var stepLogScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["step_id"] = node.Id,
            ["step_label"] = node.Data.Label ?? node.Id,
            ["activity_type"] = node.Type,
        });

        await using var stepScope = _serviceProvider.CreateAsyncScope();
        var stepDb = stepScope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        // ActivityRegistry is a singleton — the resolve below returns the same instance
        // across every step. The actual executor is resolved from the per-step scope
        // inside GetExecutor below.
        var stepRegistry = stepScope.ServiceProvider.GetRequiredService<ActivityRegistry>();

        var resolvedTargetMachine = VariableResolver.ResolveStringValue(node.Data.TargetMachineRaw, previousResults, outputVariableToStepId, globalVariables);
        var resolvedCredential = VariableResolver.ResolveStringValue(node.Data.CredentialRaw, previousResults, outputVariableToStepId, globalVariables);

        var resolvedMachine = await MachineResolver.ResolveAsync(stepDb, resolvedTargetMachine, _logger, ct);
        var targetMachineId = resolvedMachine?.Id != Guid.Empty ? resolvedMachine?.Id : null;
        var credentialId = Guid.TryParse(resolvedCredential, out var credGuid) ? credGuid : (Guid?)null;

        if (!string.IsNullOrEmpty(resolvedTargetMachine))
            stepActivity?.SetTag(TelemetryConstants.Attributes.StepTargetMachine, resolvedTargetMachine);
        stepActivity?.SetTag(TelemetryConstants.Attributes.StepHasCredential, credentialId.HasValue || resolvedMachine?.DefaultCredentialId is not null);
        if (!string.IsNullOrEmpty(node.Data.OutputVariable))
            stepActivity?.SetTag(TelemetryConstants.Attributes.StepOutputVariable, node.Data.OutputVariable);

        var stepExecution = new StepExecution
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            StepId = node.Id,
            StepName = node.Data.Label ?? node.Id,
            StepType = node.Type ?? string.Empty,
            TargetMachine = resolvedTargetMachine,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow
        };

        stepDb.StepExecutions.Add(stepExecution);
        if (!_deferRunningStateWrite)
            await stepDb.SaveChangesMeasuredAsync("step.running", ct);

        await _notifier.StepStartedAsync(execution.Id, execution.WorkflowId, node.Id, node.Data.Label, node.Type ?? "unknown", stepExecution.StartedAt ?? DateTime.UtcNow);

        try
        {
            var executor = stepRegistry.GetExecutor(node.Type ?? "unknown", stepScope.ServiceProvider);
            var variables = VariableResolver.BuildStepVariables(inputParameters, globalVariables, previousResults, outputNameByStepId);

            var context = new StepExecutionContext
            {
                WorkflowExecutionId = execution.Id,
                StepId = node.Id,
                // StepLabel and WorkflowName are filled in here so that user log activities
                // can write a human-readable identifier to the support log without needing
                // a DB lookup (the plain-text sink doesn't render logger-scope properties).
                StepLabel = node.Data.Label ?? node.Id,
                WorkflowName = workflowName,
                TargetMachineId = targetMachineId,
                CredentialId = credentialId,
                Variables = variables,
                ResolvedMachine = resolvedMachine,
                PreviousResults = previousResults,
                OutputVariableToStepId = outputVariableToStepId.Count > 0 ? outputVariableToStepId : null,
                GlobalVariables = globalVariables.Count > 0 ? globalVariables : null,
                InputParameters = inputParameters,
            };

            // H-1 (security audit 2026-05-15): activities whose config carries a raw
            // SQL/script text are dangerous to template-expand — a substituted
            // {{manual.userId}} would land in CommandText. Those fields are listed below
            // and routed through ResolveVariablesExcept, which substitutes every OTHER
            // field but passes the protected ones through verbatim. The activity executor
            // then rejects the call if the protected text still carries {{...}} residue.
            var configForExecution = ResolveConfigForExecution(
                node.Type, node.Data.Config, previousResults, outputVariableToStepId, globalVariables);

            // T-7.1: Fail the step early when step-pattern placeholders survive resolution
            // unsubstituted. This surfaces typos, deleted-step references, and wrong
            // outputVariable names as a clear error instead of silently passing the literal
            // {{...}} string to the activity. runScript resolves its own variables inside
            // the executor (different quoting semantics), so it is exempt from this check.
            if (!string.Equals(node.Type, "runScript", StringComparison.OrdinalIgnoreCase)
                && !CustomActivityType.IsCustomType(node.Type))
            {
                var unresolved = FindUnresolvedStepReferences(node.Type, configForExecution);
                if (unresolved.Count > 0)
                {
                    throw new InvalidOperationException(
                        FormatUnresolvedDiagnostic(unresolved, previousResults, outputVariableToStepId));
                }
            }

            if (debug is not null && (node.Data.Breakpoint || debug.StepOverArmed))
            {
                var shouldPause = ShouldPauseForDebug(node, previousResults, outputVariableToStepId, globalVariables, debug);
                if (shouldPause)
                    await _debugCoordinator.HandlePauseAsync(execution, node, stepExecution, stepDb, variables, debug, executionCts, ct);
            }

            var (result, attemptsUsed) = await RunWithRetryAsync(node, executor, context, configForExecution, resolvedTargetMachine, retryPolicies, ct);
            stepExecution.AttemptCount = attemptsUsed;

            var sanitized = _redactor.Redact(result);
            stepExecution.Status = result.Success ? ExecutionStatus.Succeeded : ExecutionStatus.Failed;
            stepExecution.Output = TruncateForPersist(sanitized.Output);
            stepExecution.ErrorOutput = TruncateForPersist(sanitized.ErrorOutput);
            stepExecution.TraceOutput = TruncateForPersist(sanitized.TraceOutput);
            // Persist OutputParameters as JSON so step-test/replay can reconstruct
            // {{step.param.x}} from a past run, and Coverage can answer "did this output
            // ever fire?". Only emit when non-empty to save row width on activities like
            // delay/log that never produce params. Already redacted by _redactor.Redact.
            stepExecution.OutputParametersJson = sanitized.OutputParameters is { Count: > 0 } op
                ? TruncateForPersist(JsonSerializer.Serialize(op))
                : null;
            // Reproducibility snapshot for custom-activity steps: which definition key/version/hash
            // actually ran. Survives latest-wins edits + rollbacks of the live definition. Provenance
            // carries no secrets, but it still flows through _redactor.Redact (which rebuilds the
            // result), so read it from `sanitized`.
            if (sanitized.CustomActivity is { } prov)
            {
                stepExecution.CustomActivityKey = prov.Key;
                stepExecution.CustomActivityVersion = prov.Version;
                stepExecution.CustomActivityHash = prov.Hash;
            }
            stepExecution.CompletedAt = DateTime.UtcNow;
            await stepDb.SaveChangesMeasuredAsync("step.terminal", ct);

            stepActivity?.SetTag(TelemetryConstants.Attributes.StepStatus, stepExecution.Status.ToString());
            if (result.Success)
                stepActivity?.SetStatus(ActivityStatusCode.Ok);
            else
                stepActivity?.SetStatus(ActivityStatusCode.Error, sanitized.ErrorOutput);

            finalStatus = stepExecution.Status.ToString();
            LogStepDetail(execution, node, sanitized, stepStopwatch.Elapsed, resolvedTargetMachine);
            if (!result.Success)
                LogStepFailedAsSupport(execution, node, sanitized.ErrorOutput);

            await _notifier.StepCompletedAsync(execution.Id, execution.WorkflowId, node.Id, node.Data.Label,
                stepExecution.Status, sanitized.Output, sanitized.ErrorOutput, stepExecution.CompletedAt.Value,
                sanitized.OutputParameters.Count > 0 ? sanitized.OutputParameters : null,
                sanitized.TraceOutput, node.Type, stepExecution.StartedAt, node.Data.OutputVariable);

            return result;
        }
        catch (OperationCanceledException) when (!executionCts.IsCancellationRequested && ct.IsCancellationRequested)
        {
            const string message = "Step cancelled because another branch already satisfied the workflow junction.";
            stepExecution.Status = ExecutionStatus.Cancelled;
            stepExecution.ErrorOutput = message;
            stepExecution.CompletedAt = DateTime.UtcNow;
            // DB write in error path: swallow any secondary failure — a cancelled step must
            // never abort the scheduler loop by propagating a DB exception.
            try { await stepDb.SaveChangesMeasuredAsync("step.cancelled", CancellationToken.None); }
            catch (Exception dbEx) { _logger.LogWarning(dbEx, "Failed to persist Cancelled status for step {StepId}.", node.Id); }

            stepActivity?.SetTag(TelemetryConstants.Attributes.StepStatus, "Cancelled");
            stepActivity?.SetStatus(ActivityStatusCode.Ok);
            finalStatus = "Cancelled";

            LogStepDetail(execution, node,
                new ActivityResult { Success = false, ErrorOutput = message },
                stepStopwatch.Elapsed, resolvedTargetMachine);

            await _notifier.StepCompletedAsync(execution.Id, execution.WorkflowId, node.Id, node.Data.Label,
                ExecutionStatus.Cancelled, null, message, stepExecution.CompletedAt.Value,
                stepType: node.Type, startedAt: stepExecution.StartedAt, outputVariable: node.Data.OutputVariable);

            return new ActivityResult { Success = false, ErrorOutput = message };
        }
        catch (Exception ex)
        {
            var sanitizedError = _redactor.Redact(ex.Message);
            stepExecution.Status = ExecutionStatus.Failed;
            stepExecution.ErrorOutput = TruncateForPersist(sanitizedError);
            stepExecution.CompletedAt = DateTime.UtcNow;
            // DB write in error path: swallow any secondary failure — a failed step's DB
            // write must never propagate out of StepRunner and abort the scheduler loop,
            // leaving all downstream steps permanently Skipped with a wrong timestamp.
            try { await stepDb.SaveChangesMeasuredAsync("step.failed", CancellationToken.None); }
            catch (Exception dbEx) { _logger.LogWarning(dbEx, "Failed to persist Failed status for step {StepId}.", node.Id); }

            stepActivity?.SetTag(TelemetryConstants.Attributes.StepStatus, "Failed");
            stepActivity?.SetStatus(ActivityStatusCode.Error, sanitizedError);
            stepActivity?.AddException(ex);
            finalStatus = "Failed";

            LogStepDetail(execution, node,
                new ActivityResult { Success = false, ErrorOutput = sanitizedError },
                stepStopwatch.Elapsed, resolvedTargetMachine);
            LogStepFailedAsSupport(execution, node, sanitizedError);

            await _notifier.StepCompletedAsync(execution.Id, execution.WorkflowId, node.Id, node.Data.Label,
                ExecutionStatus.Failed, null, sanitizedError, stepExecution.CompletedAt.Value,
                stepType: node.Type, startedAt: stepExecution.StartedAt, outputVariable: node.Data.OutputVariable);

            return new ActivityResult { Success = false, ErrorOutput = ex.Message };
        }
        finally
        {
            stepStopwatch.Stop();
            var statusTag = new KeyValuePair<string, object?>("status", finalStatus);
            EngineMetrics.StepsExecuted.Add(1, activityTypeTag, statusTag);
            EngineMetrics.StepDuration.Record(stepStopwatch.Elapsed.TotalMilliseconds, activityTypeTag, statusTag);
        }
    }

    private async Task<(ActivityResult result, int attemptsUsed)> RunWithRetryAsync(
        WorkflowNode node,
        IActivityExecutor executor,
        StepExecutionContext context,
        JsonElement configForExecution,
        string? resolvedTargetMachine,
        IReadOnlyDictionary<string, RetryPolicy> retryPolicies,
        CancellationToken ct)
    {
        var retryPolicy = retryPolicies.TryGetValue(node.Id, out var p) ? p : RetryPolicy.Disabled;
        int attemptsUsed = 0;
        ActivityResult result = null!;

        using var activitySpan = WorkflowEngine.ActivitiesSource.StartActivity($"activity.{node.Type}", ActivityKind.Internal);
        activitySpan?.SetTag(TelemetryConstants.Attributes.StepActivityType, node.Type);
        activitySpan?.SetTag(TelemetryConstants.Attributes.StepId, node.Id);
        if (!string.IsNullOrEmpty(resolvedTargetMachine))
            activitySpan?.SetTag(TelemetryConstants.Attributes.StepTargetMachine, resolvedTargetMachine);

        for (int attempt = 1; attempt <= retryPolicy.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 1)
            {
                var delay = retryPolicy.DelayFor(attempt);
                _logger.LogWarning(
                    "Step {StepId} ({ActivityType}) attempt {Attempt}/{Max} after failure; backing off {DelayMs} ms",
                    node.Id, node.Type, attempt, retryPolicy.MaxAttempts, (long)delay.TotalMilliseconds);
                EngineMetrics.RetryAttempts.Add(1,
                    new KeyValuePair<string, object?>("activity_type", node.Type));
                if (delay > TimeSpan.Zero)
                {
                    EngineMetrics.RetryBackoffDuration.Record(delay.TotalMilliseconds,
                        new KeyValuePair<string, object?>("backoff_kind", retryPolicy.Backoff.ToString()));
                    await Task.Delay(delay, ct);
                }
            }

            attemptsUsed = attempt;
            result = await executor.ExecuteAsync(context, configForExecution, ct);
            if (result.Success) break;
        }

        activitySpan?.SetTag(TelemetryConstants.Attributes.StepStatus, result.Success ? "Succeeded" : "Failed");
        activitySpan?.SetTag("retry.attempts_used", attemptsUsed);
        activitySpan?.SetTag("retry.max_attempts", retryPolicy.MaxAttempts);
        if (activitySpan is { IsAllDataRequested: true } && result.OutputParameters is { Count: > 0 } op)
        {
            foreach (var (rawKey, value) in op)
            {
                if (!ActivityTelemetryAllowList.IsExposable(node.Type, rawKey)) continue;
                var asString = value;
                if (asString.Length > 256) asString = asString.Substring(0, 256) + "...";
                activitySpan.SetTag($"nodepilot.activity.{node.Type}.{rawKey}", asString);
            }
        }

        if (result.Success)
            activitySpan?.SetStatus(ActivityStatusCode.Ok);
        else
            activitySpan?.SetStatus(ActivityStatusCode.Error, result.ErrorOutput);

        return (result, attemptsUsed);
    }

    private static bool ShouldPauseForDebug(
        WorkflowNode node,
        IReadOnlyDictionary<string, ActivityResult> previousResults,
        IReadOnlyDictionary<string, string> outputVariableToStepId,
        IReadOnlyDictionary<string, string> globalVariables,
        DebugHandle debug)
    {
        if (!node.Data.Breakpoint || debug.StepOverArmed)
            return true;
        if (string.IsNullOrWhiteSpace(node.Data.BreakpointCondition))
            return true;

        var resolved = VariableResolver.ResolveStringValue(node.Data.BreakpointCondition, previousResults, outputVariableToStepId, globalVariables) ?? "";
        return resolved.Length > 0
            && !resolved.Equals("false", StringComparison.OrdinalIgnoreCase)
            && !resolved.Equals("0", StringComparison.OrdinalIgnoreCase)
            && !resolved.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    private void LogStepDetail(WorkflowExecution execution, WorkflowNode node, ActivityResult result,
        TimeSpan duration, string? targetMachine)
    {
        if (!_stepDetailEnabled) return;
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["workflow_execution_id"] = execution.Id,
            ["workflow_id"] = execution.WorkflowId,
            ["step_id"] = node.Id,
            ["step_label"] = node.Data.Label ?? node.Id,
            ["activity_type"] = node.Type,
            ["target_machine"] = string.IsNullOrEmpty(targetMachine) ? "engine-local" : targetMachine,
        }))
        {
            var output = Truncate(result.Output, _stepDetailMaxChars);
            var error = Truncate(result.ErrorOutput, _stepDetailMaxChars);
            if (result.Success)
            {
                _logger.LogInformation(
                    "Step {StepId} ({ActivityType}) succeeded in {DurationMs}ms. Output: {Output}",
                    node.Id, node.Type, duration.TotalMilliseconds, output);
            }
            else
            {
                _logger.LogWarning(
                    "Step {StepId} ({ActivityType}) failed in {DurationMs}ms. Error: {Error}. Output: {Output}",
                    node.Id, node.Type, duration.TotalMilliseconds, error, output);
            }

            if (!string.IsNullOrEmpty(result.TraceOutput))
            {
                var transcript = Truncate(result.TraceOutput, _stepDetailMaxChars);
                _logger.LogInformation(
                    "Step {StepId} ({ActivityType}) transcript:{Newline}{Transcript}",
                    node.Id, node.Type, Environment.NewLine, transcript);
            }
        }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length > max ? s.Substring(0, max) + "... [truncated]" : s;
    }

    /// <summary>
    /// Emits a compact line to the support log (a second Serilog sink) when a step
    /// genuinely failed (the activity returned Success=false or threw an exception).
    /// Deliberately NOT called for junction-race cancellations — those are expected
    /// behavior for waitAny branches and would flood the support log.
    ///
    /// Format: <c>STEP_FAILED exec=&lt;short&gt; step=&lt;label&gt; activity=&lt;type&gt; reason=&lt;...&gt;</c>.
    /// Reason is redacted (comes from _redactor.Redact output) and capped at 500 characters.
    /// </summary>
    private void LogStepFailedAsSupport(WorkflowExecution execution, WorkflowNode node, string? errorOutput)
    {
        var reason = string.IsNullOrEmpty(errorOutput)
            ? "(no error message)"
            : (errorOutput.Length > 500 ? errorOutput.Substring(0, 500) + "..." : errorOutput);
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["workflow_execution_id"] = execution.Id,
            ["workflow_id"] = execution.WorkflowId,
            ["step_id"] = node.Id,
            ["step_label"] = node.Data.Label ?? node.Id,
            ["activity_type"] = node.Type ?? "unknown",
            ["SupportLog"] = true,
            ["support.event_type"] = "STEP_FAILED",
            // Clean message for the DB projection: just the failure reason. Exec/Step/Activity
            // are stored as separate columns.
            ["support.message"] = reason,
        }))
        {
            _logger.LogWarning(
                "STEP_FAILED exec={ExecutionShort} step={StepLabel} activity={ActivityType} reason={Reason}",
                execution.Id.ToString("N")[..8],
                node.Data.Label ?? node.Id,
                node.Type ?? "unknown",
                reason);
        }
    }

    private string? TruncateForPersist(string? value)
    {
        if (value is null) return null;
        if (_stepDetailMaxChars <= 0) return value;
        if (value.Length <= _stepDetailMaxChars) return value;
        return value.Substring(0, _stepDetailMaxChars)
             + $"\n\u2026 [truncated, full length was {value.Length} chars]";
    }

    /// <summary>
    /// Scans a resolved config element for step-pattern placeholders that were not substituted.
    /// Returns a deduplicated list of remaining <c>{{step.output}}</c>-style patterns.
    /// Fields listed in <see cref="FieldsNotToResolve"/> for this activity type are skipped \u2014
    /// their raw SQL / query text is intentionally left unresolved and validated by the executor.
    /// </summary>
    internal static List<string> FindUnresolvedStepReferences(string? activityType, JsonElement config)
    {
        FieldsNotToResolve.TryGetValue(activityType ?? string.Empty, out var protectedFields);

        if (protectedFields is { Count: > 0 } && config.ValueKind == JsonValueKind.Object)
        {
            var unresolved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in config.EnumerateObject())
            {
                if (protectedFields.Contains(prop.Name)) continue;
                var raw = prop.Value.GetRawText();
                if (!raw.Contains("{{")) continue;
                foreach (Match m in VariableResolver.StepPattern.Matches(raw))
                    unresolved.Add(m.Value);
            }
            return [..unresolved];
        }

        var fullRaw = config.GetRawText();
        if (!fullRaw.Contains("{{")) return [];
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in VariableResolver.StepPattern.Matches(fullRaw))
            set.Add(m.Value);
        return [..set];
    }

    /// <summary>
    /// Builds a granular diagnostic for the T-7.1 abort: splits unresolved
    /// <c>{{step.tail}}</c> patterns into three buckets so workflow authors can fix
    /// the right thing without a guessing game. Buckets:
    /// <list type="bullet">
    ///   <item><b>Step missing</b> \u2014 the prefix (alias or raw id) is not in any
    ///     prior step's results. Usually a typo or a deleted upstream node.</item>
    ///   <item><b>Param missing</b> \u2014 step ran, but <c>OutputParameters</c> has
    ///     no such key. Either the activity does not emit that param at all
    ///     (e.g. wmiQuery never had per-property params before captureProperties),
    ///     or the runScript producer never assigned <c>$paramName</c>.</item>
    ///   <item><b>Step did not produce a value</b> \u2014 step is in the result map but
    ///     the requested tail (.output/.error/.success) is empty. Rare \u2014 typically
    ///     means a step failed before producing output.</item>
    /// </list>
    /// The old message "Ensure the referenced step exists and has already run"
    /// conflated all three and sent authors hunting in the wrong place.
    /// </summary>
    internal static string FormatUnresolvedDiagnostic(
        IReadOnlyCollection<string> unresolved,
        IReadOnlyDictionary<string, ActivityResult> previousResults,
        IReadOnlyDictionary<string, string> outputVariableToStepId)
    {
        // Build a lookup that mirrors VariableResolver: alias OR raw stepId resolves
        // to the producing ActivityResult. OrdinalIgnoreCase to match the resolver.
        var nameToResult = new Dictionary<string, ActivityResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stepId, res) in previousResults)
            nameToResult[stepId] = res;
        foreach (var (alias, stepId) in outputVariableToStepId)
            if (previousResults.TryGetValue(stepId, out var res))
                nameToResult[alias] = res;

        var stepMissing = new List<string>();
        var paramMissing = new List<(string token, string step, string param)>();
        var valueEmpty = new List<(string token, string step, string tail)>();

        foreach (var token in unresolved)
        {
            var match = VariableResolver.StepPattern.Match(token);
            if (!match.Success)
            {
                // Pattern that doesn't even shape-match \u2014 surface verbatim so the author
                // sees what we choked on.
                stepMissing.Add(token);
                continue;
            }

            var stepName = match.Groups[1].Value;
            var tail = match.Groups[2].Value;

            if (!nameToResult.TryGetValue(stepName, out var result))
            {
                stepMissing.Add(token);
                continue;
            }

            if (tail.StartsWith("param.", StringComparison.Ordinal) && match.Groups[3].Success)
            {
                var paramKey = match.Groups[3].Value;
                if (!result.OutputParameters.ContainsKey(paramKey))
                {
                    paramMissing.Add((token, stepName, paramKey));
                    continue;
                }
                // Step + param both exist but resolver still left it unresolved \u2014 the
                // OutputParameters dict literally had the key set to null/empty. Surface
                // as a value-empty case so the author doesn't chase a phantom missing-key.
                valueEmpty.Add((token, stepName, $"param.{paramKey}"));
                continue;
            }

            valueEmpty.Add((token, stepName, tail));
        }

        var sb = new StringBuilder();
        sb.Append("Unresolved template variable(s): ").Append(string.Join(", ", unresolved)).Append('.');

        if (stepMissing.Count > 0)
        {
            sb.Append(" Missing step(s) \u2014 reference points to a step that has not run or does not exist: ")
              .Append(string.Join(", ", stepMissing))
              .Append('.');
        }

        if (paramMissing.Count > 0)
        {
            // Group by stepName for readability when multiple params from the same step fail.
            var grouped = paramMissing
                .GroupBy(p => p.step, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var producedParams = nameToResult.TryGetValue(g.Key, out var res) && res.OutputParameters.Count > 0
                        ? string.Join(", ", res.OutputParameters.Keys)
                        : "(none)";
                    return $"step '{g.Key}' did not emit param(s) [{string.Join(", ", g.Select(p => p.param))}] (available: {producedParams})";
                });
            sb.Append(' ').Append(string.Join("; ", grouped)).Append('.');
        }

        if (valueEmpty.Count > 0)
        {
            var grouped = valueEmpty.GroupBy(v => v.step, StringComparer.OrdinalIgnoreCase)
                .Select(g => $"step '{g.Key}' has empty {string.Join("/", g.Select(v => v.tail))}");
            sb.Append(' ').Append(string.Join("; ", grouped)).Append('.');
        }

        return sb.ToString();
    }
}

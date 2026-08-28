using System.Text.Json;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Engine.Security;
using NodePilot.Api.Security;

namespace NodePilot.Api.ExecutionDispatch;

public sealed class ExecutionDispatchService : IWorkflowExecutionDispatcher
{
    private readonly NodePilotDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutputRedactor _redactor;
    private readonly ISecretProtector _protector;
    private readonly ExecutionDispatchSignal _signal;
    private readonly ExecutionDispatchCallbackRegistry _callbacks;
    private readonly IClusterStateProvider _cluster;
    private readonly IMaintenanceWindowEvaluator _maintenance;
    private readonly IWorkflowConcurrencyGate _concurrency;
    private readonly ILogger<ExecutionDispatchService> _logger;
    private readonly IDatabaseAvailability? _availability;

    public ExecutionDispatchService(
        NodePilotDbContext db,
        IServiceScopeFactory scopeFactory,
        OutputRedactor redactor,
        IClusterStateProvider cluster,
        IMaintenanceWindowEvaluator maintenance,
        IWorkflowConcurrencyGate concurrency,
        ILogger<ExecutionDispatchService> logger,
        IDatabaseAvailability? availability = null,
        ISecretProtector? protector = null,
        ExecutionDispatchSignal? signal = null,
        ExecutionDispatchCallbackRegistry? callbacks = null)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _redactor = redactor;
        _protector = protector ?? new NodePilot.Data.Security.DpapiSecretProtector(
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        _signal = signal ?? new ExecutionDispatchSignal();
        _callbacks = callbacks ?? new ExecutionDispatchCallbackRegistry();
        _cluster = cluster;
        _maintenance = maintenance;
        _concurrency = concurrency;
        _logger = logger;
        _availability = availability;
    }

    public WorkflowExecution AddPendingExecution(WorkflowDispatchIntent intent)
    {
        // External idempotency needs the Pending Execution and idempotency key in one
        // transaction; creation still lives here so redaction and owner stamping stay local.
        var execution = BuildPendingExecution(intent);
        _db.WorkflowExecutions.Add(execution);
        _db.ExecutionDispatchOutbox.Add(BuildOutboxItem(execution.Id, intent));
        _callbacks.Register(execution.Id, intent.OnDispatchSuppressedAsync);
        return execution;
    }

    public async Task<WorkflowExecution> DispatchAsync(WorkflowDispatchIntent intent, CancellationToken ct)
    {
        var pending = AddPendingExecution(intent);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            _callbacks.Remove(pending.Id);
            throw;
        }
        _signal.Pulse();
        return pending;
    }

    private ExecutionDispatchOutboxItem BuildOutboxItem(Guid executionId, WorkflowDispatchIntent intent)
    {
        var parameterJson = intent.Parameters is null
            ? null
            : JsonSerializer.Serialize(intent.Parameters);
        return new ExecutionDispatchOutboxItem
        {
            ExecutionId = executionId,
            WorkflowId = intent.WorkflowId,
            TriggeredBy = intent.TriggeredBy,
            ProtectedParameters = parameterJson is null ? null : _protector.Protect(parameterJson),
            TimeoutSeconds = intent.TimeoutSeconds,
            DebugEnabled = intent.DebugEnabled,
            StartedByUserId = intent.StartedByUserId,
            ParentExecutionId = intent.ParentExecutionId,
            CallDepth = intent.CallDepth,
            RequireWorkflowEnabled = intent.RequireWorkflowEnabled,
            MissingWorkflowMessage = intent.MissingWorkflowMessage,
            PreOwnershipFailurePrefix = intent.PreOwnershipFailurePrefix,
            Priority = intent.Priority,
            RequireMaintenanceWindowCheck = intent.RequireMaintenanceWindowCheck,
            BypassMaintenanceWindow = intent.BypassMaintenanceWindow,
            CreatedAt = DateTime.UtcNow,
            AvailableAt = DateTime.UtcNow,
        };
    }

    private WorkflowExecution BuildPendingExecution(WorkflowDispatchIntent intent)
    {
        return new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = intent.WorkflowId,
            Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
            TriggeredBy = intent.TriggeredBy,
            StartedByUserId = intent.StartedByUserId,
            // Stamp owner so the failover-recovery sweep can tell our rows apart from a
            // dead leader's. In single-node mode this is just the machine name; the
            // recovery filter still works (the local node is the only one writing).
            OwnerNodeId = _cluster.NodeId,
            InputParametersJson = RedactAndCap(SerializeInputParameters(intent.Parameters), 32 * 1024),
        };
    }

    /// <summary>Wakes a worker after an externally owned admission transaction commits.</summary>
    public void NotifyCommitted() => _signal.Pulse();

    internal async Task<ExecutionDispatchOutcome> ProcessOutboxAsync(Guid executionId, CancellationToken workerCt)
    {
        var outbox = await _db.ExecutionDispatchOutbox
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.ExecutionId == executionId, workerCt);
        if (outbox is null) return ExecutionDispatchOutcome.Completed;

        var pending = await _db.WorkflowExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == executionId, workerCt);
        if (pending is null || pending.Status != ExecutionStatus.Pending)
        {
            await RemoveOutboxAsync(executionId);
            return ExecutionDispatchOutcome.Completed;
        }

        WorkflowDispatchIntent intent;
        try
        {
            Dictionary<string, string>? parameters = null;
            if (outbox.ProtectedParameters is not null)
            {
                var json = _protector.Unprotect(outbox.ProtectedParameters);
                parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }

            _callbacks.TryGet(executionId, out var callback);
            intent = new WorkflowDispatchIntent(
                outbox.WorkflowId,
                outbox.TriggeredBy,
                parameters,
                outbox.TimeoutSeconds,
                outbox.DebugEnabled,
                outbox.StartedByUserId,
                outbox.RequireWorkflowEnabled,
                outbox.MissingWorkflowMessage,
                outbox.PreOwnershipFailurePrefix,
                Priority: outbox.Priority,
                OnDispatchSuppressedAsync: callback,
                RequireMaintenanceWindowCheck: outbox.RequireMaintenanceWindowCheck,
                BypassMaintenanceWindow: outbox.BypassMaintenanceWindow,
                ParentExecutionId: outbox.ParentExecutionId,
                CallDepth: outbox.CallDepth);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatch intent for execution {ExecutionId} could not be decrypted.", executionId);
            await MarkPendingExecutionTerminalAsync(
                _db, executionId, ExecutionStatus.Failed,
                "Queued execution could not be dispatched because its protected intent is unreadable.",
                CancellationToken.None);
            await RemoveOutboxAsync(executionId);
            return ExecutionDispatchOutcome.Completed;
        }

        var outcome = await RunDispatchedExecutionAsync(pending, intent, workerCt);
        if (outcome == ExecutionDispatchOutcome.Completed)
            await RemoveOutboxAsync(executionId);
        return outcome;
    }

    private async Task RemoveOutboxAsync(Guid executionId)
    {
        await _db.ExecutionDispatchOutbox
            .Where(item => item.ExecutionId == executionId)
            .ExecuteDeleteAsync(CancellationToken.None);
        _callbacks.Remove(executionId);
    }

    /// <summary>
    /// Runs the actual workflow execution from a dispatch worker. Owns its own DI scope
    /// for the entire engine.ExecuteAsync lifetime. All pre-ownership exceptions are
    /// translated to a Failed/Cancelled execution row.
    /// </summary>
    private async Task<ExecutionDispatchOutcome> RunDispatchedExecutionAsync(
        WorkflowExecution pending,
        WorkflowDispatchIntent request,
        CancellationToken workerCt)
    {
        var engineStarted = false;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            try
            {
                if (!await IsPendingExecutionAsync(db, pending.Id, workerCt))
                    return ExecutionDispatchOutcome.Completed;

                var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
                var workflow = await db.Workflows.FindAsync([request.WorkflowId], workerCt);
                if (workflow is null || (request.RequireWorkflowEnabled && !workflow.IsEnabled))
                {
                    var reason = workflow is null
                        ? "workflow_deleted_before_dispatch"
                        : "workflow_disabled_before_dispatch";
                    await MarkPendingExecutionTerminalAsync(db, pending.Id, ExecutionStatus.Cancelled,
                        request.MissingWorkflowMessage,
                        CancellationToken.None);
                    await NotifyDispatchSuppressedAsync(request, reason, CancellationToken.None);
                    return ExecutionDispatchOutcome.Completed;
                }

                // Authoritative maintenance-window gate. Catches every admission path and closes
                // the TOCTOU where a window opens between a caller's early check and worker
                // pickup. Recovery operations (manual retry) and resume/sub-workflow bypass this
                // via RequireMaintenanceWindowCheck=false; an Admin force-run sets
                // BypassMaintenanceWindow.
                if (request.RequireMaintenanceWindowCheck && !request.BypassMaintenanceWindow)
                {
                    var verdict = _maintenance.Evaluate(workflow.Id, workflow.FolderId, DateTime.UtcNow);
                    if (verdict.Blocked)
                    {
                        Telemetry.ApiMetrics.MaintenanceWindowBlocks.Add(1,
                            new("source", request.TriggeredBy),
                            new("scope", "dispatch"));
                        await MarkPendingExecutionTerminalAsync(db, pending.Id, ExecutionStatus.Cancelled,
                            $"Blocked by maintenance window '{verdict.WindowName}'."
                                + (verdict.ActiveUntilUtc is { } until ? $" Active until {until:u}." : string.Empty),
                            CancellationToken.None);
                        // Race fix: the window opened between the caller's early check and this
                        // worker pickup, so an external-trigger idempotency key may already be
                        // committed pointing at this now-Cancelled row. Drop it, otherwise the same
                        // key would replay the Cancelled ghost for its 24h TTL even after the
                        // window
                        // closes. A legitimate retry then runs instead.
                        await db.IdempotencyKeys
                            .Where(k => k.ExecutionId == pending.Id)
                            .ExecuteDeleteAsync(CancellationToken.None);
                        await NotifyDispatchSuppressedAsync(request, "maintenance_window_blocked", CancellationToken.None);
                        return ExecutionDispatchOutcome.Completed;
                    }
                }

                var principalFailure = await ValidateEffectivePrincipalAsync(
                    db, scope.ServiceProvider, workflow, request, workerCt);
                if (principalFailure is not null)
                {
                    await MarkPendingExecutionTerminalAsync(db, pending.Id, ExecutionStatus.Cancelled,
                        $"Execution principal rejected: {principalFailure}.", CancellationToken.None);
                    await NotifyDispatchSuppressedAsync(request, principalFailure, CancellationToken.None);
                    return ExecutionDispatchOutcome.Completed;
                }

                // Per-workflow concurrency limit. Checked here, after the gates that can
                // terminalize the run (a run about to be Cancelled must not consume a slot) and
                // before ownership transfers: throwing from inside the engine would land in the
                // catch below and mark the execution Failed instead of leaving it queued.
                // The limit comes from the row loaded just above, so it is always current.
                if (!_concurrency.TryAcquire(workflow.Id, workflow.MaxConcurrentExecutions))
                {
                    Telemetry.ApiMetrics.WorkflowConcurrencyDeferrals.Add(1,
                        new KeyValuePair<string, object?>("source", request.TriggeredBy));
                    return ExecutionDispatchOutcome.DeferredByConcurrencyLimit;
                }

                try
                {
                    // Crossing this line transfers ownership to the engine. Any exception
                    // afterwards has unknown side effects/claim state and must never cause an
                    // automatic second start.
                    engineStarted = true;
                    var executed = await engine.ExecuteAsync(
                        workflow,
                        request.TriggeredBy,
                        workerCt,
                        request.Parameters,
                        request.TimeoutSeconds,
                        request.DebugEnabled,
                        request.StartedByUserId,
                        request.ParentExecutionId,
                        request.CallDepth,
                        executionIdOverride: pending.Id,
                        interactiveRun: request.Priority == ExecutionDispatchPriority.Interactive);
                    if (executed.Status == ExecutionStatus.Pending)
                    {
                        // The engine's database claim was fenced (typically leadership changed
                        // between outbox lease and engine ownership). No activity ran; preserve
                        // the durable intent for the current leader instead of deleting accepted
                        // work.
                        return ExecutionDispatchOutcome.RetryBeforeStart;
                    }
                    return ExecutionDispatchOutcome.Completed;
                }
                finally
                {
                    _concurrency.Release(workflow.Id);
                }
            }
            catch (OperationCanceledException) when (workerCt.IsCancellationRequested)
            {
                // Cancellation can win before the engine's Pending -> Running claim. Preserve
                // the intent only when the database proves ownership never transferred.
                return await IsPendingExecutionAsync(db, pending.Id, CancellationToken.None)
                    ? ExecutionDispatchOutcome.RetryBeforeStart
                    : ExecutionDispatchOutcome.Completed;
            }
            catch (Exception ex)
            {
                if (!engineStarted && IsConfirmedDatabaseOutage(ex))
                {
                    _logger.LogWarning(ex,
                        "Dispatch for execution {ExecutionId} paused before engine start; requeueing for database recovery.",
                        pending.Id);
                    return ExecutionDispatchOutcome.RetryBeforeStart;
                }

                _logger.LogError(ex,
                    "Dispatched workflow execution {ExecutionId} failed {OwnershipPhase}.",
                    pending.Id,
                    engineStarted ? "after engine start" : "before engine ownership");
                await MarkPendingExecutionTerminalAsync(db, pending.Id, ExecutionStatus.Failed,
                    $"{request.PreOwnershipFailurePrefix}: {ex.Message}",
                    CancellationToken.None);
                await NotifyDispatchSuppressedAsync(request, "dispatch_exception", CancellationToken.None);
                return ExecutionDispatchOutcome.Completed;
            }
        }
        catch (Exception fatal)
        {
            if (!engineStarted)
            {
                // No engine call means no activity can have produced side effects. Keep the
                // durable intent even when the failure could not be classified (for example,
                // scope creation failed or terminalizing a rejected dispatch also failed).
                // Dropping the outbox here would turn an accepted Pending execution into lost
                // work. A worker retry or startup recovery can safely make progress later.
                _logger.LogError(fatal,
                    "Dispatch for execution {ExecutionId} failed before engine ownership; preserving the durable intent for retry.",
                    pending.Id);
                return ExecutionDispatchOutcome.RetryBeforeStart;
            }

            // Once engine invocation began, retrying could duplicate external side effects.
            _logger.LogError(fatal,
                "Unrecoverable dispatch failure for execution {ExecutionId} after engine invocation; automatic retry is suppressed.",
                pending.Id);
            return ExecutionDispatchOutcome.Completed;
        }
    }

    private bool IsConfirmedDatabaseOutage(Exception exception)
        => _availability is { IsServable: false }
           && DbErrorClassifier.Classify(exception) is not DbFailureKind.None;

    private async Task NotifyDispatchSuppressedAsync(
        WorkflowDispatchIntent request,
        string reason,
        CancellationToken ct)
    {
        if (request.OnDispatchSuppressedAsync is null) return;

        try
        {
            await request.OnDispatchSuppressedAsync(
                new WorkflowDispatchSuppression(request.WorkflowId, request.TriggeredBy, reason),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Dispatch suppression callback failed for workflow {WorkflowId} with reason {Reason}.",
                request.WorkflowId,
                reason);
        }
    }

    private static string? SerializeInputParameters(Dictionary<string, string>? inputParameters)
    {
        if (inputParameters is null || inputParameters.Count == 0) return null;
        var filtered = inputParameters
            .Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return filtered.Count == 0 ? null : JsonSerializer.Serialize(filtered);
    }

    private static async Task<string?> ValidateEffectivePrincipalAsync(
        NodePilotDbContext db,
        IServiceProvider services,
        Workflow workflow,
        WorkflowDispatchIntent request,
        CancellationToken ct)
    {
        var automated = request.TriggeredBy is not ("manual" or "debug")
                        && !request.TriggeredBy.StartsWith("retry:", StringComparison.Ordinal);
        var effectiveUserId = request.StartedByUserId;
        if (automated && effectiveUserId is null)
            return "missing_effective_principal";
        if (effectiveUserId is null) return null;

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == effectiveUserId, ct);
        if (user is null || !user.IsActive || user.IsTombstoned)
            return "effective_principal_inactive";
        if (user.Provider != AuthProvider.Local)
        {
            var evaluator = services.GetService<ExternalAuthorizationEvaluator>();
            if (evaluator is not null)
            {
                var evaluation = await evaluator.EvaluateAsync(user, DateTime.UtcNow, ct);
                if (!evaluation.IsCurrent)
                    return "effective_principal_authorization_stale";
            }
            else
            {
                var configuration = services.GetService<IConfiguration>();
                var configured = configuration?.GetValue(
                    "Authentication:MaxAuthorizationStalenessMinutes", 15) ?? 15;
                var maxStaleness = TimeSpan.FromMinutes(Math.Clamp(configured, 1, 15));
                if (user.LastDirectorySyncAt is null
                    || DateTime.UtcNow - user.LastDirectorySyncAt.Value > maxStaleness)
                    return "effective_principal_authorization_stale";
            }
        }

        if (automated)
        {
            var authorization = services.GetService<IResourceAuthorizationService>();
            if (authorization is null)
                return "authorization_service_unavailable";
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
            ], "automated-dispatch");
            if (!await authorization.CanAccessWorkflowAsync(
                    new ClaimsPrincipal(identity), workflow.FolderId, ResourceOp.Run, ct))
                return "effective_principal_not_authorized";
        }

        return null;
    }

    private string? RedactAndCap(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var redacted = _redactor.Redact(value) ?? value;
        return redacted.Length > maxChars
            ? redacted[..maxChars] + "... [truncated]"
            : redacted;
    }

    private static async Task<bool> IsPendingExecutionAsync(
        NodePilotDbContext db,
        Guid executionId,
        CancellationToken ct)
    {
        return await db.WorkflowExecutions
            .AsNoTracking()
            .AnyAsync(e => e.Id == executionId && e.Status == ExecutionStatus.Pending, ct);
    }

    private static async Task MarkPendingExecutionTerminalAsync(
        NodePilotDbContext db,
        Guid executionId,
        ExecutionStatus status,
        string message,
        CancellationToken ct)
    {
        var cappedMessage = message.Length > 32 * 1024
            ? message[..(32 * 1024)] + "... [truncated]"
            : message;
        await ExecutionStateLifecycle.TrySetTerminalAsync(
            db.WorkflowExecutions.Where(execution => execution.Id == executionId
                && (execution.Status == ExecutionStatus.Pending
                    || execution.Status == ExecutionStatus.Running)),
            status,
            DateTime.UtcNow,
            cappedMessage,
            status == ExecutionStatus.Cancelled ? "dispatch" : null,
            ct);
    }
}

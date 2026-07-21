using System.Text.Json;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;
using NodePilot.Api.Security;

namespace NodePilot.Api.ExecutionDispatch;

public sealed class ExecutionDispatchService : IWorkflowExecutionDispatcher
{
    private readonly NodePilotDbContext _db;
    private readonly IExecutionDispatchQueue _dispatchQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutputRedactor _redactor;
    private readonly IClusterStateProvider _cluster;
    private readonly IMaintenanceWindowEvaluator _maintenance;
    private readonly ILogger<ExecutionDispatchService> _logger;

    public ExecutionDispatchService(
        NodePilotDbContext db,
        IExecutionDispatchQueue dispatchQueue,
        IServiceScopeFactory scopeFactory,
        OutputRedactor redactor,
        IClusterStateProvider cluster,
        IMaintenanceWindowEvaluator maintenance,
        ILogger<ExecutionDispatchService> logger)
    {
        _db = db;
        _dispatchQueue = dispatchQueue;
        _scopeFactory = scopeFactory;
        _redactor = redactor;
        _cluster = cluster;
        _maintenance = maintenance;
        _logger = logger;
    }

    public WorkflowExecution AddPendingExecution(WorkflowDispatchIntent intent)
    {
        // External idempotency needs the Pending Execution and idempotency key in one
        // transaction; creation still lives here so redaction and owner stamping stay local.
        var execution = BuildPendingExecution(intent);
        _db.WorkflowExecutions.Add(execution);
        return execution;
    }

    public async Task<WorkflowExecution> DispatchAsync(WorkflowDispatchIntent intent, CancellationToken ct)
    {
        var pending = AddPendingExecution(intent);
        await _db.SaveChangesAsync(ct);
        await EnqueueAsync(pending, intent, ct);
        return pending;
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

    public async Task EnqueueAsync(
        WorkflowExecution pending,
        WorkflowDispatchIntent request,
        CancellationToken ct)
    {
        try
        {
            // Once the Pending execution is persisted, dispatch ownership must no longer
            // depend on the HTTP request lifetime. Under load the bounded queue can wait
            // for capacity; a client timeout here must not cancel the already-created job.
            _ = ct;
            // Interactive runs stay latency-preferred via the queue's priority lane, but
            // still consume the bounded worker pool. Otherwise manual/webhook bursts create
            // one unbounded Task per request and bypass WorkerCount entirely.
            // Worker awaits the execution to completion: WorkerCount is the real
            // concurrency limit, the queue (Capacity) is the spike buffer. Anything
            // beyond WorkerCount waits in the queue until a slot frees up. The engine's
            // MaxConcurrentExecutions caps are a sanity upper-bound for pathological
            // cases (sub-workflow cascades, trigger loops) and are sized well above
            // WorkerCount so they never trip in normal operation.
            await _dispatchQueue.EnqueueAsync(
                workerCt => RunDispatchedExecutionAsync(pending, request, workerCt),
                CancellationToken.None,
                request.Priority);
        }
        catch
        {
            await MarkPendingExecutionTerminalAsync(_db, pending.Id, request.EnqueueFailureStatus,
                request.EnqueueFailureMessage,
                CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Runs the actual workflow execution from a dispatch worker. Owns its own DI scope
    /// for the entire engine.ExecuteAsync lifetime. All pre-ownership exceptions are
    /// translated to a Failed/Cancelled execution row.
    /// </summary>
    private async Task RunDispatchedExecutionAsync(
        WorkflowExecution pending,
        WorkflowDispatchIntent request,
        CancellationToken workerCt)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            try
            {
                if (!await IsPendingExecutionAsync(db, pending.Id, workerCt))
                    return;

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
                    return;
                }

                // Authoritative maintenance-window gate. Catches every admission path and closes
                // the TOCTOU where a window opens between a caller's early check and worker
                // pickup. Recovery operations (manual retry) and resume/sub-workflow bypass this
                // via RequireMaintenanceWindowCheck=false; an Admin force-run sets BypassMaintenanceWindow.
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
                        // key would replay the Cancelled ghost for its 24h TTL even after the window
                        // closes. A legitimate retry then runs instead.
                        await db.IdempotencyKeys
                            .Where(k => k.ExecutionId == pending.Id)
                            .ExecuteDeleteAsync(CancellationToken.None);
                        await NotifyDispatchSuppressedAsync(request, "maintenance_window_blocked", CancellationToken.None);
                        return;
                    }
                }

                var principalFailure = await ValidateEffectivePrincipalAsync(
                    db, scope.ServiceProvider, workflow, request, workerCt);
                if (principalFailure is not null)
                {
                    await MarkPendingExecutionTerminalAsync(db, pending.Id, ExecutionStatus.Cancelled,
                        $"Execution principal rejected: {principalFailure}.", CancellationToken.None);
                    await NotifyDispatchSuppressedAsync(request, principalFailure, CancellationToken.None);
                    return;
                }

                await engine.ExecuteAsync(
                    workflow,
                    request.TriggeredBy,
                    workerCt,
                    request.Parameters,
                    request.TimeoutSeconds,
                    request.DebugEnabled,
                    request.StartedByUserId,
                    executionIdOverride: pending.Id,
                    interactiveRun: request.Priority == ExecutionDispatchPriority.Interactive);
            }
            catch (OperationCanceledException) when (workerCt.IsCancellationRequested)
            {
                // Host shutdown — engine has its own finally that marks the row as Cancelled.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Dispatched workflow execution {ExecutionId} failed before engine ownership.",
                    pending.Id);
                await MarkPendingExecutionTerminalAsync(db, pending.Id, ExecutionStatus.Failed,
                    $"{request.PreOwnershipFailurePrefix}: {ex.Message}",
                    CancellationToken.None);
                await NotifyDispatchSuppressedAsync(request, "dispatch_exception", CancellationToken.None);
            }
        }
        catch (Exception fatal)
        {
            // Last-resort: scope creation itself threw, or MarkPendingExecutionTerminalAsync
            // failed. The pending row stays Pending until the startup reconciler sweeps it.
            _logger.LogError(fatal,
                "Unrecoverable dispatch failure for execution {ExecutionId}; row may stay Pending until reconciler runs.",
                pending.Id);
        }
    }

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
        var execution = await db.WorkflowExecutions.FindAsync([executionId], ct);
        // A database-side Pending->Running claim can succeed before the engine reaches
        // graph ownership. If setup/capacity then throws, the dispatch catch must still
        // terminalize that claimed row. Cancelled/succeeded/failed rows remain immutable.
        if (execution is null
            || execution.Status is not (ExecutionStatus.Pending or ExecutionStatus.Running))
            return;

        execution.Status = status;
        // Attribute a pre-ownership terminal cancel (workflow deleted/disabled, maintenance-window
        // block) so alerting can tell it apart from a manual user cancel.
        if (status == ExecutionStatus.Cancelled) execution.CancelledBy = "dispatch";
        execution.CompletedAt = DateTime.UtcNow;
        execution.ErrorMessage = message.Length > 32 * 1024
            ? message[..(32 * 1024)] + "... [truncated]"
            : message;
        await db.SaveChangesAsync(ct);
    }
}

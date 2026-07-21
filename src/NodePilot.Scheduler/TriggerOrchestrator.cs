using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Audit;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;

namespace NodePilot.Scheduler;

/// <summary>
/// Background service that keeps external-trigger subscriptions in sync with the
/// workflow definitions in the database. On a fixed interval it:
///   1. Loads every enabled workflow
///   2. Parses each for trigger nodes (scheduleTrigger / fileWatcherTrigger / databaseTrigger / eventLogTrigger)
///   3. Registers new ones, updates changed ones, disposes removed ones
/// When a trigger fires, the orchestrator submits a Dispatch Intent; Execution Dispatch
/// owns the Pending Execution row and queue handoff.
///
/// WebhookTrigger is NOT handled here — webhooks are served by <c>WebhooksController</c>.
/// </summary>
public class TriggerOrchestrator : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TriggerOrchestrator> _logger;
    private readonly IServiceProvider _rootServices;

    // Key: $"{workflowId}:{nodeId}" → (source, configHash)
    private readonly ConcurrentDictionary<string, (ITriggerSource source, string configHash)> _active = new();

    // Triggers whose most recent start attempt failed. Key: same as _active. Value is the
    // UTC time at which we are allowed to retry + the number of consecutive failures so far.
    // Prevents a persistently-broken trigger (bad directory, invalid cron, missing log) from
    // spamming the log every 5 seconds — the backoff doubles each attempt up to 5 minutes.
    private readonly ConcurrentDictionary<string, (DateTime notBefore, int consecutiveFailures, string hash)> _backoff = new();

    /// <summary>
    /// Per-workflow parse cache. Reusing parsed trigger descriptors avoids doing a full
    /// JsonDocument.Parse on every 5-second tick for workflows whose DefinitionJson is
    /// unchanged. Keyed by workflow id; <c>Workflow.Version</c> (increases monotonically
    /// on every update) acts as the version tag. This lets the sync loop pull only
    /// <c>(Id, Version)</c> from the DB on each tick instead of the full DefinitionJson —
    /// for 1000 workflows at ~20 KB of JSON each, that saves roughly 20 MB of DB traffic
    /// per tick. DefinitionJson is only re-fetched for workflows whose version changed
    /// (or that we're seeing for the first time).
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (int version, List<TriggerDescriptor> descriptors)> _parseCache = new();

    private readonly record struct TriggerDescriptor(string NodeId, string ActivityType, JsonElement Config, string Hash);

    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(5);

    // L-14: captured so FireAsync can propagate host shutdown into engine.ExecuteAsync,
    // letting an in-flight trigger-started run cancel gracefully instead of being
    // ungracefully killed mid-step when the service stops.
    private CancellationToken _stoppingToken;

    private readonly IClusterStateProvider _cluster;

    public TriggerOrchestrator(
        IServiceScopeFactory scopeFactory,
        IServiceProvider rootServices,
        IClusterStateProvider cluster,
        ILogger<TriggerOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _rootServices = rootServices;
        _cluster = cluster;
        _logger = logger;
        // Wake the sync loop immediately on leadership transitions so a freshly-promoted
        // node activates its triggers within milliseconds instead of waiting up to 5 s for
        // the next regular tick.
        _cluster.OnLeadershipAcquired += _ => _wakeSync.TrySetResult();
    }

    private TaskCompletionSource _wakeSync = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _logger.LogInformation("TriggerOrchestrator starting");
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SyncAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Trigger sync failed"); }

            // Wait for either the regular tick OR an immediate wake-up from the cluster
            // (leadership acquired). When the wake fires, swap the TCS so subsequent
            // acquisitions can wake us again.
            var delay = Task.Delay(SyncInterval, stoppingToken);
            var wake = _wakeSync.Task;
            var triggered = await Task.WhenAny(delay, wake);
            if (triggered == wake)
            {
                _wakeSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            if (stoppingToken.IsCancellationRequested) break;
        }

        // Tear down all active sources on shutdown
        foreach (var (_, entry) in _active)
            try { await entry.source.DisposeAsync(); } catch { /* best effort */ }
        _active.Clear();
        _logger.LogInformation("TriggerOrchestrator stopped");
    }

    internal async Task SyncAsync(CancellationToken ct)
    {
        var syncStopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var syncActivity = SchedulerMetrics.Source.StartActivity("trigger.orchestrator.sync", System.Diagnostics.ActivityKind.Internal);
        try
        {
            await SyncInnerAsync(ct);
            syncActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            await WriteHeartbeat(ct, status: "ok");
        }
        catch (Exception ex)
        {
            syncActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            SchedulerMetrics.OrchestratorSyncFailures.Add(1);
            // Still emit a heartbeat with failure status so monitors can distinguish
            // "service dead" (row stale) from "service alive but broken" (row fresh, status
            // starts with 'error:').
            await WriteHeartbeat(ct, status: $"error: {ex.GetType().Name}: {Truncate(ex.Message, 400)}");
            throw;
        }
        finally
        {
            syncStopwatch.Stop();
            SchedulerMetrics.OrchestratorSyncDuration.Record(syncStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task WriteHeartbeat(CancellationToken ct, string? status)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        // We tick every SyncInterval (5 s) but SystemHealthWriter debounces writes to once
        // per 30 s and floors the persisted interval to that cadence, so the dashboard's
        // stale-check reflects the real write rate — not this raw tick value.
        await SystemHealthWriter.BeatAsync(db, "TriggerOrchestrator",
            expectedIntervalSeconds: (int)SyncInterval.TotalSeconds, status: status, ct: ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    private async Task SyncInnerAsync(CancellationToken ct)
    {
        // HA gate: a follower node must not run any trigger sources. If we used to be the
        // leader and just lost it, dispose every active source so nothing fires while we
        // wait. Idempotent — once _active is empty, subsequent ticks fall through cheaply.
        if (!_cluster.IsLeader)
        {
            if (!_active.IsEmpty)
            {
                _logger.LogInformation("Lost leadership — disposing {N} active trigger sources", _active.Count);
                foreach (var (_, entry) in _active)
                    try { await entry.source.DisposeAsync(); } catch { /* best-effort */ }
                _active.Clear();
                _parseCache.Clear();
                _backoff.Clear();
            }
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();

        // Step 1: fetch only the version numbers (a tiny payload, ~20 bytes per row). If a
        // workflow hasn't changed since the last tick, its descriptor list stays cached.
        var versions = await db.Workflows.AsNoTracking()
            .Where(w => w.IsEnabled)
            .Select(w => new { w.Id, w.Version })
            .ToListAsync(ct);

        // Evict cache entries for workflows that disappeared or got disabled so we don't
        // leak memory on long-running processes with churning workflow definitions.
        var activeWorkflowIds = versions.Select(v => v.Id).ToHashSet();
        foreach (var cachedId in _parseCache.Keys)
            if (!activeWorkflowIds.Contains(cachedId))
                _parseCache.TryRemove(cachedId, out _);

        // Step 2: identify the workflows for which we need fresh DefinitionJson
        // (cache miss or a version bump).
        var idsNeedingJson = versions
            .Where(v => !_parseCache.TryGetValue(v.Id, out var cached) || cached.version != v.Version)
            .Select(v => v.Id)
            .ToList();

        Dictionary<Guid, (string json, int version)> freshJson;
        if (idsNeedingJson.Count == 0)
        {
            freshJson = new Dictionary<Guid, (string, int)>(0);
        }
        else
        {
            // Targeted query — only the "dirty" rows come back from the server with DefinitionJson.
            var rows = await db.Workflows.AsNoTracking()
                .Where(w => idsNeedingJson.Contains(w.Id))
                .Select(w => new { w.Id, w.Version, w.DefinitionJson })
                .ToListAsync(ct);
            freshJson = rows.ToDictionary(r => r.Id, r => (r.DefinitionJson, r.Version));
        }

        var desired = new Dictionary<string, (Guid wfId, string nodeId, string activityType, JsonElement config, string hash)>();
        foreach (var v in versions)
        {
            List<TriggerDescriptor> descriptors;
            if (freshJson.TryGetValue(v.Id, out var fresh))
            {
                descriptors = ParseDescriptors(fresh.json);
                _parseCache[v.Id] = (fresh.version, descriptors);
            }
            else if (_parseCache.TryGetValue(v.Id, out var cached))
            {
                descriptors = cached.descriptors;
            }
            else
            {
                // Can happen if the workflow was deleted between the two queries.
                continue;
            }
            foreach (var d in descriptors)
                desired[$"{v.Id}:{d.NodeId}"] = (v.Id, d.NodeId, d.ActivityType, d.Config, d.Hash);
        }

        // Remove obsolete / changed
        foreach (var key in _active.Keys.ToList())
        {
            var isGone = !desired.TryGetValue(key, out var want);
            var changed = !isGone && want.hash != _active[key].configHash;
            if ((isGone || changed) && _active.TryRemove(key, out var old))
            {
                try { await old.source.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed disposing trigger {Key}", key); }
                SchedulerMetrics.OrchestratorSyncChanges.Add(1,
                    new KeyValuePair<string, object?>("change", changed ? "update" : "remove"));
            }
        }

        // Clean up backoff entries for triggers that no longer exist so we don't leak
        // memory for deleted workflows.
        foreach (var bkey in _backoff.Keys.ToList())
            if (!desired.ContainsKey(bkey))
                _backoff.TryRemove(bkey, out _);

        // Add new
        foreach (var (key, want) in desired)
        {
            if (_active.ContainsKey(key)) continue;

            // Skip triggers whose previous StartAsync threw and are still inside the
            // exponential-backoff cool-down. Once the config hash changes we re-try
            // immediately — admin fixed the config, give it another shot.
            if (_backoff.TryGetValue(key, out var bo)
                && bo.hash == want.hash
                && DateTime.UtcNow < bo.notBefore)
                continue;

            ITriggerSource? src = CreateSource(want.activityType);
            if (src is null) continue;
            var ctx = new TriggerContext
            {
                WorkflowId = want.wfId,
                NodeId = want.nodeId,
                Config = want.config,
                OnFire = parameters => FireAsync(want.wfId, want.activityType, parameters),
            };
            try
            {
                await src.StartAsync(ctx, ct);
                _active[key] = (src, want.hash);
                _backoff.TryRemove(key, out _);
                _logger.LogInformation("Registered {Type} trigger for workflow {Wf} node {Node}",
                    want.activityType, want.wfId, want.nodeId);
                SchedulerMetrics.OrchestratorSyncChanges.Add(1,
                    new KeyValuePair<string, object?>("change", "add"),
                    new KeyValuePair<string, object?>("trigger_type", want.activityType));
            }
            catch (Exception ex)
            {
                // Compute exponential backoff: 5s, 10s, 20s, …, capped at 5 minutes.
                var previousFailures = _backoff.TryGetValue(key, out var prev) && prev.hash == want.hash
                    ? prev.consecutiveFailures
                    : 0;
                var failures = previousFailures + 1;
                var delaySeconds = Math.Min(300, 5 * (int)Math.Pow(2, Math.Min(failures - 1, 6)));
                _backoff[key] = (DateTime.UtcNow.AddSeconds(delaySeconds), failures, want.hash);

                // Log at Warning for the first failure (admin attention), Debug for subsequent
                // ones while backing off — prevents log-spam for a persistently-broken trigger.
                if (failures == 1)
                    _logger.LogWarning(ex, "Failed to register trigger {Key} (attempt {N}); retrying in {Delay}s", key, failures, delaySeconds);
                else
                    _logger.LogDebug(ex, "Trigger {Key} still failing (attempt {N}); next retry in {Delay}s", key, failures, delaySeconds);

                SchedulerMetrics.TriggerRegistrationFailures.Add(1,
                    new KeyValuePair<string, object?>("trigger_type", want.activityType));
                try { await src.DisposeAsync(); } catch { /* ignore */ }
            }
        }
    }

    private static List<TriggerDescriptor> ParseDescriptors(string definitionJson)
    {
        if (!WorkflowDefinitionDocument.TryParse(definitionJson, out var definition) || definition is null)
            return [];

        return definition.TriggerDescriptors
            .Where(descriptor => IsHandledHere(descriptor.ActivityType))
            .Select(descriptor => new TriggerDescriptor(
                descriptor.NodeId,
                descriptor.ActivityType,
                descriptor.Config,
                descriptor.Hash))
            .ToList();
    }

    private static bool IsHandledHere(string activityType) => activityType is
        "scheduleTrigger" or "fileWatcherTrigger" or "databaseTrigger" or "eventLogTrigger";

    private ITriggerSource? CreateSource(string activityType) => activityType switch
    {
        "scheduleTrigger" => _rootServices.GetService<Sources.ScheduleTriggerSource>(),
        "fileWatcherTrigger" => new Sources.FileWatcherTriggerSource(
            _rootServices.GetRequiredService<ILogger<Sources.FileWatcherTriggerSource>>(),
            _rootServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()),
        "databaseTrigger" => new Sources.DatabaseTriggerSource(
            _rootServices.GetRequiredService<ILogger<Sources.DatabaseTriggerSource>>(),
            _rootServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()),
        "eventLogTrigger" => new Sources.EventLogTriggerSource(
            _rootServices.GetRequiredService<ILogger<Sources.EventLogTriggerSource>>(),
            _rootServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()),
        _ => null,
    };

    internal async Task FireAsync(Guid workflowId, string triggerType, Dictionary<string, string> parameters)
    {
        // Defensive race-protection: Quartz / FileSystemWatcher / EventLog can deliver a
        // pending fire microseconds AFTER we lost leadership and started disposing
        // sources. Dropping it here is the cheapest way to keep the "follower never fires"
        // invariant intact.
        if (!_cluster.IsLeader) return;

        using var fireActivity = SchedulerMetrics.Source.StartActivity("trigger.fire", System.Diagnostics.ActivityKind.Producer);
        fireActivity?.SetTag("nodepilot.trigger.type", triggerType);
        fireActivity?.SetTag("nodepilot.workflow.id", workflowId.ToString());

        SchedulerMetrics.TriggersFired.Add(1,
            new KeyValuePair<string, object?>("trigger_type", triggerType),
            new KeyValuePair<string, object?>("workflow_id", workflowId.ToString()));

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var wf = await db.Workflows.FindAsync(workflowId);
        if (wf is null || !wf.IsEnabled)
        {
            var reason = wf is null ? "workflow_deleted" : "workflow_disabled";
            _logger.LogWarning("Trigger fired for {Type} but workflow {Wf} is missing or disabled", triggerType, workflowId);
            fireActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "workflow missing or disabled");
            await AppendSuppressionAudit(db, workflowId, triggerType, reason);
            return;
        }

        // Maintenance-window gate. Early-skip here (rather than letting the dispatch choke point
        // create-then-cancel a row) so a schedule firing through a long blackout doesn't churn
        // out a Cancelled execution every interval. The dispatch gate is still the authoritative
        // backstop for the rare case a window opens between this check and worker pickup.
        var maintenance = _rootServices.GetService<IMaintenanceWindowEvaluator>();
        if (maintenance is not null)
        {
            var verdict = maintenance.Evaluate(wf.Id, wf.FolderId, DateTime.UtcNow);
            if (verdict.Blocked)
            {
                _logger.LogInformation(
                    "Trigger fire for {Type} on workflow {Wf} suppressed by maintenance window '{Window}'",
                    triggerType, workflowId, verdict.WindowName);
                fireActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok, "maintenance window");
                SchedulerMetrics.MaintenanceWindowBlocks.Add(1,
                    new KeyValuePair<string, object?>("trigger_type", triggerType));
                await AppendMaintenanceBlockAudit(db, workflowId, triggerType, verdict);
                return;
            }
        }

        var parametersSnapshot = parameters.Count == 0
            ? new Dictionary<string, string>(0)
            : new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        try
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IWorkflowExecutionDispatcher>();
            await dispatcher.DispatchAsync(
                new WorkflowDispatchIntent(
                    workflowId,
                    triggerType,
                    parametersSnapshot,
                    StartedByUserId: wf.PublishedByUserId,
                    RequireWorkflowEnabled: true,
                    MissingWorkflowMessage: "Queued trigger dispatch was not executed because the workflow no longer exists or is disabled.",
                    PreOwnershipFailurePrefix: "Queued trigger dispatch failed before the engine could take ownership",
                    EnqueueFailureMessage: "Queued trigger dispatch failed before enqueue.",
                    EnqueueFailureStatus: ExecutionStatus.Failed,
                    OnDispatchSuppressedAsync: async (suppression, _) =>
                    {
                        await using var auditScope = _scopeFactory.CreateAsyncScope();
                        var auditDb = auditScope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                        await AppendSuppressionAudit(auditDb, workflowId, triggerType, suppression.Reason);
                    }),
                _stoppingToken);
            fireActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trigger-started execution of {Wf} failed to enqueue", workflowId);
            fireActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            await AppendSuppressionAudit(db, workflowId, triggerType, "dispatch_exception");
        }
    }

    /// <summary>
    /// Persists an audit row for a trigger fire that did NOT produce a WorkflowExecution —
    /// either because the workflow is missing/disabled or because the engine dispatch threw.
    /// Closes the "silent drop" gap: without this entry, a disabled workflow's missed fire
    /// leaves no trace, which makes incident forensics ("why didn't my schedule run last
    /// night?") needlessly hard. Best-effort: an audit-write failure must not prevent the
    /// orchestrator from moving to the next trigger.
    ///
    /// Routes through <see cref="IAuditStager"/> so the redaction + 4 KiB cap apply
    /// uniformly. The previous string-interpolated JSON bypassed both — a malicious
    /// trigger-type or reason string would have landed unescaped + unredacted.
    /// </summary>
    private async Task AppendSuppressionAudit(NodePilotDbContext db, Guid workflowId, string triggerType, string reason)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var stager = scope.ServiceProvider.GetRequiredService<IAuditStager>();
            var auditEntry = stager.Build(
                action: AuditActions.TriggerFireSuppressed,
                actor: AuditActor.System,
                resourceType: "Workflow",
                resourceId: workflowId,
                details: AuditDetails.Json(
                    ("triggerType", triggerType),
                    ("reason", reason)));
            db.AuditLog.Add(auditEntry);
            await db.SaveChangesAsync();
            AuditEventForwarder.ForwardCommitted(_logger, auditEntry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append TRIGGER_FIRE_SUPPRESSED audit entry for workflow {Wf}", workflowId);
        }
    }

    /// <summary>
    /// Persists an audit row for a trigger fire dropped by an active maintenance window. Kept
    /// separate from <see cref="AppendSuppressionAudit"/> (distinct action code) so the audit
    /// timeline distinguishes "blocked by maintenance window" from "fired while disabled".
    /// System actor, best-effort — an audit-write failure must not block the orchestrator.
    /// </summary>
    private async Task AppendMaintenanceBlockAudit(
        NodePilotDbContext db, Guid workflowId, string triggerType, MaintenanceEvaluation verdict)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var stager = scope.ServiceProvider.GetRequiredService<IAuditStager>();
            var auditEntry = stager.Build(
                action: AuditActions.ExecutionBlockedMaintenanceWindow,
                actor: AuditActor.System,
                resourceType: "Workflow",
                resourceId: workflowId,
                details: AuditDetails.Json(
                    ("source", triggerType),
                    ("windowId", verdict.WindowId),
                    ("windowName", verdict.WindowName),
                    ("mode", verdict.Mode?.ToString()),
                    ("activeUntil", verdict.ActiveUntilUtc)));
            db.AuditLog.Add(auditEntry);
            await db.SaveChangesAsync();
            AuditEventForwarder.ForwardCommitted(_logger, auditEntry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append EXECUTION_BLOCKED_MAINTENANCE_WINDOW audit entry for workflow {Wf}", workflowId);
        }
    }
}

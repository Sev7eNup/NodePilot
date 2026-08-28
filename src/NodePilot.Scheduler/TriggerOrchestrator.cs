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
using NodePilot.Data.Availability;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;

namespace NodePilot.Scheduler;

/// <summary>
/// Background service that keeps external-trigger subscriptions in sync with the
/// workflow definitions in the database. On a fixed interval it:
///   1. Loads every enabled workflow
/// 2. Parses each for trigger nodes (scheduleTrigger / fileWatcherTrigger / databaseTrigger /
/// eventLogTrigger)
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

    // Key: $"{workflowId}:{nodeId}" -> (source, configHash)
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

    private readonly IDatabaseAvailability _availability;

    // Mirrors the broken half of _backoff into a shape the alerting pipeline can observe. Kept
    // separate rather than exposing _backoff itself: this carries the reason and the "since" the
    // alert needs, and it survives an eviction that has not yet produced a failed retry.
    private readonly TriggerHealthRegistry _health;

    public TriggerOrchestrator(
        IServiceScopeFactory scopeFactory,
        IServiceProvider rootServices,
        IClusterStateProvider cluster,
        ILogger<TriggerOrchestrator> logger,
        IDatabaseAvailability availability,
        TriggerHealthRegistry health)
    {
        _scopeFactory = scopeFactory;
        _rootServices = rootServices;
        _cluster = cluster;
        _availability = availability;
        _health = health;
        _logger = logger;
        SourceFactory = CreateSource;
        // Wake the sync loop immediately on leadership transitions so a freshly-promoted
        // node activates its triggers within milliseconds instead of waiting up to 5 s for
        // the next regular tick.
        _cluster.OnLeadershipAcquired += OnLeadershipAcquired;
        _cluster.OnLeadershipLost += OnLeadershipLost;
    }

    private readonly SemaphoreSlim _wakeSync = new(0, 1);

    private void OnLeadershipAcquired(long _) => WakeSyncLoop();

    private void OnLeadershipLost() => WakeSyncLoop();

    private void WakeSyncLoop()
    {
        if (_wakeSync.CurrentCount != 0) return;
        try { _wakeSync.Release(); }
        catch (SemaphoreFullException) { /* another transition already queued */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _logger.LogInformation("TriggerOrchestrator starting");
        while (!stoppingToken.IsCancellationRequested)
        {
            // A leadership-loss transition must tear sources down even while the database gate
            // is closed. The event wakes this loop; no database access is needed for disposal.
            await DisposeSourcesIfFollowerAsync();

            // Availability gate, deliberately ABOVE any leadership consideration: during an outage
            // no
            // node can renew its cluster lease, so every node reads as a follower — gating on
            // IsLeader
            // first would park for the right reason and report the wrong one.
            //
            // Returns false only on shutdown, and never throws: BackgroundServiceExceptionBehavior
            // is
            // left at its default StopHost, so an escaping OperationCanceledException here would
            // take
            // the whole host down on every service stop.
            if (!await WaitUntilServableOrLeadershipChangeAsync(stoppingToken)) break;

            try { await SyncAsync(stoppingToken); }
            catch (Exception ex)
            {
                // The breaker already logged the outage once, with a classified reason. Repeating
                // it
                // here every 5 seconds for the whole outage is what trained operators to ignore
                // this
                // log in the first place.
                if (_availability.IsServable) _logger.LogError(ex, "Trigger sync failed");
                else _logger.LogDebug(ex, "Trigger sync failed while the database is unavailable");
            }

            // Wait for either the regular tick OR an immediate wake-up from the cluster
            // (leadership acquired). When the wake fires, swap the TCS so subsequent
            // acquisitions can wake us again.
            if (!await WaitForTickOrLeadershipChangeAsync(stoppingToken)) break;
        }

        // Tear down all active sources on shutdown
        await DisposeActiveSourcesAsync();
        _logger.LogInformation("TriggerOrchestrator stopped");
    }

    private async Task DisposeSourcesIfFollowerAsync()
    {
        if (_cluster.IsLeader || _active.IsEmpty) return;

        _logger.LogInformation("Lost leadership — disposing {N} active trigger sources", _active.Count);
        await DisposeActiveSourcesAsync();
        _parseCache.Clear();
        _backoff.Clear();
        // A follower owns no triggers, so it has no broken ones to report. Leaving stale entries
        // would make this node alert on the leader's triggers.
        _health.Clear();
    }

    private async Task<bool> WaitUntilServableOrLeadershipChangeAsync(CancellationToken stoppingToken)
    {
        while (!_availability.IsServable && !stoppingToken.IsCancellationRequested)
        {
            using var iteration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var available = _availability.WaitUntilServableAsync(iteration.Token);
            var leadershipChanged = _wakeSync.WaitAsync(iteration.Token);
            var completed = await Task.WhenAny(available, leadershipChanged);
            iteration.Cancel();

            if (completed == leadershipChanged)
            {
                await ObserveCancellationAsync(available);
                await DisposeSourcesIfFollowerAsync();
                continue;
            }

            await ObserveCancellationAsync(leadershipChanged);
            if (!await available) return false;
        }

        return !stoppingToken.IsCancellationRequested;
    }

    private async Task<bool> WaitForTickOrLeadershipChangeAsync(CancellationToken stoppingToken)
    {
        using var iteration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delay = Task.Delay(SyncInterval, iteration.Token);
        var leadershipChanged = _wakeSync.WaitAsync(iteration.Token);
        var completed = await Task.WhenAny(delay, leadershipChanged);
        iteration.Cancel();
        await ObserveCancellationAsync(completed == delay ? leadershipChanged : delay);
        return !stoppingToken.IsCancellationRequested;
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Disposes every registered trigger source and empties the registry. A source can hold
    /// process-global state — <see cref="Sources.ScheduleTriggerSource"/> occupies a slot in the
    /// static <c>MaxActiveJobs</c> counter — so every teardown path has to run this. Internal
    /// rather than private because tests drive <see cref="SyncAsync"/> directly and never start
    /// the BackgroundService loop that owns the shutdown path above.
    /// </summary>
    internal async Task DisposeActiveSourcesAsync()
    {
        foreach (var (_, entry) in _active)
            try { await entry.source.DisposeAsync(); } catch { /* best effort */ }
        _active.Clear();
    }

    internal async Task SyncAsync(CancellationToken ct)
    {
        var syncStopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var syncActivity = SchedulerMetrics.Source.StartActivity("trigger.orchestrator.sync", System.Diagnostics.ActivityKind.Internal);
        try
        {
            await SyncInnerAsync(ct);
            syncActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            // The pass itself succeeding says nothing about the triggers it manages: a trigger
            // that cannot register (missing directory, unreachable share) is caught per-trigger
            // and lands in _backoff, which covers both "never started" and "died and was evicted".
            // Reporting plain "ok" while a drop folder goes unwatched is the false green this
            // whole change exists to remove.
            var retrying = _backoff.Count;
            await WriteHeartbeat(ct, status: retrying == 0 ? "ok" : $"degraded: {retrying} trigger(s) retrying");
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
        // Followers dispose active trigger sources so only the current leader can fire them.
        // Repeated follower ticks are cheap after the active set becomes empty.
        if (!_cluster.IsLeader)
        {
            if (!_active.IsEmpty)
            {
                _logger.LogInformation("Lost leadership — disposing {N} active trigger sources", _active.Count);
                await DisposeActiveSourcesAsync();
                _parseCache.Clear();
                _backoff.Clear();
                // A follower owns no triggers, so it has no broken ones to report. Leaving stale
                // entries would make this node alert on the leader's triggers.
                _health.Clear();
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

        // Remove obsolete / changed / dead
        foreach (var key in _active.Keys.ToList())
        {
            if (!_active.TryGetValue(key, out var current)) continue;

            var isGone = !desired.TryGetValue(key, out var want);
            var changed = !isGone && want.hash != current.configHash;

            // A source that started fine and later died — a FileSystemWatcher whose UNC share
            // vanished, a poll loop that exited — keeps a matching config hash forever, so
            // neither branch above ever evicts it and the add-loop below skips it as "already
            // registered". Evicting on the source's own liveness verdict routes it back through
            // that add-loop, which already retries with exponential backoff and heals by itself
            // once the underlying resource returns.
            // Short-circuited for sources being removed anyway: Health is contractually a pure
            // in-memory read, but there is no reason to ask a corpse we are already burying.
            var health = isGone || changed ? TriggerHealth.Healthy : current.source.Health;

            if ((isGone || changed || !health.IsHealthy) && _active.TryRemove(key, out var old))
            {
                if (!health.IsHealthy)
                    _logger.LogWarning(
                        "Evicting unhealthy {Type} trigger {Key}: {Reason}. Re-registering; while the " +
                        "underlying resource stays unavailable, registration backs off up to 5 minutes.",
                        old.source.ActivityType, key, health.Reason);
                else
                    // Deleted, disabled or reconfigured — whatever was wrong with it is moot now.
                    // (No health write on the unhealthy branch: the add-loop below re-registers in
                    // this same pass, so it always resolves to either MarkHealthy on success or
                    // MarkUnhealthy with a real failure count. A marker here would only ever be
                    // overwritten a few lines later.)
                    _health.MarkHealthy(key);

                try { await old.source.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed disposing trigger {Key}", key); }
                SchedulerMetrics.OrchestratorSyncChanges.Add(1,
                    new KeyValuePair<string, object?>("change",
                        !health.IsHealthy ? "evict-unhealthy" : changed ? "update" : "remove"),
                    new KeyValuePair<string, object?>("trigger_type", old.source.ActivityType));
            }
        }

        // Clean up backoff entries for triggers that no longer exist so we don't leak
        // memory for deleted workflows.
        foreach (var bkey in _backoff.Keys.ToList())
            if (!desired.ContainsKey(bkey))
            {
                _backoff.TryRemove(bkey, out _);
                _health.MarkHealthy(bkey);
            }

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

            ITriggerSource? src = SourceFactory(want.activityType);
            if (src is null) continue;
            var ctx = new TriggerContext
            {
                WorkflowId = want.wfId,
                NodeId = want.nodeId,
                Config = want.config,
                ConfigurationHash = want.hash,
                OnFire = parameters => FireAsync(want.wfId, want.activityType, parameters),
                OnDurableFire = signal => AdmitFireAsync(
                    want.wfId, want.nodeId, want.activityType, want.hash, signal),
                ReadCheckpoint = () => ReadCheckpointAsync(want.wfId, want.nodeId, want.hash),
                InitializeCheckpoint = checkpoint => InitializeCheckpointAsync(
                    want.wfId, want.nodeId, want.activityType, want.hash, checkpoint),
                SaveCheckpoint = checkpoint => SaveCheckpointAsync(
                    want.wfId, want.nodeId, want.activityType, want.hash, checkpoint),
            };
            try
            {
                await src.StartAsync(ctx, ct);
                _active[key] = (src, want.hash);
                // Clearing the backoff HERE, on success, is what lets a health-evicted source be
                // re-created immediately: no _active entry can ever have a live _backoff entry,
                // so the eviction above always lands in an add-loop that is free to retry at once.
                // Moving this line would silently break re-arming after an eviction.
                _backoff.TryRemove(key, out _);
                _health.MarkHealthy(key);
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
                _health.MarkUnhealthy(key, want.wfId, want.nodeId, want.activityType,
                    $"{ex.GetType().Name}: {ex.Message}", failures, DateTime.UtcNow);

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

    /// <summary>
    /// Builds a fresh source per (workflow, trigger-node) pair. Every source is constructed with
    /// <c>new</c> from root-resolved <b>singletons</b> — deliberately NOT resolved from the
    /// container. <see cref="ITriggerSource"/> is <see cref="IAsyncDisposable"/>, and a transient
    /// disposable resolved from the root provider is tracked by that provider for the whole
    /// process lifetime: every source this loop ever created would stay referenced (growing with
    /// each trigger add/update and each backoff retry) and would be disposed a second time at
    /// shutdown. The orchestrator owns each source's lifetime and disposes it in
    /// <see cref="SyncInnerAsync"/> / <see cref="ExecuteAsync"/>; the container must stay out of
    /// it.
    /// </summary>
    /// <summary>
    /// Test-only seam. The orchestrator MUST build its own sources (see <see
    /// cref="CreateSource"/>);
    /// this only lets a test substitute the factory to drive reconcile scenarios no real source can
    /// produce on demand — chiefly "a registered source reports unhealthy". Production never
    /// assigns it.
    /// </summary>
    internal Func<string, ITriggerSource?> SourceFactory { get; set; }

    private ITriggerSource? CreateSource(string activityType) => activityType switch
    {
        "scheduleTrigger" => new Sources.ScheduleTriggerSource(
            _rootServices.GetRequiredService<Quartz.ISchedulerFactory>(),
            _rootServices.GetRequiredService<ILogger<Sources.ScheduleTriggerSource>>(),
            _rootServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()),
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
        await AdmitFireAsync(
            workflowId,
            "__direct_test__",
            triggerType,
            "__direct_test_config__",
            new TriggerSignal($"direct:{Guid.NewGuid():N}", string.Empty, parameters));
    }

    internal async Task<TriggerCheckpoint?> ReadCheckpointAsync(
        Guid workflowId, string nodeId, string configurationHash)
    {
        if (!_availability.IsServable || !_cluster.IsLeader) return null;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var checkpoint = await db.TriggerDeliveryCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkflowId == workflowId && x.TriggerNodeId == nodeId);
        return checkpoint is null
               || !string.Equals(checkpoint.ConfigurationHash, configurationHash, StringComparison.Ordinal)
            ? null
            : new TriggerCheckpoint(checkpoint.Position, checkpoint.Version);
    }

    internal async Task<bool> InitializeCheckpointAsync(
        Guid workflowId,
        string nodeId,
        string triggerType,
        string configurationHash,
        TriggerCheckpoint checkpoint)
    {
        if (!_availability.IsServable || !_cluster.IsLeader) return false;
        var leaseEpoch = _cluster.LeaseEpoch;
        if (!StillOwnsLease(leaseEpoch)) return false;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            var entity = await db.TriggerDeliveryCheckpoints.SingleOrDefaultAsync(
                x => x.WorkflowId == workflowId && x.TriggerNodeId == nodeId,
                _stoppingToken);
            if (entity is not null
                && string.Equals(entity.ConfigurationHash, configurationHash, StringComparison.Ordinal))
                return true;
            if (!StillOwnsLease(leaseEpoch)) return false;

            if (entity is null)
            {
                entity = new TriggerDeliveryCheckpoint
                {
                    WorkflowId = workflowId,
                    TriggerNodeId = nodeId,
                };
                db.TriggerDeliveryCheckpoints.Add(entity);
            }
            entity.TriggerType = triggerType;
            entity.ConfigurationHash = configurationHash;
            entity.Position = checkpoint.Position;
            entity.Version = checkpoint.Version;
            entity.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(_stoppingToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // A leadership hand-off may race the initial insert. The unique composite key makes
            // the winner authoritative; the source can simply re-read it on its next pass.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not initialize durable checkpoint for {TriggerType} workflow {WorkflowId} node {NodeId}.",
                triggerType, workflowId, nodeId);
            return false;
        }
    }

    private async Task<bool> SaveCheckpointAsync(
        Guid workflowId,
        string nodeId,
        string triggerType,
        string configurationHash,
        TriggerCheckpoint checkpoint)
    {
        if (!_availability.IsServable || !_cluster.IsLeader) return false;
        var leaseEpoch = _cluster.LeaseEpoch;
        if (!StillOwnsLease(leaseEpoch)) return false;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            var entity = await db.TriggerDeliveryCheckpoints.SingleOrDefaultAsync(
                x => x.WorkflowId == workflowId && x.TriggerNodeId == nodeId,
                _stoppingToken);
            if (!StillOwnsLease(leaseEpoch)) return false;
            if (entity is null)
            {
                entity = new TriggerDeliveryCheckpoint
                {
                    WorkflowId = workflowId,
                    TriggerNodeId = nodeId,
                };
                db.TriggerDeliveryCheckpoints.Add(entity);
            }
            entity.TriggerType = triggerType;
            entity.ConfigurationHash = configurationHash;
            entity.Position = checkpoint.Position;
            entity.Version = checkpoint.Version;
            entity.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(_stoppingToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not persist durable checkpoint for {TriggerType} workflow {WorkflowId} node {NodeId}.",
                triggerType, workflowId, nodeId);
            return false;
        }
    }

    internal async Task<bool> AdmitFireAsync(
        Guid workflowId,
        string nodeId,
        string triggerType,
        string configurationHash,
        TriggerSignal signal)
    {
        // A source keeps or reconstructs the signal until this method returns true. Database
        // outage therefore means "retry pending", never "drop".
        if (!_availability.IsServable)
        {
            SchedulerMetrics.TriggerAdmissionsDeferred.Add(1,
                new KeyValuePair<string, object?>("trigger_type", triggerType));
            _logger.LogDebug(
                "Deferring {TriggerType} fire for workflow {WorkflowId}: the database is unavailable.",
                triggerType, workflowId);
            return false;
        }

        // Defensive race-protection: Quartz / FileSystemWatcher / EventLog can deliver a
        // pending fire microseconds AFTER we lost leadership and started disposing sources.
        // Returning false keeps the signal pending while preserving the "follower never fires"
        // invariant.
        if (!_cluster.IsLeader) return false;
        var leaseEpoch = _cluster.LeaseEpoch;
        if (!StillOwnsLease(leaseEpoch)) return false;

        using var fireActivity = SchedulerMetrics.Source.StartActivity("trigger.fire", System.Diagnostics.ActivityKind.Producer);
        fireActivity?.SetTag("nodepilot.trigger.type", triggerType);
        fireActivity?.SetTag("nodepilot.workflow.id", workflowId.ToString());

        SchedulerMetrics.TriggersFired.Add(1,
            new KeyValuePair<string, object?>("trigger_type", triggerType),
            new KeyValuePair<string, object?>("workflow_id", workflowId.ToString()));

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(_stoppingToken);
            if (await db.TriggerDeliveryReceipts.AnyAsync(x =>
                    x.WorkflowId == workflowId
                    && x.TriggerNodeId == nodeId
                    && x.EventKey == signal.EventKey,
                    _stoppingToken))
            {
                await transaction.CommitAsync(_stoppingToken);
                return true;
            }

            var wf = await db.Workflows.FindAsync([workflowId], _stoppingToken);
            if (wf is null)
            {
                _logger.LogWarning("Trigger fired for {Type} but workflow {Wf} no longer exists", triggerType, workflowId);
                if (StillOwnsLease(leaseEpoch))
                    await AppendSuppressionAudit(db, workflowId, triggerType, "workflow_deleted");
                await transaction.CommitAsync(_stoppingToken);
                return true;
            }

            var receipt = new TriggerDeliveryReceipt
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflowId,
                TriggerNodeId = nodeId,
                TriggerType = triggerType,
                EventKey = signal.EventKey,
                Outcome = "admitted",
                ReceivedAt = DateTime.UtcNow,
            };
            db.TriggerDeliveryReceipts.Add(receipt);

            if (!wf.IsEnabled)
            {
                const string reason = "workflow_disabled";
                receipt.Outcome = reason;
                _logger.LogWarning("Trigger fired for {Type} but workflow {Wf} is missing or disabled", triggerType, workflowId);
                fireActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "workflow missing or disabled");
                if (StillOwnsLease(leaseEpoch))
                    await AppendSuppressionAudit(db, workflowId, triggerType, reason);
                UpsertCheckpoint(db, workflowId, nodeId, triggerType, configurationHash, signal);
                await db.SaveChangesAsync(_stoppingToken);
                await transaction.CommitAsync(_stoppingToken);
                return true;
            }

            // Maintenance-window gate. A suppressed signal is nevertheless acknowledged and its
            // cursor advanced: replaying it after the window closes would violate blackout
            // semantics and create a surprise backfill.
            var maintenance = _rootServices.GetService<IMaintenanceWindowEvaluator>();
            if (maintenance is not null)
            {
                var verdict = maintenance.Evaluate(wf.Id, wf.FolderId, DateTime.UtcNow);
                if (verdict.Blocked)
                {
                    receipt.Outcome = "maintenance_window";
                    _logger.LogInformation(
                        "Trigger fire for {Type} on workflow {Wf} suppressed by maintenance window '{Window}'",
                        triggerType, workflowId, verdict.WindowName);
                    fireActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok, "maintenance window");
                    SchedulerMetrics.MaintenanceWindowBlocks.Add(1,
                        new KeyValuePair<string, object?>("trigger_type", triggerType));
                    if (StillOwnsLease(leaseEpoch))
                        await AppendMaintenanceBlockAudit(db, workflowId, triggerType, verdict);
                    UpsertCheckpoint(db, workflowId, nodeId, triggerType, configurationHash, signal);
                    await db.SaveChangesAsync(_stoppingToken);
                    await transaction.CommitAsync(_stoppingToken);
                    return true;
                }
            }

            var parametersSnapshot = signal.Parameters.Count == 0
                ? new Dictionary<string, string>(0)
                : new Dictionary<string, string>(signal.Parameters, StringComparer.OrdinalIgnoreCase);
            // Fence immediately before DispatchAsync persists its Pending execution. A node
            // that lost and re-acquired leadership has a different epoch and may not reuse a
            // fire observed under the old lease.
            if (!StillOwnsLease(leaseEpoch)) return false;
            var dispatcher = scope.ServiceProvider.GetRequiredService<IWorkflowExecutionDispatcher>();
            var execution = await dispatcher.DispatchAsync(
                new WorkflowDispatchIntent(
                    workflowId,
                    triggerType,
                    parametersSnapshot,
                    StartedByUserId: wf.PublishedByUserId,
                    RequireWorkflowEnabled: true,
                    MissingWorkflowMessage: "Queued trigger dispatch was not executed because the workflow no longer exists or is disabled.",
                    PreOwnershipFailurePrefix: "Queued trigger dispatch failed before the engine could take ownership",
                    OnDispatchSuppressedAsync: async (suppression, _) =>
                    {
                        await using var auditScope = _scopeFactory.CreateAsyncScope();
                        var auditDb = auditScope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                        await AppendSuppressionAudit(auditDb, workflowId, triggerType, suppression.Reason);
                    }),
                _stoppingToken);
            receipt.ExecutionId = execution.Id;
            UpsertCheckpoint(db, workflowId, nodeId, triggerType, configurationHash, signal);
            await db.SaveChangesAsync(_stoppingToken);
            await transaction.CommitAsync(_stoppingToken);
            fireActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            return true;
        }
        catch (Exception ex)
        {
            SchedulerMetrics.TriggerAdmissionsDeferred.Add(1,
                new KeyValuePair<string, object?>("trigger_type", triggerType));
            _logger.LogWarning(ex,
                "Trigger-started execution of {Wf} was not durably admitted; the source will retry or reconcile it.",
                workflowId);
            fireActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
            return false;
        }
    }

    private static void UpsertCheckpoint(
        NodePilotDbContext db,
        Guid workflowId,
        string nodeId,
        string triggerType,
        string configurationHash,
        TriggerSignal signal)
    {
        var checkpoint = db.TriggerDeliveryCheckpoints.Local.FirstOrDefault(x =>
                             x.WorkflowId == workflowId && x.TriggerNodeId == nodeId)
                         ?? db.TriggerDeliveryCheckpoints.Find(workflowId, nodeId);
        if (checkpoint is null)
        {
            db.TriggerDeliveryCheckpoints.Add(new TriggerDeliveryCheckpoint
            {
                WorkflowId = workflowId,
                TriggerNodeId = nodeId,
                TriggerType = triggerType,
                ConfigurationHash = configurationHash,
                Position = signal.Position,
                Version = signal.EventKey,
                UpdatedAt = DateTime.UtcNow,
            });
            return;
        }

        checkpoint.TriggerType = triggerType;
        checkpoint.ConfigurationHash = configurationHash;
        checkpoint.Position = signal.Position;
        checkpoint.Version = signal.EventKey;
        checkpoint.UpdatedAt = DateTime.UtcNow;
    }

    private bool StillOwnsLease(long leaseEpoch)
        => _cluster.IsLeader && _cluster.LeaseEpoch == leaseEpoch;

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

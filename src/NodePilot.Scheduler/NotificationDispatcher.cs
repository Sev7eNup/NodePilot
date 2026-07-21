using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Notifications;
using NodePilot.Scheduler.Notifications;
using NodePilot.Scheduler.SystemAlerts;

namespace NodePilot.Scheduler;

/// <summary>
/// Leader-gated alerting dispatcher. Each pass: (1) re-sends any Pending delivery attempts left
/// over from a crashed pass, then (2) runs the execution-family <see cref="INotificationCollector"/>s
/// (terminal executions since the persisted watermark, long-running, queued-long) for CUSTOM rules,
/// AND the <see cref="NodePilot.Scheduler.SystemAlerts.SystemAlertEvaluator"/> for SYSTEM policies —
/// both through the SHARED match → suppress → persist-Pending → send pipeline: a Pending
/// <see cref="NotificationDeliveryAttempt"/> per route (idempotent on (rule,route,eventKey)) persisted
/// BEFORE any network I/O, and only then send.
///
/// <para>Infra/signal alerts (backlog, machine health, credential expiry, schedule health, …) are no
/// longer a legacy gauge collector: they are modular <c>ISystemAlertSource</c>s evaluated per system
/// policy (ADR 0008). The collectors own their EventKey shapes (collection AND crash recovery); this
/// class owns the pass loop and pipeline. The execution watermark is seeded to "now" on first run, and
/// system policies stamp an activation watermark, so existing history is never back-alerted.</para>
/// </summary>
public class NotificationDispatcher : BackgroundService
{
    private const int RecoverBatchSize = 100;
    private const int MaxAttempts = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClusterStateProvider _cluster;
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationSink> _sinks;
    private readonly ILogger<NotificationDispatcher> _logger;
    private readonly TimeSpan _interval;

    // The pass order is deliberate: execution watermark first (the primary source), then the
    // signal/derived collectors. Each collector also answers recovery for its own EventKey shape.
    private readonly ExecutionEventCollector _executionCollector;
    private readonly LongRunningExecutionCollector _longRunningCollector;
    private readonly QueuedLongExecutionCollector _queuedLongCollector;
    private readonly IReadOnlyList<INotificationCollector> _collectors;

    // System-alert policies (ADR 0008) run on their own evaluator (source sampling + per-policy sustain /
    // episode state), then reuse this dispatcher's suppress → persist-Pending → send primitives.
    private readonly SystemAlertEvaluator _systemEvaluator;

    // Test seams — forwarded to the owning collector so tests keep configuring the dispatcher.
    internal TimeSpan ScanSafetyLag
    {
        get => _executionCollector.ScanSafetyLag;
        set => _executionCollector.ScanSafetyLag = value;
    }

    internal TimeSpan LongRunningThreshold
    {
        get => _longRunningCollector.Threshold;
        set => _longRunningCollector.Threshold = value;
    }

    internal TimeSpan QueuedLongThreshold
    {
        get => _queuedLongCollector.Threshold;
        set => _queuedLongCollector.Threshold = value;
    }

    public NotificationDispatcher(
        IServiceScopeFactory scopeFactory,
        IClusterStateProvider cluster,
        IEnumerable<INotificationSink> sinks,
        ISystemAlertCatalog systemAlertCatalog,
        IConfiguration configuration,
        ILogger<NotificationDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _cluster = cluster;
        _systemEvaluator = new SystemAlertEvaluator(systemAlertCatalog);
        // Last-registered-wins per channel (so a deployment can override a sink).
        var map = new Dictionary<NotificationChannel, INotificationSink>();
        foreach (var s in sinks) map[s.Channel] = s;
        _sinks = map;
        _logger = logger;
        _interval = TimeSpan.FromSeconds(30);

        _executionCollector = new ExecutionEventCollector();
        _longRunningCollector = new LongRunningExecutionCollector(configuration);
        _queuedLongCollector = new QueuedLongExecutionCollector(configuration);
        _collectors =
        [
            _executionCollector,
            _longRunningCollector,
            _queuedLongCollector,
        ];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_cluster.IsLeader)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                var sent = await DispatchOnceAsync(stoppingToken);
                await HeartbeatAsync($"ok: {sent} sent", stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification dispatch pass failed — retrying next interval.");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Exposed for tests: one full pass (recover leftovers + collect/match/send new).
    internal async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<INotificationRuleStore>();

        var total = await RecoverPendingAsync(db, store, ct);

        var rules = await store.GetEnabledAsync(ct);
        foreach (var collector in _collectors)
        {
            var now = DateTime.UtcNow;
            var collection = await collector.CollectAsync(db, rules, now, ct);
            if (collection is null) continue;

            var suppressionCache = new Dictionary<(Guid, string), NotificationSuppressionState>();
            total += await MatchAndSendAsync(db, store, collection.Rules, collection.Contexts, suppressionCache, now, ct);
        }

        total += await DispatchSystemAlertsAsync(db, store, rules, ct);
        return total;
    }

    // System-alert policies: prune state for disabled/removed policies, evaluate the rest (staging match/episode
    // state), then deliver every fire through the shared suppress → persist-Pending → send primitives. The
    // single SaveChanges flushes the evaluator's state mutations together with the Pending attempts.
    private async Task<int> DispatchSystemAlertsAsync(
        NodePilotDbContext db, INotificationRuleStore store, IReadOnlyList<NotificationRule> rules, CancellationToken ct)
    {
        var systemPolicies = rules.Where(r => r.Kind == NotificationRuleKind.System).ToList();
        await _systemEvaluator.PruneOrphanedStateAsync(db, systemPolicies.Select(p => p.Id).ToList(), ct);

        var now = DateTime.UtcNow;
        var fires = await _systemEvaluator.EvaluateAsync(db, systemPolicies, now, ct);

        var toSend = new List<(NotificationDeliveryAttempt attempt, NotificationRoute route, NotificationContext ctx)>();
        var suppressionCache = new Dictionary<(Guid, string), NotificationSuppressionState>();
        foreach (var (policy, ctx) in fires)
        {
            var dedupKey = NotificationRuleSemantics.BuildDedupKey(policy, ctx);
            var supp = await GetSuppressionAsync(db, suppressionCache, policy.Id, dedupKey, ct);
            if (!ShouldFire(supp, policy, now)) continue;
            supp.LastFiredAt = now;

            foreach (var route in NotificationRuleSemantics.MatchingRoutes(policy, ctx))
            {
                var eventKey = ctx.EventKey;
                var exists = await db.NotificationDeliveryAttempts
                    .AnyAsync(a => a.NotificationRuleId == policy.Id && a.NotificationRouteId == route.Id && a.EventKey == eventKey, ct);
                if (exists) continue;
                var attempt = new NotificationDeliveryAttempt
                {
                    Id = Guid.NewGuid(),
                    NotificationRuleId = policy.Id,
                    NotificationRouteId = route.Id,
                    EventKey = eventKey,
                    DedupKey = dedupKey,
                    Status = NotificationDeliveryStatus.Pending,
                    CreatedAt = now,
                };
                db.NotificationDeliveryAttempts.Add(attempt);
                toSend.Add((attempt, route, ctx));
            }
        }

        // Always save — flushes evaluator-staged match/episode state even when nothing fires this pass.
        await db.SaveChangesAsync(ct);

        var sent = 0;
        foreach (var (attempt, route, ctx) in toSend)
        {
            await SendOneAsync(db, store, attempt, route, ctx, now, ct);
            if (attempt.Status == NotificationDeliveryStatus.Sent) sent++;
        }
        if (toSend.Count > 0) await db.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>
    /// Shared match → suppress → persist-Pending → send pipeline for any collector's contexts.
    /// One SaveChanges flushes the Pending attempts together with whatever state the collector has
    /// already staged on the tracked context (execution watermark / gauge signal-states) —
    /// preserving the persist-before-send crash-safety. Then each attempt is sent and statuses saved.
    /// </summary>
    private async Task<int> MatchAndSendAsync(
        NodePilotDbContext db, INotificationRuleStore store, IReadOnlyList<NotificationRule> rules,
        IReadOnlyList<NotificationContext> contexts,
        Dictionary<(Guid, string), NotificationSuppressionState> suppressionCache, DateTime now, CancellationToken ct)
    {
        var toSend = new List<(NotificationDeliveryAttempt attempt, NotificationRoute route, NotificationContext ctx)>();
        foreach (var ctx in contexts)
        {
            foreach (var rule in rules)
            {
                if (!NotificationRuleSemantics.RuleMatches(rule, ctx)) continue;

                var matchingRoutes = NotificationRuleSemantics.MatchingRoutes(rule, ctx);
                if (matchingRoutes.Count == 0) continue;

                var dedupKey = NotificationRuleSemantics.BuildDedupKey(rule, ctx);
                var supp = await GetSuppressionAsync(db, suppressionCache, rule.Id, dedupKey, ct);
                if (!ShouldFire(supp, rule, now)) continue;
                supp.LastFiredAt = now;

                foreach (var route in matchingRoutes)
                {
                    var eventKey = ctx.EventKey;
                    var exists = await db.NotificationDeliveryAttempts
                        .AnyAsync(a => a.NotificationRuleId == rule.Id && a.NotificationRouteId == route.Id && a.EventKey == eventKey, ct);
                    if (exists) continue;
                    var attempt = new NotificationDeliveryAttempt
                    {
                        Id = Guid.NewGuid(),
                        NotificationRuleId = rule.Id,
                        NotificationRouteId = route.Id,
                        EventKey = eventKey,
                        DedupKey = dedupKey,
                        Status = NotificationDeliveryStatus.Pending,
                        CreatedAt = now,
                    };
                    db.NotificationDeliveryAttempts.Add(attempt);
                    toSend.Add((attempt, route, ctx));
                }
            }
        }

        // Always save once — flushes the collector's staged state (watermark / signal-states) even
        // when nothing matched, plus the Pending attempts.
        await db.SaveChangesAsync(ct);

        var sent = 0;
        foreach (var (attempt, route, ctx) in toSend)
        {
            await SendOneAsync(db, store, attempt, route, ctx, now, ct);
            if (attempt.Status == NotificationDeliveryStatus.Sent) sent++;
        }
        if (toSend.Count > 0) await db.SaveChangesAsync(ct);
        return sent;
    }

    // Re-send Pending attempts orphaned by a crash between persist and send. Context is re-derived
    // by the collector that owns the attempt's EventKey shape; if the source row is gone
    // (retention), the attempt is failed out so it doesn't loop forever.
    private async Task<int> RecoverPendingAsync(NodePilotDbContext db, INotificationRuleStore store, CancellationToken ct)
    {
        var pending = await db.NotificationDeliveryAttempts
            .Where(a => a.Status == NotificationDeliveryStatus.Pending && !a.IsTest && a.Attempt < MaxAttempts)
            .OrderBy(a => a.CreatedAt)
            .Take(RecoverBatchSize)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var now = DateTime.UtcNow;
        var routeIds = pending.Select(a => a.NotificationRouteId).Distinct().ToList();
        var routes = await db.NotificationRoutes.AsNoTracking().Where(r => routeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, ct);

        var sent = 0;
        foreach (var attempt in pending)
        {
            if (!routes.TryGetValue(attempt.NotificationRouteId, out var route))
            {
                attempt.Status = NotificationDeliveryStatus.Failed;
                attempt.Error = "route no longer exists";
                continue;
            }
            var ctx = await ReconstructContextAsync(db, attempt.EventKey, ct);
            if (ctx is null)
            {
                attempt.Status = NotificationDeliveryStatus.Failed;
                attempt.Error = "source execution no longer available";
                continue;
            }
            await SendOneAsync(db, store, attempt, route, ctx, now, ct);
            if (attempt.Status == NotificationDeliveryStatus.Sent) sent++;
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    private async Task<NotificationContext?> ReconstructContextAsync(NodePilotDbContext db, string eventKey, CancellationToken ct)
    {
        // EventKey prefixes are disjoint per collector; first non-null answer wins.
        foreach (var collector in _collectors)
        {
            if (await collector.TryReconstructContextAsync(db, eventKey, ct) is { } ctx)
                return ctx;
        }
        // System-alert episodes use the disjoint "system:" prefix.
        return await _systemEvaluator.TryReconstructContextAsync(db, eventKey, ct);
    }

    private async Task SendOneAsync(NodePilotDbContext db, INotificationRuleStore store,
        NotificationDeliveryAttempt attempt, NotificationRoute route, NotificationContext ctx, DateTime now, CancellationToken ct)
    {
        attempt.Attempt++;
        attempt.SentAt = now;
        attempt.Summary = $"{ctx.EventType} → {route.Channel}:{route.Target}";

        if (!_sinks.TryGetValue(route.Channel, out var sink))
        {
            // No sink for this channel is a permanent (non-transient) failure — don't burn retries.
            attempt.Status = NotificationDeliveryStatus.Failed;
            attempt.Error = $"no sink registered for channel {route.Channel}";
            return;
        }

        NotificationSendResult result;
        try
        {
            var secret = string.IsNullOrEmpty(route.Secret) ? null : await store.GetRouteSecretAsync(route.Id, ct);
            result = await sink.SendAsync(ctx, route.Target, secret, ct);
        }
        catch (Exception ex)
        {
            // A throwing secret-decrypt (corrupt cipher / key rotation) must not abort the whole pass
            // or wedge the dispatcher — treat it as a bounded-retry failure like any other.
            result = NotificationSendResult.Fail(ex.Message);
        }

        attempt.Error = result.Error;
        // Keep the attempt re-queueable (Pending) on failure until MaxAttempts so a transient sink /
        // endpoint outage is retried by RecoverPendingAsync on later passes; only then give up (Failed).
        // This makes the bounded-retry machinery live and honours at-least-once for sink-reported
        // failures, not only for crashes between persist and send.
        attempt.Status = result.Success
            ? NotificationDeliveryStatus.Sent
            : attempt.Attempt >= MaxAttempts ? NotificationDeliveryStatus.Failed : NotificationDeliveryStatus.Pending;
        _ = db; // attempt is tracked; caller saves.
    }

    private async Task<NotificationSuppressionState> GetSuppressionAsync(
        NodePilotDbContext db, Dictionary<(Guid, string), NotificationSuppressionState> cache,
        Guid ruleId, string dedupKey, CancellationToken ct)
    {
        if (cache.TryGetValue((ruleId, dedupKey), out var cached)) return cached;
        var state = await db.NotificationSuppressionStates
            .FirstOrDefaultAsync(s => s.NotificationRuleId == ruleId && s.DedupKey == dedupKey, ct);
        if (state is null)
        {
            state = new NotificationSuppressionState { Id = Guid.NewGuid(), NotificationRuleId = ruleId, DedupKey = dedupKey };
            db.NotificationSuppressionStates.Add(state);
        }
        cache[(ruleId, dedupKey)] = state;
        return state;
    }

    // Cooldown + flap suppression. Returns true when the rule may fire for this occurrence.
    private static bool ShouldFire(NotificationSuppressionState supp, NotificationRule rule, DateTime now)
    {
        if (rule.CooldownMinutes > 0 && supp.LastFiredAt is { } last && now < last.AddMinutes(rule.CooldownMinutes))
            return false;

        if (rule.MinOccurrences > 1 && rule.OccurrenceWindowMinutes > 0)
        {
            if (supp.WindowStartedAt is not { } start || now > start.AddMinutes(rule.OccurrenceWindowMinutes))
            {
                supp.WindowStartedAt = now;
                supp.OccurrenceCount = 0;
            }
            supp.OccurrenceCount++;
            if (supp.OccurrenceCount < rule.MinOccurrences) return false;
            supp.OccurrenceCount = 0; // reset after firing
        }
        return true;
    }

    private async Task HeartbeatAsync(string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        await SystemHealthWriter.BeatAsync(db, "NotificationDispatcher",
            expectedIntervalSeconds: (int)_interval.TotalSeconds, status: status, ct: ct);
    }
}

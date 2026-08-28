using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;

namespace NodePilot.Scheduler.Sources;

/// <summary>
/// Schedules a workflow on a cron expression using Quartz.NET.
/// Config keys:
///   cronExpression (required) — Quartz cron syntax (7-field, e.g. "0 0/5 * * * ?")
///
/// Rate-limit defaults: the cron must fire at most once every
/// <c>Trigger:Schedule:MinIntervalSeconds</c> seconds (default 60). A globally-registered
/// job-count cap (<c>Trigger:Schedule:MaxActiveJobs</c>, default 500) prevents a
/// rogue workflow set from saturating Quartz.
/// </summary>
public class ScheduleTriggerSource : ITriggerSource
{
    public string ActivityType => "scheduleTrigger";

    /// <summary>
    /// Always healthy: Quartz owns job liveness process-wide, not per source. A scheduler that
    /// shut down is not a per-trigger condition — it would take every schedule trigger down at
    /// once, and re-creating them one by one against a dead scheduler would not help. There is
    /// deliberately no per-job probe here.
    /// </summary>
    public TriggerHealth Health => TriggerHealth.Healthy;

    private readonly ISchedulerFactory _schedulerFactory;
    private readonly ILogger<ScheduleTriggerSource> _logger;
    private readonly IConfiguration _config;
    private JobKey? _jobKey;
    private TriggerKey? _triggerKey;
    private CancellationTokenSource? _deliveryCts;
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private TriggerCheckpoint? _checkpoint;

    // Global counter across all ScheduleTriggerSource instances in this process. Each
    // StartAsync increments; DisposeAsync decrements. Simple and good enough — a few
    // over-the-cap racers won't hurt, and the cap is a safety net, not a quota.
    private static int _activeJobCount;

    // Guards the decrement so it pairs with the increment in StartAsync exactly once (0/1,
    // flipped via Interlocked so a concurrent double-dispose can't double-decrement).
    // Keying the decrement off _jobKey instead got this wrong in both directions: a second
    // DisposeAsync decremented again, and a StartAsync that failed between the increment and
    // the _jobKey assignment never decremented at all. Either way _activeJobCount drifts off
    // the real number of jobs, and once it drifts negative the MaxActiveJobs cap silently
    // stops rejecting anything.
    private int _holdsJobSlot;

    public ScheduleTriggerSource(ISchedulerFactory schedulerFactory, ILogger<ScheduleTriggerSource> logger, IConfiguration config)
    {
        _schedulerFactory = schedulerFactory;
        _logger = logger;
        _config = config;
    }

    public async Task StartAsync(TriggerContext context, CancellationToken ct)
    {
        var cron = context.Config.TryGetProperty("cronExpression", out var c) ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(cron))
            throw new InvalidOperationException("ScheduleTrigger: 'cronExpression' is required");

        // Validate cron syntax + minimum interval BEFORE touching the scheduler so a rogue
        // workflow can't partially-register a job and then throw.
        CronExpression parsed;
        try { parsed = new CronExpression(cron); }
        catch (FormatException ex) { throw new InvalidOperationException($"ScheduleTrigger: invalid cron '{cron}': {ex.Message}"); }
        parsed.TimeZone = TimeZoneInfo.Local;

        var minIntervalSeconds = _config.GetValue<int?>("Trigger:Schedule:MinIntervalSeconds") ?? 60;
        if (minIntervalSeconds > 1)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var n1 = parsed.GetNextValidTimeAfter(nowUtc);
            var n2 = n1.HasValue ? parsed.GetNextValidTimeAfter(n1.Value) : null;
            if (n1.HasValue && n2.HasValue)
            {
                var interval = (n2.Value - n1.Value).TotalSeconds;
                if (interval < minIntervalSeconds)
                    throw new InvalidOperationException(
                        $"ScheduleTrigger: cron '{cron}' fires every {interval:F0}s which is below the " +
                        $"configured minimum of {minIntervalSeconds}s (Trigger:Schedule:MinIntervalSeconds).");
            }
        }

        var maxActive = _config.GetValue<int?>("Trigger:Schedule:MaxActiveJobs") ?? 500;
        if (Interlocked.Increment(ref _activeJobCount) > maxActive)
        {
            Interlocked.Decrement(ref _activeJobCount);
            throw new InvalidOperationException(
                $"ScheduleTrigger: maximum number of active cron jobs ({maxActive}) reached. " +
                "Disable unused schedule triggers or raise Trigger:Schedule:MaxActiveJobs.");
        }

        // The increment above is now this instance's to release, whatever happens below.
        _holdsJobSlot = 1;

        _deliveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var deliveryCt = _deliveryCts.Token;

        _checkpoint = await context.ReadCheckpointAsync();
        if (_checkpoint is null)
        {
            var position = DateTimeOffset.UtcNow.ToString("O");
            var seeded = new TriggerCheckpoint(position, $"schedule-seed:{Guid.NewGuid():N}");
            if (!await context.InitializeCheckpointAsync(seeded))
                throw new InvalidOperationException("ScheduleTrigger: durable cursor could not be initialized");
            _checkpoint = seeded;
        }
        else
        {
            await SkipMissedFiresAsync(context, parsed);
        }

        var scheduler = await _schedulerFactory.GetScheduler(ct);
        _jobKey = new JobKey($"wf-{context.WorkflowId}-{context.NodeId}", "nodepilot");
        _triggerKey = new TriggerKey($"trg-{context.WorkflowId}-{context.NodeId}", "nodepilot");

        ScheduleJob.Register(_jobKey.ToString(), async parameters =>
        {
            if (!DateTimeOffset.TryParse(
                    parameters["firedAt"],
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var firedAt))
                firedAt = DateTimeOffset.UtcNow;
            await DeliverAtAsync(context, firedAt, parameters.GetValueOrDefault("nextFireAt", ""), deliveryCt);
        });

        var job = JobBuilder.Create<ScheduleJob>()
            .WithIdentity(_jobKey)
            .UsingJobData("callbackKey", _jobKey.ToString())
            .Build();

        // Missed fires are skipped, not replayed, so Quartz's own misfire callback stays off and
        // the durable cursor is fast-forwarded instead. A schedule tick carries no data — running
        // an hour's worth of them at once helps nobody and buries the live workload.
        var trigger = TriggerBuilder.Create()
            .WithIdentity(_triggerKey)
            .WithCronSchedule(cron, x => x
                .InTimeZone(TimeZoneInfo.Local)
                .WithMisfireHandlingInstructionDoNothing())
            .Build();

        await scheduler.ScheduleJob(job, trigger, ct);
        _logger.LogInformation("ScheduleTrigger: scheduled {Job} with cron '{Cron}'", _jobKey, cron);
    }

    /// <summary>
    /// Moves the durable cursor to now without delivering anything, so a restart or failover does
    /// not turn a downtime window into a burst of runs. The skipped ticks are counted and logged
    /// because an outage must not vanish silently.
    /// <para>
    /// The cursor is written with <c>SaveCheckpointAsync</c>, not the seeding call:
    /// <c>InitializeCheckpointAsync</c> returns early for an existing row whose configuration hash
    /// matches and would leave the position untouched. If the write fails the source must not go
    /// live — it would deliver current fires while the cursor still points into the past, and the
    /// next start would replay exactly the window this method skipped.
    /// </para>
    /// </summary>
    private async Task SkipMissedFiresAsync(TriggerContext context, CronExpression cron)
    {
        if (_checkpoint is null) return;

        var now = DateTimeOffset.UtcNow;
        var skipped = 0;
        if (DateTimeOffset.TryParse(
                _checkpoint.Position,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var cursor))
        {
            var at = cursor;
            while (cron.GetNextValidTimeAfter(at) is { } next && next <= now)
            {
                skipped++;
                at = next;
            }
        }

        var advanced = new TriggerCheckpoint(now.ToString("O"), $"schedule-skip:{Guid.NewGuid():N}");
        if (!await context.SaveCheckpointAsync(advanced))
            throw new InvalidOperationException(
                "ScheduleTrigger: durable cursor could not be advanced past the missed window");
        _checkpoint = advanced;

        if (skipped == 0) return;

        SchedulerMetrics.TriggerFiresSkipped.Add(skipped,
            new KeyValuePair<string, object?>("trigger_type", ActivityType));
        _logger.LogWarning(
            "ScheduleTrigger: skipped {Skipped} missed fire(s) for workflow {WorkflowId} node {NodeId} "
            + "between {From:u} and {To:u}. Missed schedule ticks are never replayed.",
            skipped, context.WorkflowId, context.NodeId, cursor, now);
    }

    private async Task DeliverAtAsync(
        TriggerContext context,
        DateTimeOffset firedAt,
        string nextFireAt,
        CancellationToken ct)
    {
        await _deliveryGate.WaitAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested
                   && !await DeliverAtCoreAsync(context, firedAt, nextFireAt))
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private async Task<bool> DeliverAtCoreAsync(
        TriggerContext context,
        DateTimeOffset firedAt,
        string nextFireAt)
    {
        var utc = firedAt.ToUniversalTime();
        var position = utc.ToString("O");
        var signal = new TriggerSignal(
            $"schedule:{utc.UtcTicks}",
            position,
            new Dictionary<string, string>
            {
                ["firedAt"] = position,
                ["nextFireAt"] = nextFireAt,
            });
        var accepted = await context.DeliverAsync(signal);
        if (accepted) _checkpoint = new TriggerCheckpoint(position, signal.EventKey);
        return accepted;
    }

    public async ValueTask DisposeAsync()
    {
        // No slot held -> nothing to release. Covers "never started" and every dispose after
        // the first, so the orchestrator's several teardown paths can overlap harmlessly.
        if (Interlocked.Exchange(ref _holdsJobSlot, 0) == 0) return;

        var jobKey = _jobKey;
        _jobKey = null;
        try
        {
            if (_deliveryCts is not null)
            {
                await _deliveryCts.CancelAsync();
                _deliveryCts.Dispose();
                _deliveryCts = null;
            }
            // jobKey is null when StartAsync failed after taking the slot but before
            // scheduling — there is nothing to unschedule, but the slot still must go back.
            if (jobKey is not null)
            {
                var scheduler = await _schedulerFactory.GetScheduler();
                await scheduler.DeleteJob(jobKey);
                ScheduleJob.Unregister(jobKey.ToString()!);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to unschedule {Job}", jobKey); }
        finally { Interlocked.Decrement(ref _activeJobCount); }
    }
}

/// <summary>Quartz job that looks up the OnFire callback by key and invokes it.</summary>
[DisallowConcurrentExecution]
public class ScheduleJob : IJob
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<Dictionary<string, string>, Task>> _callbacks = new();

    public static void Register(string key, Func<Dictionary<string, string>, Task> callback) => _callbacks[key] = callback;
    public static bool Unregister(string key) => _callbacks.TryRemove(key, out _);

    public async Task Execute(IJobExecutionContext context)
    {
        var key = context.JobDetail.JobDataMap.GetString("callbackKey");
        if (key is null || !_callbacks.TryGetValue(key, out var cb)) return;
        await cb(new Dictionary<string, string>
        {
            ["firedAt"] = context.FireTimeUtc.UtcDateTime.ToString("O"),
            ["nextFireAt"] = (context.NextFireTimeUtc?.UtcDateTime.ToString("O")) ?? "",
        });
    }
}

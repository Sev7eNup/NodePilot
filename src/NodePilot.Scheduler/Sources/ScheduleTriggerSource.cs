using System.Text.Json;
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

    private readonly ISchedulerFactory _schedulerFactory;
    private readonly ILogger<ScheduleTriggerSource> _logger;
    private readonly IConfiguration _config;
    private JobKey? _jobKey;
    private TriggerKey? _triggerKey;

    // Global counter across all ScheduleTriggerSource instances in this process. Each
    // StartAsync increments; DisposeAsync decrements. Simple and good enough — a few
    // over-the-cap racers won't hurt, and the cap is a safety net, not a quota.
    private static int _activeJobCount;

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

        var scheduler = await _schedulerFactory.GetScheduler(ct);
        _jobKey = new JobKey($"wf-{context.WorkflowId}-{context.NodeId}", "nodepilot");
        _triggerKey = new TriggerKey($"trg-{context.WorkflowId}-{context.NodeId}", "nodepilot");

        // Store the OnFire callback in JobDataMap so the job can invoke it
        ScheduleJob.Register(_jobKey.ToString(), context.OnFire);

        var job = JobBuilder.Create<ScheduleJob>()
            .WithIdentity(_jobKey)
            .UsingJobData("callbackKey", _jobKey.ToString())
            .Build();

        // M-15: explicit misfire policy = DoNothing. Without this, Quartz's default
        // "SmartPolicy" resolves to "fire once immediately" for cron triggers after
        // downtime. For a workflow that fires "every 5 minutes" and is down for 6 hours,
        // that still only produces one backfill — but the misfire is counted even when
        // missing by a few seconds, which surprises operators. DoNothing = "if the fire
        // time is past by the misfire-threshold, skip it and wait for the next regular
        // fire". Prevents a post-deploy backfill storm.
        var trigger = TriggerBuilder.Create()
            .WithIdentity(_triggerKey)
            .WithCronSchedule(cron, x => x
                .InTimeZone(TimeZoneInfo.Local)
                .WithMisfireHandlingInstructionDoNothing())
            .Build();

        await scheduler.ScheduleJob(job, trigger, ct);
        _logger.LogInformation("ScheduleTrigger: scheduled {Job} with cron '{Cron}'", _jobKey, cron);
    }

    public async ValueTask DisposeAsync()
    {
        if (_jobKey is null) return;
        try
        {
            var scheduler = await _schedulerFactory.GetScheduler();
            await scheduler.DeleteJob(_jobKey);
            ScheduleJob.Unregister(_jobKey.ToString()!);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to unschedule {Job}", _jobKey); }
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

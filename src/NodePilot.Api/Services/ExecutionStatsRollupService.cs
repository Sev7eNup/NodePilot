using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Engine.Security;

namespace NodePilot.Api.Services;

/// <summary>
/// Rolls raw executions up into hourly buckets so the dashboard can answer from precomputed rows
/// instead of scanning history.
///
/// <para>
/// Why this exists: the dashboard's historical aggregates cost tens of seconds once an instance has
/// millions of executions, and no request-scoped cache fixes that — the computation itself is the
/// problem. A completed hour never changes, so it can be summarised once and afterwards only summed.
/// Nachführen then touches one hour's worth of rows (thousands) instead of the whole table
/// (millions), which is what makes keeping the dashboard warm affordable at all.
/// </para>
///
/// <para>
/// Lives in the API rather than the Scheduler because it reuses <see cref="DashboardFailureCauses"/>
/// for message normalisation; <c>Scheduler -&gt; Api</c> would invert the dependency graph. Several
/// background services already live here for the same kind of reason.
/// </para>
/// </summary>
public sealed class ExecutionStatsRollupService : BackgroundService
{
    /// <summary>Fixed id of the single bookkeeping row.</summary>
    internal const int StateRowId = 1;

    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How often to catch up. The user-facing contract is "historical figures may be a minute or
    /// two old"; live counters never come from here.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Hours per backfill chunk. A day is one set of queries and a few tens of thousands of narrow
    /// rows — large enough that the whole history is covered in a couple of minutes, small enough
    /// that a chunk never holds an unbounded result set.
    /// </summary>
    private const int BackfillChunkHours = 24;

    /// <summary>
    /// Chunks per pass. The backfill runs to completion rather than trickling: it happens once,
    /// after an upgrade, and until it finishes the dashboard falls back to scanning raw rows — which
    /// is exactly the slow path this service exists to remove. Dragging it out would keep users on
    /// that path for no benefit.
    /// </summary>
    private const int BackfillChunksPerPass = 400;

    /// <summary>
    /// How long buckets are kept: the longest dashboard window (720 h) plus two days of margin.
    /// No read reaches further back, so older buckets are deleted and never backfilled.
    /// </summary>
    internal static readonly TimeSpan BucketRetention = TimeSpan.FromHours(720 + 48);

    /// <summary>
    /// The reader stops trusting the buckets when the state row has not been updated for this long,
    /// and computes from raw rows instead. Covers a disabled rollup and a pass that keeps failing.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDatabaseAvailability _availability;
    private readonly IClusterStateProvider? _cluster;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExecutionStatsRollupService> _logger;

    public ExecutionStatsRollupService(
        IServiceScopeFactory scopeFactory,
        IDatabaseAvailability availability,
        IConfiguration configuration,
        ILogger<ExecutionStatsRollupService> logger,
        IClusterStateProvider? cluster = null)
    {
        _scopeFactory = scopeFactory;
        _availability = availability;
        _configuration = configuration;
        _logger = logger;
        _cluster = cluster;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Everything inside this catch: HostOptions.BackgroundServiceExceptionBehavior is StopHost,
        // so an escaping exception would take the process down over a statistics refresh.
        try
        {
            if (!_configuration.GetValue("Stats:Rollup:Enabled", true))
            {
                _logger.LogInformation(
                    "Execution stats rollup disabled (Stats:Rollup:Enabled=false); the dashboard " +
                    "computes its historical aggregates from raw rows.");
                return;
            }

            await Task.Delay(StartDelay, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await _availability.WaitUntilServableAsync(stoppingToken).ConfigureAwait(false))
                    return;

                // Leader-gated: the buckets are shared state in the database, so exactly one node
                // writes them. Reads work on every node.
                if (_cluster is null || _cluster.IsLeader)
                {
                    try
                    {
                        await RunPassAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Execution stats rollup pass failed; retrying next sweep.");
                    }
                }

                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Execution stats rollup stopped. The dashboard still works and falls back to " +
                "computing from raw rows.");
        }
    }

    /// <summary>One catch-up pass: forward to now, then a slice of backfill if history is missing.</summary>
    internal async Task RunPassAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        var redactor = scope.ServiceProvider.GetRequiredService<OutputRedactor>();

        var state = await LoadStateAsync(db, ct).ConfigureAwait(false);
        var currentHour = Truncate(DateTime.UtcNow);

        await PruneAsync(db, state, currentHour - BucketRetention, ct).ConfigureAwait(false);

        // Forward: everything from the last covered hour up to (and including) the current one, plus
        // any earlier hour still marked provisional because runs were open when it was written.
        var from = state.CoverageEndUtc is { } end ? end : currentHour;
        var hours = new SortedSet<DateTime>();
        for (var h = Truncate(from); h <= currentHour; h = h.AddHours(1)) hours.Add(h);
        foreach (var provisional in await ProvisionalHoursAsync(db, ct).ConfigureAwait(false))
            hours.Add(provisional);

        foreach (var hour in hours)
        {
            ct.ThrowIfCancellationRequested();
            await RollUpHourAsync(db, redactor, hour, ct).ConfigureAwait(false);
        }

        state.CoverageEndUtc = currentHour;

        // Backfill walks backwards from the oldest covered hour towards the retention horizon.
        if (!state.BackfillComplete)
            await BackfillSliceAsync(db, redactor, state, ct).ConfigureAwait(false);

        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task BackfillSliceAsync(
        NodePilotDbContext db, OutputRedactor redactor, ExecutionStatsRollupState state,
        CancellationToken ct)
    {
        var oldestRaw = await db.WorkflowExecutions.AsNoTracking()
            .OrderBy(e => e.StartedAt)
            .Select(e => (DateTime?)e.StartedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (oldestRaw is null)
        {
            // Nothing to summarise: coverage is trivially complete.
            state.CoverageStartUtc = state.CoverageEndUtc;
            state.BackfillComplete = true;
            return;
        }

        var horizon = Truncate(oldestRaw.Value);
        var retentionStart = Truncate(DateTime.UtcNow) - BucketRetention;
        if (horizon < retentionStart) horizon = retentionStart;
        var cursor = state.CoverageStartUtc is { } start ? start : state.CoverageEndUtc ?? Truncate(DateTime.UtcNow);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chunks = 0;

        for (; chunks < BackfillChunksPerPass && cursor > horizon; chunks++)
        {
            ct.ThrowIfCancellationRequested();
            var chunkEnd = cursor;
            cursor = cursor.AddHours(-BackfillChunkHours);
            if (cursor < horizon) cursor = horizon;

            await RollUpRangeAsync(db, redactor, cursor, chunkEnd, ct).ConfigureAwait(false);
            // Persist per chunk so an interrupted backfill resumes where it stopped instead of
            // starting over.
            state.CoverageStartUtc = cursor;
            state.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Saved buckets are not needed again in this pass. Without clearing, every later chunk
            // would run change detection over all buckets written so far.
            db.ChangeTracker.Clear();
            db.ExecutionStatsRollupStates.Attach(state);
        }

        if (cursor <= horizon)
        {
            state.BackfillComplete = true;
            _logger.LogInformation(
                "Execution stats backfill complete back to {Horizon:u} ({Chunks} chunk(s), " +
                "{ElapsedMs} ms); the dashboard now answers its historical aggregates from " +
                "precomputed buckets.", horizon, chunks, sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation(
                "Execution stats backfill reached {Cursor:u} after {Chunks} chunk(s) ({ElapsedMs} ms); " +
                "continuing next pass.", cursor, chunks, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>Hours previously written while runs were still open, so their numbers may have moved.</summary>
    private static async Task<List<DateTime>> ProvisionalHoursAsync(NodePilotDbContext db, CancellationToken ct)
        => await db.ExecutionHourlyStats.AsNoTracking()
            .Where(s => !s.IsFinal)
            .Select(s => s.HourUtc)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>Recomputes a single hour. Thin wrapper over the range form.</summary>
    internal Task RollUpHourAsync(
        NodePilotDbContext db, OutputRedactor redactor, DateTime hourUtc, CancellationToken ct)
    {
        var hour = Truncate(hourUtc);
        return RollUpRangeAsync(db, redactor, hour, hour.AddHours(1), ct);
    }

    /// <summary>
    /// Recomputes every hour in <c>[from, to)</c> and upserts their buckets.
    ///
    /// <para>
    /// Works on a whole range rather than hour by hour because the query count, not the row count,
    /// is what made the initial backfill slow: a day handled hourly is 24 round-trips per aggregate,
    /// the same day handled as a range is three. Rows are grouped into hours in C#, which also keeps
    /// the hour boundaries free of provider-specific date functions.
    /// </para>
    ///
    /// <para>
    /// Always recomputes instead of accumulating, so a missed or interrupted pass is self-correcting.
    /// </para>
    /// </summary>
    internal async Task RollUpRangeAsync(
        NodePilotDbContext db, OutputRedactor redactor, DateTime fromUtc, DateTime toUtc,
        CancellationToken ct)
    {
        var from = Truncate(fromUtc);
        var to = Truncate(toUtc);
        if (to <= from) return;

        var inRange = db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.StartedAt >= from && e.StartedAt < to);

        // Narrow projection, no LOB columns: one day is tens of thousands of rows at a few bytes
        // each. Aggregating in C# also avoids DateDiff, which only exists on SQL Server.
        var rows = await inRange
            .Select(e => new { e.Id, e.WorkflowId, e.Status, e.StartedAt, e.CompletedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        // Retried runs for the whole range in one query. EXISTS seeks the
        // (WorkflowExecutionId, …) index; the live form filters StepExecutions on AttemptCount,
        // which has no index and scans the largest table in the schema.
        var retriedIds = (await inRange
            .Where(e => db.StepExecutions.Any(s => s.WorkflowExecutionId == e.Id && s.AttemptCount > 1))
            .Select(e => e.Id)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

        var now = DateTime.UtcNow;

        // An hour is settled only once nothing started in it is still open — a run started at 10:55
        // may not reach its final state until 11:30.
        var openHours = rows
            .Where(r => r.Status is ExecutionStatus.Pending or ExecutionStatus.Running or ExecutionStatus.Paused)
            .Select(r => Truncate(r.StartedAt))
            .ToHashSet();

        var existing = await db.ExecutionHourlyStats
            .Where(s => s.HourUtc >= from && s.HourUtc < to)
            .ToListAsync(ct).ConfigureAwait(false);
        var existingByKey = existing.ToDictionary(s => (s.HourUtc, s.WorkflowId));
        var writtenKeys = new HashSet<(DateTime, Guid)>();

        foreach (var g in rows.GroupBy(r => (Hour: Truncate(r.StartedAt), r.WorkflowId)))
        {
            var key = (g.Key.Hour, g.Key.WorkflowId);
            writtenKeys.Add(key);
            if (!existingByKey.TryGetValue(key, out var row))
            {
                row = new ExecutionHourlyStat { HourUtc = g.Key.Hour, WorkflowId = g.Key.WorkflowId };
                db.ExecutionHourlyStats.Add(row);
            }
            row.TotalCount = g.Count();
            row.SucceededCount = g.Count(e => e.Status == ExecutionStatus.Succeeded);
            row.FailedCount = g.Count(e => e.Status == ExecutionStatus.Failed);
            row.CancelledCount = g.Count(e => e.Status == ExecutionStatus.Cancelled);
            row.RunningCount = g.Count(e => e.Status == ExecutionStatus.Running);
            row.FinishedCount = g.Count(e => e.Status is ExecutionStatus.Succeeded
                                                      or ExecutionStatus.Failed
                                                      or ExecutionStatus.Cancelled);
            row.RetriedCount = g.Count(e => retriedIds.Contains(e.Id));
            row.DurationMsSum = g.Where(e => e.CompletedAt != null)
                .Sum(e => (long)(e.CompletedAt!.Value - e.StartedAt).TotalMilliseconds);
            row.DurationMsCount = g.Count(e => e.CompletedAt != null);
            row.IsFinal = !openHours.Contains(g.Key.Hour);
            row.ComputedAt = now;
        }

        // Buckets whose runs are gone (retention, deletion) must not linger.
        foreach (var stale in existing.Where(s => !writtenKeys.Contains((s.HourUtc, s.WorkflowId))))
            db.ExecutionHourlyStats.Remove(stale);

        await RollUpFailureCausesAsync(db, redactor, from, to, openHours, now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Groups the range's failures exactly as the live path does — same first-failed-step fallback,
    /// same C# normalisation — and stores one bucket per (hour, workflow, normalised message).
    /// </summary>
    private static async Task RollUpFailureCausesAsync(
        NodePilotDbContext db, OutputRedactor redactor,
        DateTime from, DateTime to, HashSet<DateTime> openHours, DateTime now, CancellationToken ct)
    {
        var failures = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.Failed && e.StartedAt >= from && e.StartedAt < to)
            .Select(e => new
            {
                e.Id,
                e.WorkflowId,
                e.StartedAt,
                e.ErrorMessage,
                // Mirrors the live query: the earliest failed step's output wins over the
                // execution-level message.
                FirstStepError = db.StepExecutions
                    .Where(s => s.WorkflowExecutionId == e.Id && s.Status == ExecutionStatus.Failed)
                    .OrderBy(s => s.StartedAt).ThenBy(s => s.Id)
                    .Select(s => s.ErrorOutput)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var normalizer = new DashboardFailureCauses(db, redactor);
        var grouped = new Dictionary<(DateTime Hour, Guid WorkflowId, string Hash), FailureCauseHourlyStat>();

        foreach (var f in failures)
        {
            var raw = !string.IsNullOrWhiteSpace(f.FirstStepError) ? f.FirstStepError : f.ErrorMessage ?? "";
            var message = normalizer.Normalize(raw);
            var hash = HashMessage(message);
            var hour = Truncate(f.StartedAt);
            var key = (hour, f.WorkflowId, hash);

            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = new FailureCauseHourlyStat
                {
                    HourUtc = hour,
                    WorkflowId = f.WorkflowId,
                    MessageHash = hash,
                    Message = message,
                    LatestExecutionId = f.Id,
                    LatestStartedAt = f.StartedAt,
                };
                grouped[key] = bucket;
            }
            bucket.Count++;
            if (f.StartedAt > bucket.LatestStartedAt
                || (f.StartedAt == bucket.LatestStartedAt && f.Id.CompareTo(bucket.LatestExecutionId) > 0))
            {
                bucket.LatestExecutionId = f.Id;
                bucket.LatestStartedAt = f.StartedAt;
            }
        }

        var existing = await db.FailureCauseHourlyStats
            .Where(s => s.HourUtc >= from && s.HourUtc < to)
            .ToListAsync(ct).ConfigureAwait(false);
        var existingByKey = existing.ToDictionary(s => (s.HourUtc, s.WorkflowId, s.MessageHash));

        foreach (var stale in existing.Where(s => !grouped.ContainsKey((s.HourUtc, s.WorkflowId, s.MessageHash))))
            db.FailureCauseHourlyStats.Remove(stale);

        foreach (var (key, fresh) in grouped)
        {
            var isFinal = !openHours.Contains(key.Hour);
            if (!existingByKey.TryGetValue(key, out var row))
            {
                fresh.IsFinal = isFinal;
                fresh.ComputedAt = now;
                db.FailureCauseHourlyStats.Add(fresh);
                continue;
            }
            row.Message = fresh.Message;
            row.Count = fresh.Count;
            row.LatestExecutionId = fresh.LatestExecutionId;
            row.LatestStartedAt = fresh.LatestStartedAt;
            row.IsFinal = isFinal;
            row.ComputedAt = now;
        }
    }

    /// <summary>
    /// Deletes buckets older than <paramref name="cutoff"/> and moves the recorded coverage start
    /// up to it, so the state never claims hours whose buckets are gone.
    /// </summary>
    internal static async Task PruneAsync(
        NodePilotDbContext db, ExecutionStatsRollupState state, DateTime cutoff, CancellationToken ct)
    {
        await db.ExecutionHourlyStats.Where(s => s.HourUtc < cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.FailureCauseHourlyStats.Where(s => s.HourUtc < cutoff).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (state.CoverageStartUtc < cutoff) state.CoverageStartUtc = cutoff;
    }

    private static async Task<ExecutionStatsRollupState> LoadStateAsync(NodePilotDbContext db, CancellationToken ct)
    {
        var state = await db.ExecutionStatsRollupStates
            .FirstOrDefaultAsync(s => s.Id == StateRowId, ct).ConfigureAwait(false);
        if (state is not null) return state;

        state = new ExecutionStatsRollupState { Id = StateRowId, UpdatedAt = DateTime.UtcNow };
        db.ExecutionStatsRollupStates.Add(state);
        return state;
    }

    /// <summary>Hash over the FULL normalised text, so groups that differ only past the stored cap stay apart.</summary>
    internal static string HashMessage(string? message)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message ?? string.Empty)));

    internal static DateTime Truncate(DateTime value)
        => new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
}

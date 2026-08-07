using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Engine;

namespace NodePilot.Api.Diagnostics;

/// <summary>
/// Consumer for the <see cref="SupportEventChannel"/>: reads in batches (up to
/// <see cref="BatchSize"/> rows, or whenever <see cref="BatchTimeout"/> elapses) and inserts
/// them into the <c>SupportEvents</c> table using a dedicated DI scope. Best-effort —
/// an insert failure just drops the batch and increments a counter, no retry spam. During a known
/// database outage it never attempts an insert: it drains and counts dropped projections, then
/// persists one summary row when the shared probe reports recovery.
///
/// <para>A fresh DI scope per batch: the DbContext is scoped, while this BackgroundService
/// itself is a singleton — without a per-batch scope the context would live as long as the
/// app and its change-tracking state would grow without bound.</para>
///
/// <para>HA note: unlike the retention services, this flush service runs on
/// <b>every</b> node, not leader-only. Each node writes its own support events into the
/// same table — cluster-wide visibility falls out naturally since all nodes share the
/// same database.</para>
/// </summary>
internal sealed class SupportEventFlushService : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(1);

    private readonly SupportEventChannel _channel;
    private readonly IServiceProvider _services;
    private readonly ILogger<SupportEventFlushService> _logger;
    private readonly IDatabaseAvailability _availability;
    private readonly SemaphoreSlim _recoverySignal = new(0, 1);
    private long _droppedDuringCurrentOutage;

    internal long DroppedDuringCurrentOutage => Interlocked.Read(ref _droppedDuringCurrentOutage);

    public SupportEventFlushService(
        SupportEventChannel channel,
        IServiceProvider services,
        ILogger<SupportEventFlushService> logger,
        IDatabaseAvailability availability)
    {
        _channel = channel;
        _services = services;
        _logger = logger;
        _availability = availability;
        _availability.StateChanged += OnAvailabilityChanged;
    }

    private void OnAvailabilityChanged(DatabaseAvailabilityState state)
    {
        if (state is not DatabaseAvailabilityState.Available || _recoverySignal.CurrentCount != 0)
            return;
        try { _recoverySignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<SupportEvent>(BatchSize);
        var reader = _channel.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for at least one item with a deadline. WaitToReadAsync blocks until
                // either an event is available or the channel completes — we don't
                // poll-loop, so this costs 0 CPU while idle.
                if (_availability.IsServable && DroppedDuringCurrentOutage > 0)
                    await TryFlushRecoverySummaryAsync(stoppingToken);

                var available = await WaitForInputOrRecoveryAsync(reader, stoppingToken);
                if (!available) break; // Channel completed → shutdown

                // Drain into the batch — TryRead is non-blocking, grabs everything ready now.
                while (batch.Count < BatchSize && reader.TryRead(out var ev))
                    batch.Add(ev);

                if (batch.Count == 0) continue;

                // If the first read didn't fill a whole batch, wait up to BatchTimeout for
                // more events — at low volume this saves a round-trip to the DB by batching a
                // few more events together; under burst load the loop above already filled it.
                if (batch.Count < BatchSize)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(BatchTimeout);
                    try
                    {
                        while (batch.Count < BatchSize
                            && await reader.WaitToReadAsync(deadline.Token))
                        {
                            while (batch.Count < BatchSize && reader.TryRead(out var ev))
                                batch.Add(ev);
                        }
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested
                                                              && !stoppingToken.IsCancellationRequested)
                    {
                        // Batch timeout elapsed — flush whatever we've collected so far.
                    }
                }

                if (!_availability.IsServable)
                    RecordOutageDrop(batch.Count);
                else
                    await FlushBatchAsync(batch, stoppingToken);
                batch.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown — the last in-flight batch is not flushed and is dropped.
                break;
            }
            catch (Exception ex)
            {
                // The service loop must survive. Drop the current batch and keep going,
                // otherwise a single DB hiccup would permanently kill the flush service.
                if (!_availability.IsServable)
                {
                    RecordOutageDrop(batch.Count);
                }
                else
                {
                    _logger.LogWarning(ex, "Support-Event flush loop encountered an unexpected error; dropping batch of {Count}.", batch.Count);
                    EngineMetrics.SupportEventsDropped.Add(batch.Count,
                        new KeyValuePair<string, object?>("reason", "loop_error"));
                }
                batch.Clear();
            }
        }
    }

    private async Task<bool> WaitForInputOrRecoveryAsync(
        System.Threading.Channels.ChannelReader<SupportEvent> reader,
        CancellationToken stoppingToken)
    {
        using var iteration = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var input = reader.WaitToReadAsync(iteration.Token).AsTask();
        var recovery = _recoverySignal.WaitAsync(iteration.Token);
        var completed = await Task.WhenAny(input, recovery);
        iteration.Cancel();

        if (completed == recovery)
        {
            await ObserveCancellationAsync(input);
            return true;
        }

        await ObserveCancellationAsync(recovery);
        return await input;
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private void RecordOutageDrop(int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _droppedDuringCurrentOutage, count);
        EngineMetrics.SupportEventsDropped.Add(count,
            new KeyValuePair<string, object?>("reason", "database_unavailable"));
    }

    private async Task TryFlushRecoverySummaryAsync(CancellationToken ct)
    {
        var dropped = DroppedDuringCurrentOutage;
        if (dropped <= 0 || !_availability.IsServable) return;

        var summary = new SupportEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Level = 2,
            EventType = "DATABASE_OUTAGE_RECOVERED",
            Message = $"Database recovered; {dropped} support events were omitted from the DB projection during the outage.",
            PropertiesJson = JsonSerializer.Serialize(new { droppedCount = dropped }),
        };

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        try
        {
            db.SupportEvents.Add(summary);
            await db.SaveChangesAsync(ct);
            Interlocked.Add(ref _droppedDuringCurrentOutage, -dropped);
            EngineMetrics.SupportEventsWritten.Add(1);
        }
        catch (Exception ex)
        {
            db.Entry(summary).State = EntityState.Detached;
            if (_availability.IsServable)
                _logger.LogWarning(ex, "Failed to persist the database-outage support-event summary; will retry after the next wake-up.");
        }
    }

    private async Task FlushBatchAsync(List<SupportEvent> batch, CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
        try
        {
            db.SupportEvents.AddRange(batch);
            await db.SaveChangesAsync(ct);
            EngineMetrics.SupportEventsWritten.Add(batch.Count);
        }
        catch (Exception ex)
        {
            // DB insert failure: drop the batch and count it. Do not retry-loop here — the
            // channel keeps filling up with fresh events, so the next batch can go through
            // once the DB is back. A retry storm would only pile more load on a struggling DB.
            if (!_availability.IsServable)
            {
                RecordOutageDrop(batch.Count);
            }
            else
            {
                _logger.LogWarning(ex, "Failed to flush {Count} support events to DB; dropping.", batch.Count);
                EngineMetrics.SupportEventsDropped.Add(batch.Count,
                    new KeyValuePair<string, object?>("reason", "db_insert_failed"));
            }
        }
    }

    public override void Dispose()
    {
        _availability.StateChanged -= OnAvailabilityChanged;
        _recoverySignal.Dispose();
        base.Dispose();
    }
}

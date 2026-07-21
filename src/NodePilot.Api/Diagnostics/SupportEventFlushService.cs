using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;

namespace NodePilot.Api.Diagnostics;

/// <summary>
/// Consumer for the <see cref="SupportEventChannel"/>: reads in batches (up to
/// <see cref="BatchSize"/> rows, or whenever <see cref="BatchTimeout"/> elapses) and inserts
/// them into the <c>SupportEvents</c> table using a dedicated DI scope. Best-effort —
/// an insert failure just drops the batch and increments a counter, no retry spam.
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

    public SupportEventFlushService(
        SupportEventChannel channel,
        IServiceProvider services,
        ILogger<SupportEventFlushService> logger)
    {
        _channel = channel;
        _services = services;
        _logger = logger;
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
                var available = await reader.WaitToReadAsync(stoppingToken);
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
                _logger.LogWarning(ex, "Support-Event flush loop encountered an unexpected error; dropping batch of {Count}.", batch.Count);
                EngineMetrics.SupportEventsDropped.Add(batch.Count,
                    new KeyValuePair<string, object?>("reason", "loop_error"));
                batch.Clear();
            }
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
            _logger.LogWarning(ex, "Failed to flush {Count} support events to DB; dropping.", batch.Count);
            EngineMetrics.SupportEventsDropped.Add(batch.Count,
                new KeyValuePair<string, object?>("reason", "db_insert_failed"));
        }
    }
}

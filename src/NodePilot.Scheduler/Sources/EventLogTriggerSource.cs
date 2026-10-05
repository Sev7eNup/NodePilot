#pragma warning disable CA1416 // Windows-only API
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Triggers;
using NodePilot.Engine.Triggers;

namespace NodePilot.Scheduler.Sources;

/// <summary>
/// Subscribes to a Windows Event Log (Application / System / custom) and fires the workflow for
/// every entry that passes the node's filters.
///
/// <para>Config parsing, filter semantics and the log allow-list all live in
/// <see cref="EventLogTriggerSettings"/>, shared with the node executor
/// (<c>NodePilot.Engine.Triggers.EventLogTrigger</c>) so a documented key cannot be honoured by one
/// runtime and silently dropped by the other. <c>lookbackMinutes</c> remains limited to the manual
/// diagnostic run; the live source resumes from its durable EventLog index cursor.</para>
/// </summary>
public class EventLogTriggerSource : ITriggerSource
{
    public string ActivityType => "eventLogTrigger";

    /// <summary>
    /// The subscription API has no fault callback, so liveness comes from the owned reconciliation
    /// task. It periodically reads the log and replays entries after the durable cursor; an
    /// unexpected task exit makes the orchestrator rebuild this source.
    /// </summary>
    public TriggerHealth Health =>
        _reconcileTask is null or { IsCompleted: false }
            ? TriggerHealth.Healthy
            : TriggerHealth.Faulted($"event-log reconciliation ended ({_reconcileTask.Status})");

    private readonly ILogger<EventLogTriggerSource> _logger;
    private readonly IConfiguration _config;
    private EventLog? _log;
    private TriggerContext? _ctx;
    private EventLogTriggerSettings? _settings;
    private CancellationTokenSource? _cts;
    private Task? _reconcileTask;
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private TriggerCheckpoint? _checkpoint;
    private EventLogCursor? _cursor;

    public EventLogTriggerSource(ILogger<EventLogTriggerSource> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public async Task StartAsync(TriggerContext context, CancellationToken ct)
    {
        _ctx = context;
        var settings = EventLogTriggerSettings.Parse(context.Config);

        var extra = _config.GetSection("Trigger:EventLog:AllowedLogs").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToArray();
        if (!EventLogTriggerSettings.IsLogAllowed(settings.LogName, extra))
            throw new InvalidOperationException(
                EventLogTriggerSettings.DescribeRejectedLog(settings.LogName, extra));

        _settings = settings;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _checkpoint = await context.ReadCheckpointAsync();
        int maxIndex;
        int writtenWhileDown;
        // The handler is live as soon as the log is opened, and a reconcile it starts must neither
        // read the log alongside this method nor see a cursor that is not set yet.
        using (await _reconcileGate.HoldAsync(ct))
        {
            _log = new EventLog(settings.LogName) { EnableRaisingEvents = true };
            _log.EntryWritten += OnEntry;

            _cursor = DeserializeCursor(_checkpoint?.Position);

            // Both startup questions (the highest index present, and how many entries sit above the
            // stored cursor) are answered from the end of the log, so the cost follows what is new
            // and not the size of the log.
            var log = _log;
            List<EventLogEntry> newerThanCursor;
            (maxIndex, newerThanCursor) = ReadFromEnd(log, _cursor?.Index ?? int.MaxValue);
            writtenWhileDown = newerThanCursor.Count;
            if (_cursor is null)
            {
                _cursor = new EventLogCursor(Guid.NewGuid().ToString("N"), maxIndex);
                var seeded = new TriggerCheckpoint(JsonSerializer.Serialize(_cursor), $"eventlog-seed:{Guid.NewGuid():N}");
                if (!await context.InitializeCheckpointAsync(seeded))
                    throw new InvalidOperationException("EventLogTrigger: durable cursor could not be initialized");
                _checkpoint = seeded;
            }
            else if (maxIndex < _cursor.Index)
            {
                // The log was cleared or recreated. A fresh generation prevents reused Entry.Index
                // values from colliding with receipts from the previous incarnation.
                // Every entry still in the log predates this start, so all of them count as written
                // while the source was down; the count above was measured against the stale cursor.
                writtenWhileDown = log.Entries.Count;
                _cursor = new EventLogCursor(Guid.NewGuid().ToString("N"), 0);
                var reset = new TriggerCheckpoint(JsonSerializer.Serialize(_cursor), $"eventlog-reset:{_cursor.Generation}");
                if (!await context.SaveCheckpointAsync(reset))
                    throw new InvalidOperationException("EventLogTrigger: cleared-log cursor could not be persisted");
                _checkpoint = reset;
            }
        }

        await SkipEntriesWrittenWhileDownAsync(context, maxIndex, writtenWhileDown, _cts.Token);
        var reconcileSeconds = Math.Max(1, _config.GetValue<int?>("Trigger:EventLog:ReconcileSeconds") ?? 30);
        _reconcileTask = ReconcileLoopAsync(TimeSpan.FromSeconds(reconcileSeconds), _cts.Token);
        _logger.LogInformation(
            "EventLogTrigger: subscribed to {Log} src={Src} type={Type} eventId={EventId} pattern={Pattern}",
            settings.LogName,
            settings.Source ?? "*",
            settings.EntryType?.ToString() ?? "any",
            settings.EventId?.ToString() ?? "any",
            settings.MessagePattern is null ? "none" : "set");
    }

    private void OnEntry(object? sender, EntryWrittenEventArgs e)
    {
        var entry = e.Entry;
        var settings = _settings;
        if (settings is null) return;

        // Count every event the kernel hands us — even if filters drop it later. Lets
        // operators distinguish "log is quiet" from "filters are too strict".
        SchedulerMetrics.TriggerEvents.Add(1,
            new KeyValuePair<string, object?>("trigger_type", "eventLogTrigger"),
            new KeyValuePair<string, object?>("event_kind", entry.EntryType.ToString()));

        TriggerFireObserver.Observe(
            ReconcileAsync(_cts?.Token ?? CancellationToken.None),
            _logger, ActivityType, _ctx!.WorkflowId, _ctx.NodeId);
    }

    /// <summary>
    /// Moves the cursor past everything already in the log at startup, so entries written while
    /// this source was not running are skipped rather than replayed. The periodic reconcile and
    /// the <c>EntryWritten</c> handler keep their full behaviour — they recover notifications
    /// missed while the source *is* running, which is a live gap, not a restart.
    /// <para>
    /// Takes the delivery gate because the handler is already attached at this point and may be
    /// running a reconcile of its own.
    /// </para>
    /// </summary>
    private async Task SkipEntriesWrittenWhileDownAsync(
        TriggerContext context, int maxIndex, int skipped, CancellationToken ct)
    {
        await _deliveryGate.WaitAsync(ct);
        try
        {
            if (PlanSkip(_cursor, maxIndex) is not { } advanced) return;

            var checkpoint = new TriggerCheckpoint(
                JsonSerializer.Serialize(advanced), $"eventlog-skip:{Guid.NewGuid():N}");
            if (!await context.SaveCheckpointAsync(checkpoint))
                throw new InvalidOperationException(
                    "EventLogTrigger: durable cursor could not be advanced past the missed window");
            _cursor = advanced;
            _checkpoint = checkpoint;

            if (skipped == 0) return;

            SchedulerMetrics.TriggerFiresSkipped.Add(skipped,
                new KeyValuePair<string, object?>("trigger_type", ActivityType));
            _logger.LogWarning(
                "EventLogTrigger: skipped {Skipped} entr(y/ies) in '{Log}' for workflow {WorkflowId} node "
                + "{NodeId} that were written while the source was not running. Missed entries are never replayed.",
                skipped, _settings?.LogName, context.WorkflowId, context.NodeId);
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private async Task ReconcileLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
            await ReconcileAsync(ct);
    }

    // Every written entry asks for a reconcile, and the EventLog instance is not thread-safe, so
    // passes never overlap; requests that arrive during a pass are folded into one follow-up pass.
    private readonly SinglePassGate _reconcileGate = new();

    private Task ReconcileAsync(CancellationToken ct)
        => _reconcileGate.RunAsync(() => ReconcileOnceAsync(ct), ct);

    private int _consecutiveReadFailures;

    private async Task ReconcileOnceAsync(CancellationToken ct)
    {
        var log = _log;
        if (log is null) return;

        // The log can change under a read (it wraps, or an entry is overwritten). The cursor makes
        // the next pass pick up exactly where this one stopped, so a failed read loses nothing.
        if (!TryReadFromEnd(log, _cursor?.Index ?? int.MinValue, out var maxIndex, out var entries))
            return;
        if (_cursor is not null && maxIndex < _cursor.Index)
        {
            // The log was cleared: every entry in it is new.
            await ResetCursorAfterClearAsync(ct);
            if (!TryReadFromEnd(log, _cursor?.Index ?? int.MinValue, out _, out entries))
                return;
        }

        foreach (var entry in entries)
            await DeliverEntryAsync(entry, ct);
    }

    private bool TryReadFromEnd(EventLog log, int afterIndex, out int maxIndex, out List<EventLogEntry> entries)
    {
        try
        {
            (maxIndex, entries) = ReadFromEnd(log, afterIndex);
            _consecutiveReadFailures = 0;
            return true;
        }
        catch (Exception ex) when (IsTransientReadFailure(ex))
        {
            ReportTransientReadFailure(ex);
            maxIndex = 0;
            entries = [];
            return false;
        }
    }

    // What EventLog.Entries throws when the log moves under the read: an out-of-range position, or
    // the IndexOutOfRangeException raised inside its own entry cache.
    internal static bool IsTransientReadFailure(Exception ex)
        => ex is IndexOutOfRangeException or ArgumentException
           || ex is AggregateException { InnerExceptions: { Count: > 0 } inner } && inner.All(IsTransientReadFailure);

    private void ReportTransientReadFailure(Exception ex)
    {
        SchedulerMetrics.TriggerPollErrors.Add(1,
            new KeyValuePair<string, object?>("trigger_type", "eventLogTrigger"),
            new KeyValuePair<string, object?>("error_class", ex.GetType().Name));
        var failures = ++_consecutiveReadFailures;
        // The first failure and then every twentieth: a single hiccup is expected on a busy log,
        // a log that cannot be read for minutes is not.
        if (failures == 1 || failures % 20 == 0)
            _logger.LogWarning(ex,
                "EventLogTrigger: reading '{Log}' failed {Failures} time(s) in a row; the next pass retries "
                + "from the stored cursor.",
                _settings?.LogName, failures);
    }

    /// <summary>
    /// Reads the entries newer than <paramref name="afterIndex"/> from the end of the log, newest
    /// last, together with the highest index present. Cost follows the number of new entries, not
    /// the size of the log. Relies on the log keeping ascending record numbers in position order.
    /// </summary>
    private static (int MaxIndex, List<EventLogEntry> Newer) ReadFromEnd(EventLog log, int afterIndex)
    {
        var entries = log.Entries;
        return ReadFromEnd(entries.Count, position => entries[position], entry => entry.Index, afterIndex);
    }

    internal static (int MaxIndex, List<T> Newer) ReadFromEnd<T>(
        int count, Func<int, T> at, Func<T, int> indexOf, int afterIndex)
    {
        var newer = new List<T>();
        var maxIndex = 0;
        for (var position = count - 1; position >= 0; position--)
        {
            var entry = at(position);
            var index = indexOf(entry);
            if (position == count - 1) maxIndex = index;
            if (index <= afterIndex) break;
            newer.Add(entry);
        }
        newer.Reverse();
        return (maxIndex, newer);
    }

    private async Task ResetCursorAfterClearAsync(CancellationToken ct)
    {
        await _deliveryGate.WaitAsync(ct);
        try
        {
            var cursor = new EventLogCursor(Guid.NewGuid().ToString("N"), 0);
            var checkpoint = new TriggerCheckpoint(
                JsonSerializer.Serialize(cursor),
                $"eventlog-reset:{cursor.Generation}");
            while (!ct.IsCancellationRequested && !await _ctx!.SaveCheckpointAsync(checkpoint))
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (!ct.IsCancellationRequested)
            {
                _cursor = cursor;
                _checkpoint = checkpoint;
            }
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private async Task DeliverEntryAsync(EventLogEntry entry, CancellationToken ct)
    {
        await _deliveryGate.WaitAsync(ct);
        try
        {
            var settings = _settings;
            if (settings is null) return;
            if (_cursor is not null && entry.Index <= _cursor.Index) return;

            var generation = _cursor?.Generation ?? Guid.NewGuid().ToString("N");
            var nextCursor = new EventLogCursor(generation, entry.Index);
            var match = settings.Matches(entry.Source, entry.InstanceId, EventLogTrigger.ToFilter(entry.EntryType), entry.Message);
            if (match != EventLogMatch.Match)
            {
                if (match == EventLogMatch.PatternTimeout)
                {
                    SchedulerMetrics.TriggerPollErrors.Add(1,
                        new KeyValuePair<string, object?>("trigger_type", "eventLogTrigger"),
                        new KeyValuePair<string, object?>("error_class", nameof(System.Text.RegularExpressions.RegexMatchTimeoutException)));
                    _logger.LogWarning("EventLogTrigger: messagePattern regex timed out on event from {Src}; skipping.", entry.Source);
                }
                var skipped = new TriggerCheckpoint(
                    JsonSerializer.Serialize(nextCursor),
                    $"eventlog-skip:{settings.LogName}:{generation}:{entry.Index}");
                while (!ct.IsCancellationRequested && !await _ctx!.SaveCheckpointAsync(skipped))
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                if (!ct.IsCancellationRequested)
                {
                    _cursor = nextCursor;
                    _checkpoint = skipped;
                }
                return;
            }

            var signal = new TriggerSignal(
                $"eventlog:{settings.LogName}:{generation}:{entry.Index}",
                JsonSerializer.Serialize(nextCursor),
                new Dictionary<string, string>
                {
                    ["eventSource"] = entry.Source,
                    ["eventEntryType"] = entry.EntryType.ToString(),
                    ["eventId"] = entry.InstanceId.ToString(),
                    ["eventMessage"] = entry.Message ?? "",
                    ["eventTimeWritten"] = entry.TimeWritten.ToString("O"),
                });
            while (!ct.IsCancellationRequested && !await _ctx!.DeliverAsync(signal))
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (!ct.IsCancellationRequested)
            {
                _cursor = nextCursor;
                _checkpoint = new TriggerCheckpoint(signal.Position, signal.EventKey);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private static EventLogCursor? DeserializeCursor(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<EventLogCursor>(json); }
        catch (JsonException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_log is not null)
        {
            _log.EnableRaisingEvents = false;
            _log.EntryWritten -= OnEntry;
            _log.Dispose();
            _log = null;
        }
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            try { if (_reconcileTask is not null) await _reconcileTask; }
            catch (OperationCanceledException) { }
            _cts.Dispose();
            _cts = null;
            _reconcileTask = null;
        }
    }

    /// <summary>
    /// Where the cursor has to land so nothing written while the source was down is replayed:
    /// the highest index currently in the log, under the cursor's existing generation. Returns
    /// null when there is nothing to skip. The generation is carried over deliberately — a new
    /// one is only minted when the log itself was cleared, and reusing indices across an unnoticed
    /// clear would collide with receipts from the previous incarnation.
    /// </summary>
    internal static EventLogCursor? PlanSkip(EventLogCursor? cursor, int maxIndex)
        => cursor is null || maxIndex <= cursor.Index
            ? null
            : new EventLogCursor(cursor.Generation, maxIndex);

    internal sealed record EventLogCursor(string Generation, int Index);
}

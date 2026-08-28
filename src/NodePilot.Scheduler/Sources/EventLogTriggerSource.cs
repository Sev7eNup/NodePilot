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
        _log = new EventLog(settings.LogName) { EnableRaisingEvents = true };
        _log.EntryWritten += OnEntry;

        var maxIndex = _log.Entries.Cast<EventLogEntry>().Select(entry => entry.Index).DefaultIfEmpty(0).Max();
        _cursor = DeserializeCursor(_checkpoint?.Position);
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
            _cursor = new EventLogCursor(Guid.NewGuid().ToString("N"), 0);
            var reset = new TriggerCheckpoint(JsonSerializer.Serialize(_cursor), $"eventlog-reset:{_cursor.Generation}");
            if (!await context.SaveCheckpointAsync(reset))
                throw new InvalidOperationException("EventLogTrigger: cleared-log cursor could not be persisted");
            _checkpoint = reset;
        }

        await ReconcileAsync(_cts.Token);
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

    private async Task ReconcileLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
            await ReconcileAsync(ct);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var log = _log;
        if (log is null) return;
        var allEntries = log.Entries.Cast<EventLogEntry>().ToList();
        var maxIndex = allEntries.Select(entry => entry.Index).DefaultIfEmpty(0).Max();
        if (_cursor is not null && maxIndex < _cursor.Index)
            await ResetCursorAfterClearAsync(ct);
        var entries = allEntries
            .Where(entry => _cursor is null || entry.Index > _cursor.Index)
            .OrderBy(entry => entry.Index)
            .ToList();
        foreach (var entry in entries)
            await DeliverEntryAsync(entry, ct);
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

    private sealed record EventLogCursor(string Generation, int Index);
}

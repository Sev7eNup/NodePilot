#pragma warning disable CA1416 // Windows-only API
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Triggers;

namespace NodePilot.Scheduler.Sources;

/// <summary>
/// Subscribes to a Windows Event Log (Application / System / custom) and fires the workflow for
/// every entry that passes the node's filters.
///
/// <para>Config parsing, filter semantics and the log allow-list all live in
/// <see cref="EventLogTriggerSettings"/>, shared with the node executor
/// (<c>NodePilot.Engine.Triggers.EventLogTrigger</c>) so a documented key cannot be honoured by one
/// runtime and silently dropped by the other. <c>lookbackMinutes</c> is the one key this source
/// ignores by design: it bounds the manual run's backwards scan, and replaying history on start
/// would re-fire the same events every time the orchestrator rebuilds this source.</para>
/// </summary>
public class EventLogTriggerSource : ITriggerSource
{
    public string ActivityType => "eventLogTrigger";

    /// <summary>
    /// Always healthy — a deliberate limitation, not an oversight. <see cref="EventLog"/> has no
    /// Error/fault channel at all, so a subscription killed by a log clear or an EventLog-service
    /// restart goes silently deaf with no in-memory signal to read. The only real probe
    /// (<c>_log.Entries.Count</c>) is RPC to the EventLog service, which the
    /// <see cref="ITriggerSource.Health"/> contract forbids inline and which would need its own
    /// probe loop. Revisit if a dead eventLogTrigger is ever actually reported (docs/roadmap.md).
    /// </summary>
    public TriggerHealth Health => TriggerHealth.Healthy;

    private readonly ILogger<EventLogTriggerSource> _logger;
    private readonly IConfiguration _config;
    private EventLog? _log;
    private TriggerContext? _ctx;
    private EventLogTriggerSettings? _settings;

    public EventLogTriggerSource(ILogger<EventLogTriggerSource> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public Task StartAsync(TriggerContext context, CancellationToken ct)
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
        _log = new EventLog(settings.LogName) { EnableRaisingEvents = true };
        _log.EntryWritten += OnEntry;
        _logger.LogInformation(
            "EventLogTrigger: subscribed to {Log} src={Src} type={Type} eventId={EventId} pattern={Pattern}",
            settings.LogName,
            settings.Source ?? "*",
            settings.EntryType?.ToString() ?? "any",
            settings.EventId?.ToString() ?? "any",
            settings.MessagePattern is null ? "none" : "set");
        return Task.CompletedTask;
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

        var match = settings.Matches(entry.Source, entry.InstanceId, ToFilter(entry.EntryType), entry.Message);
        if (match == EventLogMatch.PatternTimeout)
        {
            SchedulerMetrics.TriggerPollErrors.Add(1,
                new KeyValuePair<string, object?>("trigger_type", "eventLogTrigger"),
                new KeyValuePair<string, object?>("error_class", nameof(System.Text.RegularExpressions.RegexMatchTimeoutException)));
            _logger.LogWarning("EventLogTrigger: messagePattern regex timed out on event from {Src}; skipping.", entry.Source);
            return;
        }
        if (match != EventLogMatch.Match) return;

        TriggerFireObserver.Observe(
            _ctx!.OnFire(new Dictionary<string, string>
            {
                ["eventSource"] = entry.Source,
                ["eventEntryType"] = entry.EntryType.ToString(),
                ["eventId"] = entry.InstanceId.ToString(),
                ["eventMessage"] = entry.Message ?? "",
                ["eventTimeWritten"] = entry.TimeWritten.ToString("O"),
            }),
            _logger, ActivityType, _ctx.WorkflowId, _ctx.NodeId);
    }

    /// <summary>
    /// Maps the framework enum onto the Core filter enum. A plain switch rather than a
    /// <c>ToString</c> round-trip: this runs on the EventLog callback thread for every entry
    /// written to the log, filtered or not.
    /// </summary>
    internal static EventLogEntryTypeFilter ToFilter(EventLogEntryType type) => type switch
    {
        EventLogEntryType.Error => EventLogEntryTypeFilter.Error,
        EventLogEntryType.Warning => EventLogEntryTypeFilter.Warning,
        EventLogEntryType.SuccessAudit => EventLogEntryTypeFilter.SuccessAudit,
        EventLogEntryType.FailureAudit => EventLogEntryTypeFilter.FailureAudit,
        _ => EventLogEntryTypeFilter.Information,
    };

    public ValueTask DisposeAsync()
    {
        if (_log is not null)
        {
            _log.EnableRaisingEvents = false;
            _log.EntryWritten -= OnEntry;
            _log.Dispose();
            _log = null;
        }
        return ValueTask.CompletedTask;
    }
}

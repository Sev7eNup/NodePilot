using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Triggers;

namespace NodePilot.Engine.Triggers;

/// <summary>
/// Windows Event Log trigger — the node-executor half of the trigger.
///
/// <para>When the orchestrator fires the workflow, this node just surfaces the event metadata the
/// listener captured. On a manual run it scans the log itself and reports a sample of matching
/// entries, so an author can check the filters without waiting for a real event.</para>
///
/// <para>Config parsing, filter semantics and the log allow-list live in
/// <see cref="EventLogTriggerSettings"/>, shared with
/// <c>NodePilot.Scheduler.Sources.EventLogTriggerSource</c> — the sample scan and the live listener
/// therefore apply the same filters to the same keys.</para>
/// </summary>
public class EventLogTrigger : IActivityExecutor
{
    // D9: hard cap on how many EventLogEntry objects the manual-run scan inspects per
    // execution. Without this a GB-class Application log on a busy server can pin
    // a worker thread for minutes — and the trigger's purpose is "show me a sample",
    // not "full forensic search".
    internal const int MaxEventsToScanPerManualRun = 5000;

    private readonly IConfiguration? _config;

    public EventLogTrigger(IConfiguration? config = null)
    {
        _config = config;
    }

    public string ActivityType => "eventLogTrigger";

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        // If the orchestrator fired this trigger, event metadata is in context.Variables as manual.*
        var orchestratorParams = TriggerVariables.ExtractManualParams(context.Variables);

        if (orchestratorParams.TryGetValue("eventId", out var triggeredEventId))
        {
            var triggeredMessage = orchestratorParams.GetValueOrDefault("eventMessage", "");
            var triggeredLog = orchestratorParams.GetValueOrDefault("eventSource", "");
            return Task.FromResult(new ActivityResult
            {
                Success = true,
                Output = $"Event Log trigger fired\nSource: {triggeredLog}\nEvent ID: {triggeredEventId}\nMessage: {triggeredMessage}",
                OutputParameters = orchestratorParams,
            });
        }

        EventLogTriggerSettings settings;
        try
        {
            settings = EventLogTriggerSettings.Parse(config);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new ActivityResult { Success = false, ErrorOutput = ex.Message });
        }

        // Manual execution: query recent events
        try
        {
            // L-11: even on manual-run the log name is attacker-controllable via workflow JSON.
            // Opening "Security" unprivileged or a non-existent log throws, but that's still a
            // useful reconnaissance signal — enforce the same allow-list the listener uses.
            var extraAllowed = _config?.GetSection("Trigger:EventLog:AllowedLogs").Get<string[]>();
            if (!EventLogTriggerSettings.IsLogAllowed(settings.LogName, extraAllowed))
            {
                return Task.FromResult(new ActivityResult
                {
                    Success = false,
                    ErrorOutput = EventLogTriggerSettings.DescribeRejectedLog(settings.LogName, extraAllowed),
                });
            }

            var eventLog = new EventLog(settings.LogName);
            var cutoff = DateTime.Now.AddMinutes(-settings.LookbackMinutes);

            var scan = ScanEventLogNewestFirst(
                eventLog, cutoff, settings, matchLimit: 20, scanLimit: MaxEventsToScanPerManualRun);

            var output = $"Event Log: {settings.LogName}\nSource filter: {settings.Source ?? "(any)"}\n" +
                         $"Event ID filter: {settings.EventId?.ToString() ?? "(any)"}\n" +
                         $"Entry type: {settings.EntryType?.ToString() ?? "(any)"}\n" +
                         $"Message pattern: {settings.MessagePattern ?? "(any)"}\n" +
                         $"Lookback: {settings.LookbackMinutes} min\nMatches: {scan.Matches.Count}\n";
            if (scan.PatternTimeouts > 0)
                output += $"Note: messagePattern timed out on {scan.PatternTimeouts} entr(ies); they were skipped.\n";
            output += "\n";

            foreach (var entry in scan.Matches.Take(10))
            {
                output += $"[{entry.TimeGenerated:HH:mm:ss}] ID:{entry.InstanceId} {entry.EntryType} - {entry.Source}\n";
                output += $"  {entry.Message?.Split('\n').FirstOrDefault()?.Trim()}\n\n";
            }

            return Task.FromResult(new ActivityResult
            {
                Success = true,
                Output = output.TrimEnd()
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ActivityResult
            {
                Success = false,
                ErrorOutput = $"Event Log error: {ex.Message}"
            });
        }
    }

    private readonly record struct ScanResult(List<EventLogEntry> Matches, int PatternTimeouts);

    // Walks the EventLog from newest to oldest by indexing in reverse. Stops as soon as
    // we have enough matches or we've inspected `scanLimit` entries — whichever comes
    // first. The previous Cast/Where/OrderByDescending pipeline implicitly enumerated the
    // entire collection before sorting, which on a multi-GB Application log scaled
    // catastrophically.
    private static ScanResult ScanEventLogNewestFirst(
        EventLog log, DateTime cutoff, EventLogTriggerSettings settings, int matchLimit, int scanLimit)
    {
        var matches = new List<EventLogEntry>(matchLimit);
        var timeouts = 0;
        var entries = log.Entries;
        var total = entries.Count;
        var inspected = 0;
        for (var i = total - 1; i >= 0 && inspected < scanLimit && matches.Count < matchLimit; i--)
        {
            EventLogEntry entry;
            try { entry = entries[i]; }
            catch (ArgumentException) { continue; } // entry was rotated out between Count and read
            inspected++;
            if (entry.TimeGenerated < cutoff) break; // sorted; older than cutoff → done

            switch (settings.Matches(entry.Source, entry.InstanceId, ToFilter(entry.EntryType), entry.Message))
            {
                case EventLogMatch.Match: matches.Add(entry); break;
                case EventLogMatch.PatternTimeout: timeouts++; break;
                default: break;
            }
        }
        return new ScanResult(matches, timeouts);
    }

    /// <summary>
    /// Maps the framework enum onto the Core filter enum used by the shared matcher. Public
    /// because the background source (NodePilot.Scheduler) needs the identical mapping — the
    /// Core settings type cannot host it without pulling the Windows-only
    /// System.Diagnostics.EventLog package into Core, and from there into the CLI and MCP
    /// executables that only speak HTTP.
    /// <para>A plain switch rather than a <c>ToString</c> round-trip: the scheduler runs this on
    /// the EventLog callback thread for every entry written to the log, filtered or not.</para>
    /// </summary>
    public static EventLogEntryTypeFilter ToFilter(EventLogEntryType type) => type switch
    {
        EventLogEntryType.Error => EventLogEntryTypeFilter.Error,
        EventLogEntryType.Warning => EventLogEntryTypeFilter.Warning,
        EventLogEntryType.SuccessAudit => EventLogEntryTypeFilter.SuccessAudit,
        EventLogEntryType.FailureAudit => EventLogEntryTypeFilter.FailureAudit,
        _ => EventLogEntryTypeFilter.Information,
    };
}

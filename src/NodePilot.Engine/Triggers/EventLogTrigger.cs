using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Triggers;

/// <summary>
/// Windows Event Log trigger — monitors the event log for specific events.
/// When executed as a node (manual run), it queries recent matching events.
/// Background listener uses EventLog.EntryWritten subscription.
/// Config: logName (Application, System, Security), source, eventId, level
/// </summary>
public class EventLogTrigger : IActivityExecutor
{
    private static readonly string[] DefaultAllowedLogs = { "Application", "System" };

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
        var logName = config.TryGetProperty("logName", out var ln) ? ln.GetString() : "Application";
        var source = config.TryGetProperty("source", out var s) ? s.GetString() : null;
        var eventIdFilter = config.TryGetProperty("eventId", out var eid) ? eid.GetInt32() : (int?)null;
        // Field name aligned with the scheduler-side source (EventLogTriggerSource expects
        // `entryType`). The legacy `level` key is still read as a fallback so workflows
        // saved against the old vocabulary keep matching until they're re-saved.
        var entryType = config.TryGetProperty("entryType", out var et) ? et.GetString() : null;
        if (string.IsNullOrEmpty(entryType))
            entryType = config.TryGetProperty("level", out var lv) ? lv.GetString() : null;
        var lookbackMinutes = config.TryGetProperty("lookbackMinutes", out var lb) ? lb.GetInt32() : 5;

        // If the orchestrator fired this trigger, event metadata is in context.Variables as manual.*
        var orchestratorParams = new Dictionary<string, string>();
        foreach (var (k, v) in context.Variables)
            if (k.StartsWith("manual.", StringComparison.OrdinalIgnoreCase))
                orchestratorParams[k["manual.".Length..]] = v;

        if (orchestratorParams.TryGetValue("eventId", out var triggeredEventId))
        {
            var triggeredMessage = orchestratorParams.GetValueOrDefault("eventMessage", "");
            return Task.FromResult(new ActivityResult
            {
                Success = true,
                Output = $"Event Log trigger fired\nLog: {logName}\nEvent ID: {triggeredEventId}\nMessage: {triggeredMessage}",
                OutputParameters = orchestratorParams,
            });
        }

        // Manual execution: query recent events
        try
        {
            // L-11: even on manual-run the log name is attacker-controllable via workflow JSON.
            // Opening "Security" unprivileged or a non-existent log throws, but that's still a
            // useful reconnaissance signal — enforce an allow-list. Admins can extend via
            // Trigger:EventLog:AllowedLogs.
            var effectiveLogName = logName ?? "Application";
            var allowed = _config?.GetSection("Trigger:EventLog:AllowedLogs").Get<string[]>()
                          ?? DefaultAllowedLogs;
            if (!allowed.Any(a => string.Equals(a, effectiveLogName, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(new ActivityResult
                {
                    Success = false,
                    ErrorOutput = $"Event Log '{effectiveLogName}' is not in the allow-list. " +
                                  "Add it to Trigger:EventLog:AllowedLogs to permit access.",
                });
            }

            var eventLog = new EventLog(effectiveLogName);
            var cutoff = DateTime.Now.AddMinutes(-lookbackMinutes);

            // D9: cap the linear scan at a sane upper bound. EventLog.Entries lazy-loads but
            // an unfiltered LINQ pass over a GB-class Application log would still pin a worker
            // thread for minutes. Walk newest-first explicitly (via reverse index) and stop
            // when we have enough matches or hit the scan cap.
            var matchingEntries = ScanEventLogNewestFirst(
                eventLog, cutoff, source, eventIdFilter, entryType,
                matchLimit: 20, scanLimit: MaxEventsToScanPerManualRun);

            var output = $"Event Log: {logName}\nSource filter: {source ?? "(any)"}\nEvent ID filter: {eventIdFilter?.ToString() ?? "(any)"}\n" +
                         $"Entry type: {entryType ?? "(any)"}\nLookback: {lookbackMinutes} min\nMatches: {matchingEntries.Count}\n\n";

            foreach (var entry in matchingEntries.Take(10))
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

    // Walks the EventLog from newest to oldest by indexing in reverse. Stops as soon as
    // we have enough matches or we've inspected `scanLimit` entries — whichever comes
    // first. The previous Cast/Where/OrderByDescending pipeline implicitly enumerated the
    // entire collection before sorting, which on a multi-GB Application log scaled
    // catastrophically.
    private static List<EventLogEntry> ScanEventLogNewestFirst(
        EventLog log, DateTime cutoff, string? source, int? eventIdFilter, string? entryType,
        int matchLimit, int scanLimit)
    {
        var matches = new List<EventLogEntry>(matchLimit);
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
            if (source is not null && !entry.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
            if (eventIdFilter is not null && entry.InstanceId != eventIdFilter) continue;
            if (entryType is not null && !MatchesEntryType(entry.EntryType, entryType)) continue;
            matches.Add(entry);
        }
        return matches;
    }

    // Accepts both the new `entryType` vocabulary (Error/Warning/Information/SuccessAudit/
    // FailureAudit — matches EventLogEntryType + the scheduler-side source) and the legacy
    // `level` aliases (error/warning/information/info/critical) so workflows saved against
    // the old UI keep matching until they're re-saved.
    private static bool MatchesEntryType(EventLogEntryType actual, string filter)
    {
        return filter.Trim().ToLowerInvariant() switch
        {
            "error" or "critical" => actual == EventLogEntryType.Error,
            "warning" => actual == EventLogEntryType.Warning,
            "information" or "info" => actual == EventLogEntryType.Information,
            "successaudit" or "success" => actual == EventLogEntryType.SuccessAudit,
            "failureaudit" or "failure" => actual == EventLogEntryType.FailureAudit,
            _ => true,
        };
    }
}

#pragma warning disable CA1416 // Windows-only API
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NodePilot.Scheduler.Sources;

/// <summary>
/// Subscribes to a Windows Event Log (Application / System / Security / custom).
/// Config keys:
///   logName (required) — e.g. "Application"
///   source (optional) — filter on EventLogEntry.Source
///   entryType (optional) — "Error" | "Warning" | "Information" | "SuccessAudit" | "FailureAudit" | "any" (default "any")
///   messagePattern (optional) — regex tested against the message (timeout 500 ms)
///
/// Log-name safety: only <c>Application</c>, <c>System</c>, and names listed under
/// <c>Trigger:EventLog:AllowedLogs</c> are accepted. Reading <c>Security</c> requires
/// elevated privileges and would leak logon events / audit trails to any workflow author,
/// so it is blocked by default. Add it to AllowedLogs if an admin truly needs it.
/// </summary>
public class EventLogTriggerSource : ITriggerSource
{
    public string ActivityType => "eventLogTrigger";

    private readonly ILogger<EventLogTriggerSource> _logger;
    private readonly IConfiguration _config;
    private EventLog? _log;
    private TriggerContext? _ctx;
    private string? _sourceFilter;
    private EventLogEntryType? _typeFilter;
    private Regex? _messageRegex;

    // 500 ms per-match cap stops a catastrophic-backtracking pattern (e.g. `(a+)+b` vs a
    // crafted event message) from pinning the EventLog callback thread.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly string[] DefaultAllowedLogs =
    {
        "Application",
        "System",
    };

    public EventLogTriggerSource(ILogger<EventLogTriggerSource> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public Task StartAsync(TriggerContext context, CancellationToken ct)
    {
        _ctx = context;
        var cfg = context.Config;
        var logName = cfg.TryGetProperty("logName", out var l) ? l.GetString() : null;
        if (string.IsNullOrWhiteSpace(logName))
            throw new InvalidOperationException("EventLogTrigger: 'logName' is required");

        var extra = _config.GetSection("Trigger:EventLog:AllowedLogs").GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToArray();
        var allowed = DefaultAllowedLogs.Concat(extra);
        if (!allowed.Any(a => string.Equals(a, logName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"EventLogTrigger: log '{logName}' is not allowed. Allowed by default: " +
                $"{string.Join(", ", DefaultAllowedLogs)}. Add it under Trigger:EventLog:AllowedLogs to permit.");

        _sourceFilter = cfg.TryGetProperty("source", out var s) ? s.GetString() : null;
        var typeStr = (cfg.TryGetProperty("entryType", out var t) ? t.GetString() : null) ?? "any";
        _typeFilter = typeStr.ToLowerInvariant() switch
        {
            "error" => EventLogEntryType.Error,
            "warning" => EventLogEntryType.Warning,
            "information" => EventLogEntryType.Information,
            "successaudit" => EventLogEntryType.SuccessAudit,
            "failureaudit" => EventLogEntryType.FailureAudit,
            _ => null,
        };
        var pattern = cfg.TryGetProperty("messagePattern", out var p) ? p.GetString() : null;
        if (!string.IsNullOrEmpty(pattern))
        {
            try
            {
                _messageRegex = new Regex(pattern, RegexOptions.Compiled, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"EventLogTrigger: invalid messagePattern regex: {ex.Message}");
            }
        }

        _log = new EventLog(logName) { EnableRaisingEvents = true };
        _log.EntryWritten += OnEntry;
        _logger.LogInformation("EventLogTrigger: subscribed to {Log} src={Src} type={Type}",
            logName, _sourceFilter ?? "*", typeStr);
        return Task.CompletedTask;
    }

    private void OnEntry(object? sender, EntryWrittenEventArgs e)
    {
        var entry = e.Entry;
        // Count every event the kernel hands us — even if filters drop it later. Lets
        // operators distinguish "log is quiet" from "filters are too strict".
        SchedulerMetrics.TriggerEvents.Add(1,
            new KeyValuePair<string, object?>("trigger_type", "eventLogTrigger"),
            new KeyValuePair<string, object?>("event_kind", entry.EntryType.ToString()));

        if (_sourceFilter is not null && !string.Equals(entry.Source, _sourceFilter, StringComparison.OrdinalIgnoreCase)) return;
        if (_typeFilter is not null && entry.EntryType != _typeFilter.Value) return;
        if (_messageRegex is not null)
        {
            try
            {
                if (!_messageRegex.IsMatch(entry.Message ?? "")) return;
            }
            catch (RegexMatchTimeoutException)
            {
                SchedulerMetrics.TriggerPollErrors.Add(1,
                    new KeyValuePair<string, object?>("trigger_type", "eventLogTrigger"),
                    new KeyValuePair<string, object?>("error_class", nameof(RegexMatchTimeoutException)));
                _logger.LogWarning("EventLogTrigger: messagePattern regex timed out on event from {Src}; skipping.", entry.Source);
                return;
            }
        }

        _ = _ctx!.OnFire(new Dictionary<string, string>
        {
            ["eventSource"] = entry.Source,
            ["eventEntryType"] = entry.EntryType.ToString(),
            ["eventId"] = entry.InstanceId.ToString(),
            ["eventMessage"] = entry.Message ?? "",
            ["eventTimeWritten"] = entry.TimeWritten.ToString("O"),
        });
    }

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

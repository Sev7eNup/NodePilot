using System.Text.Json;
using System.Text.RegularExpressions;

namespace NodePilot.Core.Triggers;

/// <summary>
/// Entry types an <c>eventLogTrigger</c> can filter on. Mirrors
/// <c>System.Diagnostics.EventLogEntryType</c> by NAME so both runtimes round-trip through
/// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> — Core deliberately does not take a
/// dependency on the Windows-only System.Diagnostics.EventLog package just to name five constants.
/// </summary>
public enum EventLogEntryTypeFilter
{
    Error,
    Warning,
    Information,
    SuccessAudit,
    FailureAudit,
}

/// <summary>
/// Outcome of applying an event-log trigger's filters to one entry. <see cref="PatternTimeout"/> is
/// distinct from <see cref="NoMatch"/> because the caller must count and log a regex timeout — a
/// silently dropped event and a pattern that blew its time budget are different operational facts.
/// </summary>
public enum EventLogMatch
{
    Match,
    NoMatch,
    PatternTimeout,
}

/// <summary>
/// The parsed, validated config of an <c>eventLogTrigger</c> node — the single vocabulary shared by
/// both runtimes that read it: <c>NodePilot.Engine.Triggers.EventLogTrigger</c> (the node executor,
/// i.e. the manual sample run) and <c>NodePilot.Scheduler.Sources.EventLogTriggerSource</c> (the
/// live EntryWritten listener).
///
/// <para>Both used to parse the node config themselves and had drifted: the listener never read
/// <c>eventId</c> or the <c>level</c> alias (so a UI-set event-id filter was silently ignored and
/// the workflow fired on every event of the log), while <c>messagePattern</c> existed only on the
/// listener and appeared in no documentation. Parsing and matching live here so a key cannot be
/// honoured by one path and dropped by the other.</para>
///
/// <para><see cref="LookbackMinutes"/> is the one deliberately asymmetric key: it bounds the manual
/// run's backwards scan and has no meaning for a live subscription. It is NOT a startup replay —
/// the orchestrator rebuilds a source on every config change and after any health fault, so a
/// replay-on-start would re-fire the same historical events each time.</para>
/// </summary>
public sealed class EventLogTriggerSettings
{
    public const string DefaultLogName = "Application";
    public const int DefaultLookbackMinutes = 5;

    /// <summary>
    /// Per-match cap that stops a catastrophic-backtracking pattern (e.g. <c>(a+)+b</c> against a
    /// crafted event message) from pinning the EventLog callback thread.
    /// </summary>
    public static readonly TimeSpan MessageRegexTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Logs any workflow author may subscribe to. <c>Security</c> is excluded on purpose: reading it
    /// needs elevation and would expose logon events and audit trails to every workflow author. An
    /// admin can add it under <c>Trigger:EventLog:AllowedLogs</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAllowedLogs = ["Application", "System"];

    public required string LogName { get; init; }
    public string? Source { get; init; }
    public long? EventId { get; init; }
    public EventLogEntryTypeFilter? EntryType { get; init; }
    public string? MessagePattern { get; init; }
    public int LookbackMinutes { get; init; }

    private Regex? MessageRegex { get; init; }

    /// <summary>
    /// Parses a node config. Throws <see cref="InvalidOperationException"/> with an operator-facing
    /// message when a value is present but unusable — the listener lets that surface as a
    /// registration failure (retried with backoff), the node executor turns it into a failed step.
    /// </summary>
    public static EventLogTriggerSettings Parse(JsonElement config)
    {
        var logName = ReadString(config, "logName");
        if (string.IsNullOrWhiteSpace(logName)) logName = DefaultLogName;

        // `level` is the legacy spelling of `entryType`. Both are documented; exact-key wins.
        var entryTypeRaw = ReadString(config, "entryType");
        if (string.IsNullOrWhiteSpace(entryTypeRaw)) entryTypeRaw = ReadString(config, "level");

        var pattern = ReadString(config, "messagePattern");
        Regex? regex = null;
        if (!string.IsNullOrEmpty(pattern))
        {
            try
            {
                // Interpreted, not Compiled: the node executor builds a fresh instance on every
                // manual run, and matching a single event message is microseconds either way.
                regex = new Regex(pattern, RegexOptions.None, MessageRegexTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"EventLogTrigger: invalid messagePattern regex: {ex.Message}");
            }
        }

        return new EventLogTriggerSettings
        {
            LogName = logName!,
            Source = NullIfBlank(ReadString(config, "source")),
            EventId = ReadInt64(config, "eventId"),
            EntryType = ParseEntryType(entryTypeRaw),
            MessagePattern = NullIfBlank(pattern),
            MessageRegex = regex,
            LookbackMinutes = Math.Max(1, ReadInt32(config, "lookbackMinutes") ?? DefaultLookbackMinutes),
        };
    }

    /// <summary>
    /// Applies every filter to one entry. Both runtimes call this so a filter can never be honoured
    /// on one path and skipped on the other.
    /// </summary>
    public EventLogMatch Matches(string? source, long eventId, EventLogEntryTypeFilter entryType, string? message)
    {
        if (Source is not null && !string.Equals(source, Source, StringComparison.OrdinalIgnoreCase))
            return EventLogMatch.NoMatch;
        if (EventId is not null && eventId != EventId.Value)
            return EventLogMatch.NoMatch;
        if (EntryType is not null && entryType != EntryType.Value)
            return EventLogMatch.NoMatch;

        if (MessageRegex is null) return EventLogMatch.Match;
        try
        {
            return MessageRegex.IsMatch(message ?? "") ? EventLogMatch.Match : EventLogMatch.NoMatch;
        }
        catch (RegexMatchTimeoutException)
        {
            return EventLogMatch.PatternTimeout;
        }
    }

    /// <summary>
    /// Whether <paramref name="logName"/> may be opened. <paramref name="additionalAllowed"/> is the
    /// admin's <c>Trigger:EventLog:AllowedLogs</c> list — it EXTENDS
    /// <see cref="DefaultAllowedLogs"/> rather than replacing it, so configuring one extra log
    /// cannot silently lock workflows out of Application and System.
    /// </summary>
    public static bool IsLogAllowed(string? logName, IEnumerable<string>? additionalAllowed)
    {
        if (string.IsNullOrWhiteSpace(logName)) return false;
        return AllowedLogs(additionalAllowed)
            .Any(a => string.Equals(a, logName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Operator-facing message for a rejected log name — identical on both runtimes.</summary>
    public static string DescribeRejectedLog(string? logName, IEnumerable<string>? additionalAllowed) =>
        $"EventLogTrigger: log '{logName}' is not allowed. Allowed: "
        + $"{string.Join(", ", AllowedLogs(additionalAllowed))}. "
        + "Add it under Trigger:EventLog:AllowedLogs to permit.";

    private static IEnumerable<string> AllowedLogs(IEnumerable<string>? additionalAllowed) =>
        DefaultAllowedLogs.Concat(
            (additionalAllowed ?? []).Where(v => !string.IsNullOrWhiteSpace(v)));

    /// <summary>
    /// Maps the documented entry-type vocabulary onto the filter enum. Accepts the canonical
    /// EventLogEntryType names plus the shorthand aliases the designer used to emit
    /// (info/critical/success/failure). An unrecognised value means "no entry-type filter" rather
    /// than an error — the same tolerance the node executor always had.
    /// </summary>
    public static EventLogEntryTypeFilter? ParseEntryType(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "error" or "critical" => EventLogEntryTypeFilter.Error,
            "warning" => EventLogEntryTypeFilter.Warning,
            "information" or "info" => EventLogEntryTypeFilter.Information,
            "successaudit" or "success" => EventLogEntryTypeFilter.SuccessAudit,
            "failureaudit" or "failure" => EventLogEntryTypeFilter.FailureAudit,
            _ => null,
        };

    private static string? ReadString(JsonElement config, string key) =>
        config.ValueKind == JsonValueKind.Object && config.TryGetProperty(key, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number ? v.ToString() : null
            : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // Numeric keys are read tolerantly: the designer writes JSON numbers, but imported and
    // AI-authored definitions routinely carry them as strings, and a hard cast would throw at
    // registration time for a value the operator can see is fine.
    private static long? ReadInt64(JsonElement config, string key)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    private static int? ReadInt32(JsonElement config, string key)
    {
        var value = ReadInt64(config, key);
        if (value is null) return null;
        return value.Value is > int.MaxValue or < int.MinValue ? null : (int)value.Value;
    }
}

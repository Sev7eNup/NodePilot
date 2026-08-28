using System.Globalization;
using NodePilot.Core.Time;

namespace NodePilot.Ai.Knowledge;

/// <summary>
/// Builds the "current time" context block appended to the knowledge-chat system prompt each turn.
/// The model has no clock of its own, and all stored and tool timestamps are UTC (global UTC value
/// converter in <c>NodePilotDbContext</c>). The block supplies the current time in UTC and in the
/// caller's local zone (sent by the browser) and instructs the model to present times locally with
/// an explicit zone label. Static and pure, so it can be tested without touching the clock.
/// </summary>
public static class KnowledgeTimeContext
{
    /// <summary>
    /// Renders the German context block. <paramref name="timeZoneId"/> is the caller's zone; the
    /// browser sends an IANA id such as <c>Europe/Berlin</c>, and the Windows form also resolves.
    /// <paramref name="offsetMinutes"/> is that zone's current UTC offset in minutes, used as a
    /// fallback. Resolution order: a resolvable zone id (honours DST), then the raw offset,
    /// then UTC alone.
    /// </summary>
    public static string Build(DateTimeOffset nowUtc, string? timeZoneId, int? offsetMinutes)
    {
        var utc = nowUtc.UtcDateTime;
        var utcLine = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        string? localLine = null;
        if (!string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneResolver.TryResolve(timeZoneId, out var tz))
        {
            var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
            localLine = $"{Format(local)} ({timeZoneId.Trim()}, {OffsetLabel(local.Offset)})";
        }

        if (localLine is null && offsetMinutes is int mins && Math.Abs(mins) <= 14 * 60)
        {
            var offset = TimeSpan.FromMinutes(mins);
            var local = nowUtc.ToOffset(offset);
            localLine = $"{Format(local)} ({OffsetLabel(offset)})";
        }

        var lines = new List<string>
        {
            "## Aktueller Zeitpunkt",
            $"Jetzt (UTC): {utcLine}",
        };
        if (localLine is not null)
            lines.Add($"Jetzt (Lokalzeit des Users): {localLine}");

        lines.Add(
            "Alle gespeicherten Zeiten (Ausführungen, Läufe, geplante Fires) sind UTC. Rechne Zeiten "
            + "für den User in dessen Lokalzeit um und nenne die Zone explizit (z. B. \"16:42 Uhr "
            + "Ortszeit / 14:42 UTC\"). Für \"wann läuft der/ein Workflow als Nächstes\" nutze das "
            + "Tool get_next_scheduled_fires statt aus vergangenen Läufen zu raten.");

        return string.Join("\n", lines);
    }

    private static string Format(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string OffsetLabel(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }
}

using System.Globalization;

namespace NodePilot.Ai.Knowledge;

/// <summary>
/// Builds the "current time" context block appended to the knowledge-chat system prompt each turn.
/// The model otherwise has no clock: it cannot answer "what time is it" or reason about "the next
/// run" without an anchor, and all stored/tool timestamps are UTC (see the global UTC value
/// converter in <c>NodePilotDbContext</c>). This block gives it <b>now</b> in both UTC and the
/// caller's local zone (supplied by the browser) and instructs it to present times locally with an
/// explicit zone label — removing the "14:42 UTC vs 16:42 local" confusion at the source.
/// Pure/static so it is unit-testable without touching the clock.
/// </summary>
public static class KnowledgeTimeContext
{
    /// <summary>
    /// Renders the German context block. <paramref name="timeZoneId"/> is the caller's IANA zone
    /// (e.g. <c>Europe/Berlin</c>); <paramref name="offsetMinutes"/> is its current UTC offset in
    /// minutes (browser fallback). Resolution order: a valid IANA zone (honours DST) → the raw
    /// offset → UTC only.
    /// </summary>
    public static string Build(DateTimeOffset nowUtc, string? timeZoneId, int? offsetMinutes)
    {
        var utc = nowUtc.UtcDateTime;
        var utcLine = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        string? localLine = null;
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
                var local = TimeZoneInfo.ConvertTime(nowUtc, tz);
                localLine = $"{Format(local)} ({timeZoneId.Trim()}, {OffsetLabel(local.Offset)})";
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
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

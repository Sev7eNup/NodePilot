namespace NodePilot.Core.Time;

/// <summary>
/// Resolves a time-zone id that may be in EITHER IANA (<c>Europe/Berlin</c>) or Windows
/// (<c>W. Europe Standard Time</c>) form, regardless of the host OS. The browser's
/// <c>Intl.DateTimeFormat().resolvedOptions().timeZone</c> always yields an IANA id, while a
/// Windows host's <see cref="TimeZoneInfo.FindSystemTimeZoneById"/> historically only knew
/// Windows ids — so a stored IANA id could fail to evaluate, and a UI default could be rejected
/// as "unknown". This resolver bridges both directions so the UI↔backend contract holds on any
/// platform without forcing the user to know which naming scheme their server uses.
/// </summary>
public static class TimeZoneResolver
{
    /// <summary>
    /// Tries to resolve <paramref name="id"/> to a <see cref="TimeZoneInfo"/>: first a direct
    /// lookup, then via IANA→Windows and Windows→IANA conversion. Returns false (and leaves
    /// <paramref name="timeZone"/> as <see cref="TimeZoneInfo.Utc"/>) if nothing matches.
    /// </summary>
    public static bool TryResolve(string? id, out TimeZoneInfo timeZone)
    {
        timeZone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        id = id.Trim();

        if (TryFind(id, out timeZone)) return true;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && TryFind(windowsId, out timeZone)) return true;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) && TryFind(ianaId, out timeZone)) return true;
        return false;
    }

    private static bool TryFind(string? id, out TimeZoneInfo timeZone)
    {
        if (string.IsNullOrWhiteSpace(id)) { timeZone = TimeZoneInfo.Utc; return false; }
        try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
            return false;
        }
    }
}

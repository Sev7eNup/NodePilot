namespace NodePilot.Core.Time;

/// <summary>
/// Resolves a time-zone id given in either IANA (<c>Europe/Berlin</c>) or Windows
/// (<c>W. Europe Standard Time</c>) form, regardless of the host OS. The browser reports IANA ids
/// via <c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>, while a Windows host may only
/// know Windows ids. Converting in both directions keeps the UI and backend agreeing on any
/// platform, without the user needing to know which naming scheme the server uses.
/// </summary>
public static class TimeZoneResolver
{
    /// <summary>
    /// Tries to resolve <paramref name="id"/> to a <see cref="TimeZoneInfo"/>: first a direct
    /// lookup, then by converting IANA to Windows and Windows to IANA. Returns false and leaves
    /// <paramref name="timeZone"/> as <see cref="TimeZoneInfo.Utc"/> if nothing matches.
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

using System.Globalization;

namespace NodePilot.Api.Diagnostics;

/// <summary>
/// Finds the segments Serilog's daily rolling file sink writes for one date:
/// <c>{name}{yyyyMMdd}.log</c> plus <c>{name}{yyyyMMdd}_001.log</c>, <c>_002</c>, ... after size rollovers.
/// </summary>
internal static class RollingLogFiles
{
    /// <summary>Segments of <paramref name="date"/> ordered by numeric roll sequence (oldest first).</summary>
    public static IReadOnlyList<string> GetFilesForDate(
        string directory, string baseNameWithoutDate, string extension, DateOnly date)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return [];
        var prefix = baseNameWithoutDate + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return Directory.EnumerateFiles(directory, prefix + "*" + extension)
            .Select(path => (Path: path, Suffix: System.IO.Path.GetFileNameWithoutExtension(path)[prefix.Length..]))
            .Select(file => (file.Path, Sequence: file.Suffix.Length == 0 ? 0 :
                file.Suffix.StartsWith('_') && int.TryParse(file.Suffix.AsSpan(1), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var sequence) && sequence > 0 ? sequence : -1))
            .Where(file => file.Sequence >= 0)
            .OrderBy(file => file.Sequence)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => file.Path)
            .ToArray();
    }
}

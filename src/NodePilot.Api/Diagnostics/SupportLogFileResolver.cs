using NodePilot.Api.Hosting;
using System.Globalization;

namespace NodePilot.Api.Diagnostics;

/// <summary>
/// Resolves the on-disk paths of the Support-Log rolling files. Used by the
/// DiagnosticsController for tail + download endpoints. A separate service (not just a
/// static helper) so tests can mock the file layout.
/// </summary>
public interface ISupportLogFileResolver
{
    /// <summary>Today's segments in write order, using Serilog's local rolling date.</summary>
    IReadOnlyList<string> GetCurrentDayFiles();

    /// <summary>All existing segments for a date, ordered by numeric roll sequence.</summary>
    IReadOnlyList<string> GetFilesForDate(DateOnly date);

    /// <summary>Directory that holds the Support Log files.</summary>
    string Directory { get; }

    /// <summary>Glob pattern for the daily files (e.g. <c>nodepilot-support-*.log</c>).</summary>
    string FileSearchPattern { get; }
}

internal sealed class SupportLogFileResolver : ISupportLogFileResolver
{
    private readonly string _basePath;
    private readonly string _baseNameWithoutDate; // e.g. "nodepilot-support-"
    private readonly string _extension; // e.g. ".log"

    public SupportLogFileResolver(IConfiguration configuration, IWebHostEnvironment env)
    {
        _basePath = LoggingSetup.ResolveSupportLogFilePath(configuration, env.ContentRootPath);
        var fileName = Path.GetFileNameWithoutExtension(_basePath); // "nodepilot-support-"
        _extension = Path.GetExtension(_basePath); // ".log"
        // Serilog's RollingInterval.Day appends "yyyyMMdd" between the base name and extension
        // (without separator) when the path template ends with a trailing '-' before the
        // extension. Our path resolution preserves that template (path ends with "support-.log").
        _baseNameWithoutDate = fileName; // ends with '-'
    }

    public string Directory => Path.GetDirectoryName(_basePath) ?? "";

    public string FileSearchPattern => _baseNameWithoutDate + "*" + _extension;

    public IReadOnlyList<string> GetCurrentDayFiles() => GetFilesForDate(DateOnly.FromDateTime(DateTime.Today));

    public IReadOnlyList<string> GetFilesForDate(DateOnly date)
    {
        var dir = Directory;
        if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return [];
        var prefix = _baseNameWithoutDate + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return System.IO.Directory.EnumerateFiles(dir, prefix + "*" + _extension)
            .Select(path => (Path: path, Suffix: Path.GetFileNameWithoutExtension(path)[prefix.Length..]))
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

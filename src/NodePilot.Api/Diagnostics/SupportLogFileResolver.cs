using NodePilot.Api.Hosting;

namespace NodePilot.Api.Diagnostics;

/// <summary>
/// Resolves the on-disk paths of the Support-Log rolling files. Used by the
/// DiagnosticsController for tail + download endpoints. A separate service (not just a
/// static helper) so tests can mock the file layout.
/// </summary>
public interface ISupportLogFileResolver
{
    /// <summary>Today's daily file (e.g. <c>nodepilot-support-20260515.log</c>) — may be missing if nothing has been logged yet today.</summary>
    string? GetCurrentDayFile();

    /// <summary>Path to the daily file for a given date. Exists once the file has rolled over.</summary>
    string? GetFileForDate(DateOnly date);

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

    public string? GetCurrentDayFile() => GetFileForDate(DateOnly.FromDateTime(DateTime.UtcNow.Date));

    public string? GetFileForDate(DateOnly date)
    {
        var dir = Directory;
        if (string.IsNullOrEmpty(dir)) return null;
        var candidate = Path.Combine(dir,
            $"{_baseNameWithoutDate}{date:yyyyMMdd}{_extension}");
        return File.Exists(candidate) ? candidate : null;
    }
}

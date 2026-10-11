using NodePilot.Api.Hosting;

namespace NodePilot.Api.Diagnostics;

/// <summary>Resolves the rolling files of the application log (<c>nodepilot-.log</c>).</summary>
public interface IAppLogFileResolver
{
    /// <summary>All existing segments for a date, ordered by numeric roll sequence (oldest first).</summary>
    IReadOnlyList<string> GetFilesForDate(DateOnly date);
}

internal sealed class AppLogFileResolver : IAppLogFileResolver
{
    private readonly string _directory;
    private readonly string _baseNameWithoutDate;
    private readonly string _extension;

    public AppLogFileResolver(IConfiguration configuration, IWebHostEnvironment env)
    {
        var basePath = LoggingSetup.ResolveLogFilePath(configuration, env.ContentRootPath);
        _directory = Path.GetDirectoryName(basePath) ?? "";
        _baseNameWithoutDate = Path.GetFileNameWithoutExtension(basePath);
        _extension = Path.GetExtension(basePath);
    }

    public IReadOnlyList<string> GetFilesForDate(DateOnly date)
        => RollingLogFiles.GetFilesForDate(_directory, _baseNameWithoutDate, _extension, date);
}

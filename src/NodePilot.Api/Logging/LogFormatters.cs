using Serilog;
using Serilog.Configuration;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace NodePilot.Api.Logging;

internal static class LogFormatters
{
    /// <summary>
    /// Returns an <see cref="ITextFormatter"/> for the configured format, or <c>null</c>
    /// when the caller should fall back to a plain output-template string.
    /// Supported values (case-insensitive):
    ///  - <c>text</c> (default): plain output template
    ///  - <c>cmtrace</c>: ConfigMgr CMTrace-readable
    ///  - <c>json</c>: Serilog Compact JSON (CLEF) — generic, parseable, but not SIEM-friendly
    ///  - <c>ecs-json</c>: Elastic Common Schema 1.x — for Elastic / Sentinel / Splunk HEC ingest,
    ///    NodePilot-domain properties land under <c>nodepilot.*</c>
    /// </summary>
    internal static ITextFormatter? Create(string? format) =>
        format?.ToLowerInvariant() switch
        {
            "cmtrace"  => new CmTraceFormatter(),
            "json"     => new CompactJsonFormatter(),
            "ecs-json" => new EcsJsonFormatter(),
            _          => null
        };

    // shared:true → lets concurrent readers (tail -f, grep, Get-Content -Wait) observe the
    // file without Serilog holding an exclusive write lock. flushToDiskInterval forces the
    // OS to sync dirty pages every second so directory size/mtime reflect reality within
    // ~1s — without it Windows leaves metadata stale for the lifetime of the write handle,
    // which made log activity look silent even when entries were streaming fine.
    private static readonly TimeSpan LiveFlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Adds the rolling file sink using either a structured <paramref name="formatter"/>
    /// (cmtrace / json) or the plain <paramref name="outputTemplate"/> (text default).
    /// </summary>
    internal static LoggerConfiguration ApplyFormat(
        this LoggerSinkConfiguration writeTo,
        ITextFormatter? formatter,
        string path,
        string outputTemplate,
        int retainedFileCountLimit,
        long fileSizeLimitBytes) =>
        formatter is not null
            ? writeTo.File(formatter, path,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retainedFileCountLimit,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                flushToDiskInterval: LiveFlushInterval)
            : writeTo.File(path,
                rollingInterval: RollingInterval.Day,
                outputTemplate: outputTemplate,
                retainedFileCountLimit: retainedFileCountLimit,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                flushToDiskInterval: LiveFlushInterval);
}

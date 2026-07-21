using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodePilot.Engine.Security;

namespace NodePilot.Scheduler.Sources;

/// <summary>
/// Watches a directory for file create/change/delete events. Config keys:
///   directory (required) — absolute path to watch
///   filter (optional) — glob like "*.log", default "*"
///   watchType (optional) — "created" | "changed" | "deleted" | "any" (default "created")
///   includeSubdirectories (optional, default false)
/// Debounces duplicate events within 500 ms.
///
/// Path safety: the directory must resolve inside one of the roots listed under
/// <c>Trigger:FileWatcher:AllowedRoots</c>. System directories (Windows, Program Files,
/// user profile secret subtrees) are blocked even when the root list is empty so a
/// workflow author can't point the API process at <c>C:\Windows\System32</c> and harvest
/// metadata. Admins who genuinely need a system path can add it to AllowedRoots.
/// </summary>
public class FileWatcherTriggerSource : ITriggerSource
{
    public string ActivityType => "fileWatcherTrigger";

    private readonly ILogger<FileWatcherTriggerSource> _logger;
    private readonly IConfiguration _config;
    private FileSystemWatcher? _watcher;
    private TriggerContext? _ctx;
    private string? _directory;

    // M-28: per-path debounce. A single DateTime was wrong because two simultaneous writes
    // to different files ("a.log" + "b.log") would suppress one of them. Keep a tiny map
    // keyed on full-path and prune when it grows so a high-churn directory doesn't leak.
    private readonly ConcurrentDictionary<string, DateTime> _lastFirePerPath = new();
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(500);

    public FileWatcherTriggerSource(ILogger<FileWatcherTriggerSource> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public Task StartAsync(TriggerContext context, CancellationToken ct)
    {
        _ctx = context;
        var cfg = context.Config;
        var dir = cfg.TryGetProperty("directory", out var d) ? d.GetString() : null;
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException("FileWatcherTrigger: 'directory' is required");

        ValidateDirectory(dir);

        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"FileWatcherTrigger: directory '{dir}' does not exist");

        var filter = cfg.TryGetProperty("filter", out var f) ? f.GetString() ?? "*" : "*";
        var watchType = (cfg.TryGetProperty("watchType", out var wt) ? wt.GetString() : null)?.ToLowerInvariant() ?? "created";
        var includeSub = cfg.TryGetProperty("includeSubdirectories", out var is_) && is_.ValueKind == JsonValueKind.True;

        _directory = dir;
        _watcher = new FileSystemWatcher(dir, filter)
        {
            IncludeSubdirectories = includeSub,
            EnableRaisingEvents = false,
            // M-28: default InternalBufferSize is 8 KiB which overflows quickly under a
            // burst (tens of events in the same millisecond). Overflow drops events
            // silently. 64 KiB is the FSW-documented practical upper bound — beyond that
            // the kernel either rejects the allocation or the cost outweighs the benefit.
            InternalBufferSize = 65536,
        };

        // Subscribe to Error so a buffer overflow or native-handle failure is at least
        // logged instead of silently dropping everything until the next file event.
        _watcher.Error += OnWatcherError;

        void HandleEvent(string action, string path)
        {
            // Count every raw FSW event before debounce — operators chasing "trigger fires too
            // often" need to see whether the noise is the watcher itself or our dispatch.
            SchedulerMetrics.TriggerEvents.Add(1,
                new KeyValuePair<string, object?>("trigger_type", "fileWatcherTrigger"),
                new KeyValuePair<string, object?>("event_kind", action));

            var now = DateTime.UtcNow;
            var last = _lastFirePerPath.TryGetValue(path, out var prev) ? prev : DateTime.MinValue;
            if ((now - last) < DebounceWindow) return;
            _lastFirePerPath[path] = now;

            // Prune the per-path map when it grows beyond a reasonable threshold. Cheap
            // and amortized — the map only grows under a genuinely high-churn directory,
            // and entries older than 10 min can never contribute to another debounce hit.
            if (_lastFirePerPath.Count > 1000)
            {
                var cutoff = now.AddMinutes(-10);
                foreach (var kv in _lastFirePerPath)
                    if (kv.Value < cutoff)
                        _lastFirePerPath.TryRemove(kv.Key, out _);
            }

            _ = _ctx!.OnFire(new Dictionary<string, string>
            {
                ["fileAction"] = action,
                ["filePath"] = path,
                ["fileName"] = Path.GetFileName(path),
            });
        }

        if (watchType is "created" or "any") _watcher.Created += (_, e) => HandleEvent("created", e.FullPath);
        if (watchType is "changed" or "any") _watcher.Changed += (_, e) => HandleEvent("changed", e.FullPath);
        if (watchType is "deleted" or "any") _watcher.Deleted += (_, e) => HandleEvent("deleted", e.FullPath);
        // B3: "renamed" first-class plus "any" covers it for the UI's "All Changes" option.
        // Previously the UI offered "renamed"/"all" labels but the source had no Renamed
        // subscription — the "All Changes" workflow lost every rename event silently.
        if (watchType is "renamed" or "any") _watcher.Renamed += (_, e) => HandleEvent("renamed", e.FullPath);

        _watcher.EnableRaisingEvents = true;
        _logger.LogInformation("FileWatcher: watching {Dir} filter={Filter} type={Type} sub={Sub}",
            dir, filter, watchType, includeSub);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
        _lastFirePerPath.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// FSW raises Error when its internal buffer overflows or the underlying native handle
    /// hits a fatal condition. Default behavior is to silently drop subsequent events,
    /// which turns this trigger into a ticking time bomb. Logging makes overflow diagnosable.
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        SchedulerMetrics.TriggerPollErrors.Add(1,
            new KeyValuePair<string, object?>("trigger_type", "fileWatcherTrigger"),
            new KeyValuePair<string, object?>("error_class", e.GetException().GetType().Name));
        _logger.LogError(e.GetException(),
            "FileWatcher error on directory '{Dir}' — buffer overflow or native handle failure. " +
            "Subsequent events may be lost until the next event fires.", _directory);
    }

    // ValidateDirectory is factored out into FileWatcherPathGuard so the manual executor
    // (FileWatcherTrigger.ExecuteAsync) runs the same allow-list + hard-block check.
    private void ValidateDirectory(string dir) => FileWatcherPathGuard.Validate(_config, dir);
}

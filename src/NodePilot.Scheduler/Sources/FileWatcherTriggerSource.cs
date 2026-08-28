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
///   watchType (optional) — "created" | "changed" | "deleted" | "renamed" | "any" (default "created")
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

    /// <summary>
    /// Faulted once the watcher is provably dead. Pure field read — no I/O, per the
    /// <see cref="ITriggerSource.Health"/> contract.
    ///
    /// Deliberately NOT derived from <c>_watcher.EnableRaisingEvents</c>: in the runtime's
    /// <c>Monitor()</c> re-issue failure path the directory handle is disposed but <c>_enabled</c>
    /// is never cleared, so the property still reads <c>true</c> on a corpse. Worse, the setter
    /// early-returns when the value is unchanged, so assigning <c>true</c> again cannot revive it.
    /// Only a fresh <see cref="FileSystemWatcher"/> instance works — which is why the verdict goes
    /// to the orchestrator instead of being handled locally.
    /// </summary>
    public TriggerHealth Health =>
        _faultReason is { } reason ? TriggerHealth.Faulted(reason) : TriggerHealth.Healthy;

    private readonly ILogger<FileWatcherTriggerSource> _logger;
    private readonly IConfiguration _config;
    private FileSystemWatcher? _watcher;
    private TriggerContext? _ctx;
    private string? _directory;
    private string _filter = "*";
    private string _watchType = "created";
    private bool _includeSubdirectories;
    private CancellationTokenSource? _deliveryCts;
    private readonly SemaphoreSlim _deliveryGate = new(1, 1);
    private Dictionary<string, FileStamp> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private TriggerCheckpoint? _checkpoint;

    // null == healthy. Written by OnWatcherError and ProbeLoopAsync; read by the orchestrator's
    // sync pass on a different thread, hence volatile.
    private volatile string? _faultReason;

    private CancellationTokenSource? _probeCts;
    private Task? _probeTask;

    // A single failed probe must never evict a healthy watcher — that would cause exactly the
    // event loss the probe exists to prevent. Not configurable: the cost is only detection
    // latency (<= 2x the probe interval) against a baseline of "dead forever".
    private const int ProbeFailuresBeforeFault = 2;

    /// <summary>
    /// How this source decides a path is reachable — used both when registering and by the health
    /// probe, so there is one answer to that question.
    ///
    /// Test-only seam. Neither case can be staged against the real filesystem: deleting a watched
    /// directory DOES raise an FSW Error (Win32Exception, access denied), so the primary fault path
    /// fires first and masks the probe entirely, and a path that hangs instead of answering needs a
    /// genuinely unreachable host. Production never assigns this.
    /// </summary>
    internal Func<string, bool> DirectoryProbe { get; set; } = Directory.Exists;

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

    public async Task StartAsync(TriggerContext context, CancellationToken ct)
    {
        _ctx = context;
        var cfg = context.Config;
        var dir = cfg.TryGetProperty("directory", out var d) ? d.GetString() : null;
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException("FileWatcherTrigger: 'directory' is required");

        var filter = cfg.TryGetProperty("filter", out var f) ? f.GetString() ?? "*" : "*";
        var watchType = (cfg.TryGetProperty("watchType", out var wt) ? wt.GetString() : null)?.ToLowerInvariant() ?? "created";
        var includeSub = cfg.TryGetProperty("includeSubdirectories", out var is_) && is_.ValueKind == JsonValueKind.True;

        _directory = dir;
        _filter = filter;
        _watchType = watchType;
        _includeSubdirectories = includeSub;
        _deliveryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Every filesystem touch below happens off this thread under a hard deadline. Three calls
        // can block on the SMB redirector for tens of seconds against an unreachable host — the
        // explicit Directory.Exists, the FileSystemWatcher constructor (CheckPathValidity does its
        // own Directory.Exists), and the arming assignment (which opens the directory handle).
        // The orchestrator's sync pass registers triggers sequentially, so an unbounded start on
        // one dead UNC path stalls trigger reconciliation for the whole installation.
        // Bounding at the orchestrator's call site would not work: this method does its work
        // inline, so by the time a Task exists to WaitAsync on, the blocking already happened.
        var pathTimeout = TimeSpan.FromSeconds(
            Math.Max(1, _config.GetValue<int?>("Trigger:FileWatcher:PathTimeoutSeconds") ?? 5));

        FileSystemWatcher watcher;
        try
        {
            watcher = await RunBoundedAsync(
                () => BuildAndArmWatcher(dir, filter, includeSub),
                pathTimeout, static w => w.Dispose(), ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"FileWatcherTrigger: directory '{dir}' did not respond within {pathTimeout.TotalSeconds:F0}s " +
                "(unreachable share or hung SMB redirector).");
        }

        // Publishes the instance the event handlers gate on. Between the arming inside
        // BuildAndArmWatcher and this assignment there is a sub-millisecond window in which an
        // event is dropped by that identity guard — the safe direction, and the same class of
        // gap as the failover window documented in docs/ha-active-passive.md.
        Volatile.Write(ref _watcher, watcher);

        _checkpoint = await context.ReadCheckpointAsync();
        var initialSnapshot = CaptureSnapshot();
        if (_checkpoint is null)
        {
            var position = JsonSerializer.Serialize(initialSnapshot);
            var seeded = new TriggerCheckpoint(position, $"file-seed:{Guid.NewGuid():N}");
            if (!await context.InitializeCheckpointAsync(seeded))
                throw new InvalidOperationException("FileWatcherTrigger: durable snapshot could not be initialized");
            _checkpoint = seeded;
            _snapshot = initialSnapshot;
        }
        else
        {
            _snapshot = DeserializeSnapshot(_checkpoint.Position);
            // Replay changes missed during downtime and close the checkpoint-to-watcher gap.
            await ReconcileAsync(_deliveryCts.Token);
        }

        var probeSeconds = _config.GetValue<int?>("Trigger:FileWatcher:HealthProbeSeconds") ?? 60;
        if (probeSeconds > 0)
        {
            _probeCts = new CancellationTokenSource();
            _probeTask = ProbeLoopAsync(dir, TimeSpan.FromSeconds(probeSeconds), pathTimeout, _probeCts.Token);
        }

        _logger.LogInformation("FileWatcher: watching {Dir} filter={Filter} type={Type} sub={Sub}",
            dir, filter, watchType, includeSub);
    }

    /// <summary>
    /// Catches the watcher dying without raising Error: when the host behind a share goes away
    /// hard, the pending ReadDirectoryChangesW is simply never completed by the redirector, so no
    /// fault is ever delivered and the watcher is silently deaf. This is strictly a backstop —
    /// measured behavior is that both a vanishing share and a deleted local directory normally do
    /// raise Error, which faults the source within milliseconds instead of within a probe interval.
    ///
    /// KNOWN GAP: this does not catch an SMB session that reconnects without re-arming the
    /// server-side change notification — Directory.Exists returns true in that state. Detecting
    /// that would need an active canary (writing a sentinel file into a user's directory, which
    /// would also fire the trigger), which is not worth it.
    ///
    /// Runs on its own task, never on the orchestrator's sync pass: Directory.Exists against a
    /// dead UNC path can block for the SMB timeout, and stalling here only delays this source's
    /// own next probe.
    /// </summary>
    private async Task ProbeLoopAsync(string dir, TimeSpan interval, TimeSpan timeout, CancellationToken ct)
    {
        var consecutiveFailures = 0;
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // Already faulted through the Error event — the orchestrator owns the case now.
                if (_faultReason is not null) return;

                bool reachable;
                try
                {
                    reachable = await RunBoundedAsync(
                        () => DirectoryProbe(dir), timeout, disposeAbandoned: null, ct);
                }
                catch (TimeoutException)
                {
                    reachable = false;
                }

                if (reachable)
                {
                    consecutiveFailures = 0;
                    // The scan is both a silent-death probe and overflow recovery. Persistent
                    // snapshots turn missed create/change/delete notifications into replayable
                    // signals instead of relying solely on the finite kernel buffer.
                    try { await ReconcileAsync(ct); }
                    catch (IOException ex) { _logger.LogWarning(ex, "FileWatcher reconciliation scan failed for {Dir}.", dir); }
                    catch (UnauthorizedAccessException ex) { _logger.LogWarning(ex, "FileWatcher reconciliation scan was denied for {Dir}.", dir); }
                    continue;
                }
                if (++consecutiveFailures < ProbeFailuresBeforeFault) continue;

                _faultReason ??= $"directory '{dir}' unreachable on {consecutiveFailures} consecutive health probes";
                SchedulerMetrics.TriggerPollErrors.Add(1,
                    new KeyValuePair<string, object?>("trigger_type", "fileWatcherTrigger"),
                    new KeyValuePair<string, object?>("error_class", "HealthProbeUnreachable"));
                _logger.LogWarning(
                    "FileWatcher health probe: {Reason}. Marking the source faulted so the orchestrator re-creates it.",
                    _faultReason);
                return; // one verdict is enough; the orchestrator takes it from here
            }
        }
        catch (OperationCanceledException) { /* disposal */ }
    }

    /// <summary>
    /// Runs a blocking filesystem call off the caller's thread under a hard deadline.
    ///
    /// TRADEOFF: on timeout the work is abandoned but its thread stays blocked in the OS call
    /// until that call returns — there is no way to cancel a pending SMB operation. Bounded in
    /// practice: at most one orphan per registration attempt or probe tick. If the abandoned work
    /// still produces a result, <paramref name="disposeAbandoned"/> releases it — an armed
    /// FileSystemWatcher nobody owns would otherwise keep firing workflows forever.
    ///
    /// Internal so the deadline and the abandon-cleanup are testable without a hung network path.
    /// </summary>
    internal static async Task<T> RunBoundedAsync<T>(
        Func<T> work, TimeSpan timeout, Action<T>? disposeAbandoned, CancellationToken ct)
    {
        var task = Task.Run(work, CancellationToken.None);
        try
        {
            return await task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully) disposeAbandoned?.Invoke(t.Result);
                    else _ = t.Exception; // observe, so an abandoned failure isn't an unobserved fault
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// Constructs, subscribes and arms the watcher. Runs on a thread-pool thread under the
    /// caller's deadline — everything here may block on an unreachable path.
    /// </summary>
    private FileSystemWatcher BuildAndArmWatcher(string dir, string filter, bool includeSub)
    {
        // Keep canonicalization in the same bounded target-side operation as handle creation,
        // immediately before the first filesystem touch. A concurrent rename after this check
        // is still an OS-level race; ACLs on watched roots remain the authoritative control.
        ValidateDirectory(dir);

        // Kept even though the FileSystemWatcher constructor checks the path itself: this
        // produces the friendly DirectoryNotFoundException that callers and tests rely on,
        // where CheckPathValidity would throw a raw ArgumentException.
        if (!DirectoryProbe(dir))
            throw new DirectoryNotFoundException($"FileWatcherTrigger: directory '{dir}' does not exist");

        if (includeSub)
            FileWatcherPathGuard.ValidateReparseFreeSubtree(dir);

        // Revalidate the watched root after the subtree walk and immediately before the
        // constructor obtains its native handle. This narrows, but path APIs cannot eliminate,
        // a concurrent parent-directory replacement race.
        ValidateDirectory(dir);

        var watcher = new FileSystemWatcher(dir, filter)
        {
            IncludeSubdirectories = includeSub,
            EnableRaisingEvents = false,
            // M-28: default InternalBufferSize is 8 KiB which overflows quickly under a
            // burst (tens of events in the same millisecond). Overflow drops events
            // silently. 64 KiB is the FSW-documented practical upper bound — beyond that
            // the kernel either rejects the allocation or the cost outweighs the benefit.
            InternalBufferSize = 65536,
        };

        // Subscribe to Error so a buffer overflow is logged and a fatal handle failure marks this
        // source unhealthy — the orchestrator then disposes and re-creates it.
        watcher.Error += OnWatcherError;

        void HandleEvent(string action, string path, string? oldPath = null)
        {
            // Identity guard: a build whose deadline expired is abandoned but may still finish
            // and arm itself on its orphaned thread. Only the instance StartAsync published may
            // fire — otherwise a timed-out registration attempt would keep triggering workflows.
            if (!ReferenceEquals(watcher, Volatile.Read(ref _watcher))) return;

            // A junction can be created after the startup preflight. Never dispatch an event
            // whose current path traverses one; validation uses link-local attributes and thus
            // does not itself follow a link to a UNC target.
            try { FileWatcherPathGuard.Validate(_config, path); }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(
                    ex,
                    "FileWatcher suppressed {Action} event for unsafe path '{Path}'",
                    action,
                    path);
                return;
            }

            // Count every raw FSW event before debounce — operators chasing "trigger fires too
            // often" need to see whether the noise is the watcher itself or our dispatch.
            SchedulerMetrics.TriggerEvents.Add(1,
                new KeyValuePair<string, object?>("trigger_type", "fileWatcherTrigger"),
                new KeyValuePair<string, object?>("event_kind", action));

            var shouldFire = Watches(action);
            var now = DateTime.UtcNow;
            if (shouldFire)
            {
                var last = _lastFirePerPath.TryGetValue(path, out var prev) ? prev : DateTime.MinValue;
                if ((now - last) < DebounceWindow) return;
                _lastFirePerPath[path] = now;
            }

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

            TriggerFireObserver.Observe(
                DeliverObservedAsync(
                    action, path, oldPath, shouldFire, $"file:{Guid.NewGuid():N}", new Dictionary<string, string>
                {
                    ["fileAction"] = action,
                    ["filePath"] = path,
                    ["fileName"] = Path.GetFileName(path),
                    // Both are trivially derivable from filePath, but not from a {{…}} template —
                    // there is no expression language there. Without them, "take the dropped file's
                    // name, put a different extension on it" and "work in the folder it landed in"
                    // both need a script step for what is really just addressing the event.
                    ["fileNameWithoutExtension"] = Path.GetFileNameWithoutExtension(path),
                    ["fileDirectory"] = Path.GetDirectoryName(path) ?? "",
                }),
                _logger, ActivityType, _ctx!.WorkflowId, _ctx.NodeId);
        }

        // Track every raw event in the durable snapshot, even if the user selected only one event
        // kind. Otherwise an ignored create followed by a selected delete (or vice versa) leaves
        // the baseline stale and makes restart/overflow reconciliation invent or miss changes.
        watcher.Created += (_, e) => HandleEvent("created", e.FullPath);
        watcher.Changed += (_, e) => HandleEvent("changed", e.FullPath);
        watcher.Deleted += (_, e) => HandleEvent("deleted", e.FullPath);
        watcher.Renamed += (_, e) => HandleEvent("renamed", e.FullPath, e.OldFullPath);

        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private async Task DeliverObservedAsync(
        string action,
        string path,
        string? oldPath,
        bool shouldFire,
        string eventKey,
        Dictionary<string, string> parameters)
    {
        var ct = _deliveryCts?.Token ?? CancellationToken.None;
        await _deliveryGate.WaitAsync(ct);
        try
        {
            var next = new Dictionary<string, FileStamp>(_snapshot, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(oldPath))
            {
                next.Remove(oldPath);
                parameters["fileOldPath"] = oldPath;
            }
            if (action == "deleted")
                next.Remove(path);
            else if (TryReadStamp(path, out var stamp))
                next[path] = stamp;

            var position = JsonSerializer.Serialize(next);
            var checkpoint = new TriggerCheckpoint(position, eventKey);
            if (shouldFire)
            {
                var signal = new TriggerSignal(eventKey, position, parameters);
                while (!ct.IsCancellationRequested && !await _ctx!.DeliverAsync(signal))
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            else
            {
                while (!ct.IsCancellationRequested && !await _ctx!.SaveCheckpointAsync(checkpoint))
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }

            if (!ct.IsCancellationRequested)
            {
                _snapshot = next;
                _checkpoint = checkpoint;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        await _deliveryGate.WaitAsync(ct);
        try
        {
            var current = CaptureSnapshot();
            var changes = new List<(string action, string path, string? oldPath)>();

            var created = current.Keys.Except(_snapshot.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var deleted = _snapshot.Keys.Except(current.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A snapshot has no native rename identity. Pair only unique old/new stamps; an
            // ambiguous match is left as create+delete for "any" and cannot safely be called a
            // rename. Live watcher notifications still carry the exact old path.
            if (_watchType is "renamed" or "any")
            {
                foreach (var oldPath in deleted.ToList())
                {
                    var candidates = created.Where(path => current[path] == _snapshot[oldPath]).ToList();
                    if (candidates.Count != 1) continue;
                    var newPath = candidates[0];
                    if (deleted.Count(path => _snapshot[path] == current[newPath]) != 1) continue;
                    changes.Add(("renamed", newPath, oldPath));
                    deleted.Remove(oldPath);
                    created.Remove(newPath);
                }
            }

            if (_watchType is "created" or "any")
                changes.AddRange(created.Select(path => ("created", path, (string?)null)));
            if (_watchType is "changed" or "any")
                changes.AddRange(current.Keys.Intersect(_snapshot.Keys, StringComparer.OrdinalIgnoreCase)
                    .Where(path => current[path] != _snapshot[path])
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Select(path => ("changed", path, (string?)null)));
            if (_watchType is "deleted" or "any")
                changes.AddRange(deleted.Select(path => ("deleted", path, (string?)null)));

            foreach (var (action, path, oldPath) in changes)
            {
                ct.ThrowIfCancellationRequested();
                var next = new Dictionary<string, FileStamp>(_snapshot, StringComparer.OrdinalIgnoreCase);
                if (oldPath is not null) next.Remove(oldPath);
                if (action == "deleted") next.Remove(path);
                else next[path] = current[path];
                var basis = (_checkpoint?.Version ?? "none") + "\0" + action + "\0" + oldPath + "\0" + path;
                var eventKey = "file-reconcile:" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(basis)));
                var signal = BuildSignal(action, path, oldPath, eventKey, next);
                if (!await _ctx!.DeliverAsync(signal)) return;
                _snapshot = next;
                _checkpoint = new TriggerCheckpoint(signal.Position, signal.EventKey);
            }

            if (_snapshot.Count != current.Count
                || _snapshot.Any(item => !current.TryGetValue(item.Key, out var stamp) || stamp != item.Value))
            {
                var position = JsonSerializer.Serialize(current);
                var basis = (_checkpoint?.Version ?? "none") + "\0baseline\0" + position;
                var baseline = new TriggerCheckpoint(
                    position,
                    "file-baseline:" + Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(basis))));
                if (await _ctx!.SaveCheckpointAsync(baseline))
                {
                    _snapshot = current;
                    _checkpoint = baseline;
                }
            }
        }
        finally
        {
            _deliveryGate.Release();
        }
    }

    private static TriggerSignal BuildSignal(
        string action,
        string path,
        string? oldPath,
        string eventKey,
        Dictionary<string, FileStamp> next)
    {
        var parameters = new Dictionary<string, string>
            {
                ["fileAction"] = action,
                ["filePath"] = path,
                ["fileName"] = Path.GetFileName(path),
                ["fileNameWithoutExtension"] = Path.GetFileNameWithoutExtension(path),
                ["fileDirectory"] = Path.GetDirectoryName(path) ?? "",
            };
        if (oldPath is not null) parameters["fileOldPath"] = oldPath;
        return new TriggerSignal(eventKey, JsonSerializer.Serialize(next), parameters);
    }

    private bool Watches(string action) => _watchType == "any" || _watchType == action;

    private Dictionary<string, FileStamp> CaptureSnapshot()
    {
        var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        if (_directory is null) return result;
        var option = _includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var path in Directory.EnumerateFiles(_directory, _filter, option))
            if (TryReadStamp(path, out var stamp)) result[path] = stamp;
        return result;
    }

    private static bool TryReadStamp(string path, out FileStamp stamp)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { stamp = default; return false; }
            stamp = new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (IOException) { stamp = default; return false; }
        catch (UnauthorizedAccessException) { stamp = default; return false; }
    }

    private static Dictionary<string, FileStamp> DeserializeSnapshot(string json)
    {
        var snapshot = JsonSerializer.Deserialize<Dictionary<string, FileStamp>>(json)
                       ?? new Dictionary<string, FileStamp>();
        return new Dictionary<string, FileStamp>(snapshot, StringComparer.OrdinalIgnoreCase);
    }

    private readonly record struct FileStamp(long Length, long LastWriteUtcTicks);

    public async ValueTask DisposeAsync()
    {
        if (_probeCts is not null)
        {
            await _probeCts.CancelAsync();
            try { if (_probeTask is not null) await _probeTask; } catch { /* ignore */ }
            _probeCts.Dispose();
            _probeCts = null;
            _probeTask = null;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
            _watcher = null;
        }
        if (_deliveryCts is not null)
        {
            await _deliveryCts.CancelAsync();
            _deliveryCts.Dispose();
            _deliveryCts = null;
        }
        _lastFirePerPath.Clear();
    }

    /// <summary>
    /// Maps an FSW Error-event exception to a fault reason, or null when the watcher survives it.
    ///
    /// Verified against the runtime's FileSystemWatcher.Windows.cs: a buffer overflow raises
    /// <see cref="InternalBufferOverflowException"/> and the read callback's finally block
    /// unconditionally re-issues ReadDirectoryChangesW, so the watcher keeps running. Treating
    /// that as a fault would make the source flap under sustained churn — dispose/re-create in a
    /// loop, losing more events than the overflow did.
    ///
    /// Every other completion error is terminal: the callback either clears EnableRaisingEvents
    /// or the re-issue path disposes the directory handle, in both cases BEFORE raising Error.
    /// Such a watcher never recovers on its own and cannot be re-armed in place (see
    /// <see cref="Health"/>), so the verdict has to travel to the orchestrator.
    ///
    /// Internal + static so the decision is unit-testable without owning a live watcher.
    /// </summary>
    internal static string? ClassifyWatcherError(Exception ex) =>
        ex is InternalBufferOverflowException ? null : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// Internal rather than private so tests can drive a fault without provoking a real
    /// native-handle failure. Production only ever reaches this via the FSW Error event.
    /// </summary>
    internal void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        SchedulerMetrics.TriggerPollErrors.Add(1,
            new KeyValuePair<string, object?>("trigger_type", "fileWatcherTrigger"),
            new KeyValuePair<string, object?>("error_class", ex.GetType().Name));

        if (ClassifyWatcherError(ex) is { } fault)
        {
            // First fault wins — later noise on the way down must not rewrite the reason the
            // operator gets to see.
            _faultReason ??= fault;
            _logger.LogError(ex,
                "FileWatcher on '{Dir}' faulted and is dead ({Reason}). The orchestrator will dispose " +
                "and re-create it; while the path stays unreachable, registration backs off up to 5 minutes.",
                _directory, fault);
            return;
        }

        if (_deliveryCts is { } delivery)
            TriggerFireObserver.Observe(
                ReconcileAsync(delivery.Token),
                _logger,
                ActivityType,
                _ctx!.WorkflowId,
                _ctx.NodeId);
        _logger.LogWarning(ex,
            "FileWatcher buffer overflow on '{Dir}' — events were dropped, but the watcher keeps running.",
            _directory);
    }

    // ValidateDirectory is factored out into FileWatcherPathGuard so the manual executor
    // (FileWatcherTrigger.ExecuteAsync) runs the same allow-list + hard-block check.
    private void ValidateDirectory(string dir) => FileWatcherPathGuard.Validate(_config, dir);
}

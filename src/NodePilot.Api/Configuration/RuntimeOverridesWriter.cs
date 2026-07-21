using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NodePilot.Api.Configuration;

/// <summary>
/// Owns reading/writing of <c>appsettings.runtime.json</c> — the UI-managed override
/// file that sits between <c>appsettings.{Env}.json</c> (Installer-Bootstrap) and the
/// EnvVar/CLI providers in the configuration chain.
///
/// <para>Responsibilities:</para>
/// <list type="bullet">
///   <item>Atomic writes via <see cref="File.Replace(string, string, string)"/> with a
///   UNC/non-NTFS fallback to copy+move. Crash between tmp-write and replace leaves the
///   original file intact.</item>
///   <item>Backup rotation: every successful write produces a timestamped <c>.bak.*</c>
///   file next to the override; the writer keeps the last 10 and deletes older ones.</item>
///   <item>Process-local serialization via a named <see cref="Mutex"/> so a save can't
///   race a concurrent restart-marker clear in the same process.</item>
///   <item>ETag computation per top-level section (SHA256 of canonicalized JSON), so the
///   <c>AdminSettingsController</c> can implement optimistic concurrency.</item>
///   <item>Restart-marker management: <c>__meta.restartRequiredFor</c> tracks which
///   sections have unactivated changes since the last service start. Cleared via
///   <see cref="ClearRestartMarker"/> when <c>IHostApplicationLifetime.ApplicationStarted</c>
///   fires.</item>
/// </list>
///
/// <para>Cross-host concurrency (HA cluster on a UNC share) is NOT solved here — that
/// belongs to the controller's ETag/If-Match check. The mutex is only intra-process.</para>
/// </summary>
public sealed class RuntimeOverridesWriter
{
    public const string MetaSectionKey = "__meta";
    public const string MetaRestartRequiredSinceKey = "restartRequiredSince";
    public const string MetaRestartRequiredForKey = "restartRequiredFor";

    private const int MaxBackupsToKeep = 10;
    private const int MutexAcquireTimeoutMs = 30_000;

    private readonly string _path;
    private readonly ILogger<RuntimeOverridesWriter> _log;
    private readonly string _mutexName;

    public RuntimeOverridesWriter(string path, ILogger<RuntimeOverridesWriter> log)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Override path must not be empty.", nameof(path));
        _path = Path.GetFullPath(path);
        _log = log;
        // Mutex name must be path-derived so multiple override files (e.g. test isolation)
        // don't serialize against each other. Limited to 260 chars by Windows; SHA256 hex
        // (64 chars) plus prefix stays well under that.
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToLowerInvariant())));
        _mutexName = $"Global\\NodePilot.RuntimeOverrides.{pathHash}";
    }

    public string OverridesPath => _path;

    /// <summary>
    /// Read the current file as a JSON object. Returns an empty object when the file
    /// does not exist (first-time scenario) or contains an empty document.
    /// </summary>
    public JsonObject ReadOrEmpty()
    {
        if (!File.Exists(_path)) return new JsonObject();
        var raw = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(raw)) return new JsonObject();
        var parsed = JsonNode.Parse(raw) as JsonObject
            ?? throw new InvalidOperationException(
                $"Runtime overrides file '{_path}' must contain a JSON object at the root.");
        return parsed;
    }

    /// <summary>
    /// Read-mutate-write under the process-local lock. The mutator may freely modify the
    /// root object (which is a fresh in-memory copy of what's on disk); the writer
    /// serializes + atomically replaces the file and rotates backups.
    /// </summary>
    public void MutateAndWrite(Action<JsonObject> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        using var mutex = AcquireMutex();
        try
        {
            EnsureDirectoryExists();
            var root = ReadOrEmpty();
            mutator(root);
            WriteAtomic(root);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* not held */ }
        }
    }

    /// <summary>
    /// Compute the ETag for a given top-level section (or the empty-object ETag if the
    /// section is missing). Stable across key reordering — keys are canonicalized
    /// alphabetically before hashing.
    /// </summary>
    public string ComputeSectionEtag(string sectionPath)
    {
        var root = ReadOrEmpty();
        var section = NavigateSection(root, sectionPath);
        return ComputeEtag(section);
    }

    public static string ComputeEtag(JsonNode? node)
    {
        var canonical = CanonicalizeJson(node);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }

    /// <summary>
    /// Mark one or more sections as needing a service restart to take effect. Idempotent;
    /// the meta-block accumulates section names until a restart clears it.
    /// </summary>
    public void MarkRestartRequired(IEnumerable<string> sections, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var sectionList = sections.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (sectionList.Count == 0) return;

        MutateAndWrite(root =>
        {
            if (root[MetaSectionKey] is not JsonObject meta)
            {
                meta = new JsonObject();
                root[MetaSectionKey] = meta;
            }
            // Preserve oldest restartRequiredSince — the banner reflects how long the
            // operator has been overdue on a restart, not the most recent edit.
            if (meta[MetaRestartRequiredSinceKey] is null)
                meta[MetaRestartRequiredSinceKey] = now.UtcDateTime.ToString("O");

            var existing = (meta[MetaRestartRequiredForKey] as JsonArray) ?? new JsonArray();
            var combined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in existing) if (n is JsonValue v && v.TryGetValue(out string? s) && s is not null) combined.Add(s);
            foreach (var s in sectionList) combined.Add(s);
            var ordered = new JsonArray();
            foreach (var s in combined.OrderBy(x => x, StringComparer.Ordinal)) ordered.Add(s);
            meta[MetaRestartRequiredForKey] = ordered;
        });
    }

    /// <summary>
    /// Result of an atomic section-update attempt. <see cref="Success"/> is true exactly when
    /// the ETag matched and the file was written; on a mismatch, <see cref="CurrentSection"/>
    /// carries the up-to-date server snapshot so the caller can return it to the client as a
    /// 412 body without re-reading the file (which would itself be racy).
    /// </summary>
    public sealed record AtomicSectionUpdateResult(
        bool Success,
        string CurrentEtag,
        JsonObject? CurrentSection,
        JsonObject? PersistedSection);

    /// <summary>
    /// Atomic check-then-write under the process-local mutex. This is the only correct
    /// way to serialise a section save: the ETag comparison, the JSON mutation, the
    /// last-save metadata + restart-marker bookkeeping, and the file replace ALL happen
    /// inside the same critical section. Without this, two concurrent PUTs with the
    /// same ETag would both pass the pre-flight check and the second write would
    /// silently clobber the first — which is exactly the HA/UNC race the design called
    /// out as the central guarantee.
    ///
    /// <para>On ETag mismatch the file is NOT written and <see cref="AtomicSectionUpdateResult.CurrentSection"/>
    /// carries the on-disk snapshot the caller should surface to the client.</para>
    /// </summary>
    public AtomicSectionUpdateResult TryUpdateSectionAtomic(
        string sectionPath,
        string expectedEtag,
        JsonObject newSection,
        IEnumerable<string>? restartRequiredFor,
        string savedBy,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(sectionPath)) throw new ArgumentException("Section path must not be empty.", nameof(sectionPath));
        ArgumentNullException.ThrowIfNull(newSection);

        using var mutex = AcquireMutex();
        try
        {
            EnsureDirectoryExists();
            var root = ReadOrEmpty();
            var currentSection = NavigateSection(root, sectionPath);
            var currentEtag = ComputeEtag(currentSection);

            if (!string.Equals(currentEtag, expectedEtag, StringComparison.Ordinal))
            {
                return new AtomicSectionUpdateResult(
                    Success: false,
                    CurrentEtag: currentEtag,
                    CurrentSection: currentSection as JsonObject ?? new JsonObject(),
                    PersistedSection: null);
            }

            // Apply the section override, accumulate restart-marker metadata, and write
            // last-save bookkeeping — all in the same root document, all under the same lock.
            ApplySectionInternal(root, sectionPath, newSection);

            var meta = EnsureMeta(root);
            meta["lastSavedAt"] = now.UtcDateTime.ToString("O");
            meta["lastSavedBy"] = savedBy;

            if (restartRequiredFor is not null)
            {
                var sections = restartRequiredFor.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
                if (sections.Count > 0)
                {
                    if (meta[MetaRestartRequiredSinceKey] is null)
                        meta[MetaRestartRequiredSinceKey] = now.UtcDateTime.ToString("O");

                    var existing = (meta[MetaRestartRequiredForKey] as JsonArray) ?? new JsonArray();
                    var combined = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var n in existing) if (n is JsonValue v && v.TryGetValue(out string? s) && s is not null) combined.Add(s);
                    foreach (var s in sections) combined.Add(s);
                    var ordered = new JsonArray();
                    foreach (var s in combined.OrderBy(x => x, StringComparer.Ordinal)) ordered.Add(s);
                    meta[MetaRestartRequiredForKey] = ordered;
                }
            }

            WriteAtomic(root);

            // Re-read the section we just wrote so the caller sees the canonical persisted
            // shape (post-merge, with any inherited keys) rather than just the override slice.
            var persistedSection = NavigateSection(ReadOrEmpty(), sectionPath) as JsonObject ?? newSection;
            var newEtag = ComputeEtag(persistedSection);

            return new AtomicSectionUpdateResult(
                Success: true,
                CurrentEtag: newEtag,
                CurrentSection: null,
                PersistedSection: persistedSection);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { /* not held */ }
        }
    }

    private static void ApplySectionInternal(JsonObject root, string sectionPath, JsonObject newSection)
    {
        var parts = sectionPath.Split(':', StringSplitOptions.RemoveEmptyEntries);
        JsonObject parent = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parent[parts[i]] is not JsonObject next)
            {
                next = new JsonObject();
                parent[parts[i]] = next;
            }
            parent = next;
        }
        parent[parts[^1]] = newSection;
    }

    private static JsonObject EnsureMeta(JsonObject root)
    {
        if (root[MetaSectionKey] is not JsonObject meta)
        {
            meta = new JsonObject();
            root[MetaSectionKey] = meta;
        }
        return meta;
    }

    /// <summary>
    /// Wipe the restart-marker meta-fields. Called from
    /// <c>IHostApplicationLifetime.ApplicationStarted</c> so the banner only disappears
    /// after the host is genuinely up — not just after <c>app.Build()</c>, which would
    /// also clear it on a process that crashes between Build and Run.
    /// </summary>
    public void ClearRestartMarker()
    {
        if (!File.Exists(_path)) return;
        // Avoid a write (and a backup) when there's no marker to clear — that's the
        // common path on every restart and noisy backup churn would obscure real edits.
        var current = ReadOrEmpty();
        if (current[MetaSectionKey] is not JsonObject meta) return;
        if (meta[MetaRestartRequiredSinceKey] is null && meta[MetaRestartRequiredForKey] is null) return;

        MutateAndWrite(root =>
        {
            if (root[MetaSectionKey] is not JsonObject m) return;
            m.Remove(MetaRestartRequiredSinceKey);
            m.Remove(MetaRestartRequiredForKey);
            // Drop the meta-block entirely if it's now empty so the file stays clean.
            if (m.Count == 0) root.Remove(MetaSectionKey);
        });
    }

    public RuntimeOverridesStatus ReadStatus()
    {
        if (!File.Exists(_path))
            return new RuntimeOverridesStatus(_path, false, null, Array.Empty<string>(), null, null);

        var root = ReadOrEmpty();
        var meta = root[MetaSectionKey] as JsonObject;
        DateTimeOffset? since = null;
        var sections = Array.Empty<string>();
        DateTimeOffset? lastSavedAt = null;
        string? lastSavedBy = null;

        if (meta is not null)
        {
            if (meta[MetaRestartRequiredSinceKey] is JsonValue sv && sv.TryGetValue(out string? s)
                && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                since = parsed;

            if (meta[MetaRestartRequiredForKey] is JsonArray arr)
                sections = arr.Where(n => n is JsonValue).Select(n => n!.GetValue<string>()).ToArray();

            if (meta["lastSavedAt"] is JsonValue lsa && lsa.TryGetValue(out string? lsaStr)
                && DateTimeOffset.TryParse(lsaStr, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var lsaParsed))
                lastSavedAt = lsaParsed;

            if (meta["lastSavedBy"] is JsonValue lsb && lsb.TryGetValue(out string? lsbStr))
                lastSavedBy = lsbStr;
        }

        return new RuntimeOverridesStatus(
            OverridesPath: _path,
            RestartRequired: since is not null || sections.Length > 0,
            RestartRequiredSince: since,
            RestartRequiredFor: sections,
            LastSavedAt: lastSavedAt,
            LastSavedBy: lastSavedBy);
    }

    /// <summary>
    /// Write a transient "lastSavedAt"/"lastSavedBy" pair into the meta-block. Called by
    /// the controller after a successful section save so the status endpoint can surface
    /// who-saved-when without needing to peek at the audit log.
    /// </summary>
    public void RecordLastSave(string username, DateTimeOffset now)
    {
        MutateAndWrite(root =>
        {
            if (root[MetaSectionKey] is not JsonObject meta)
            {
                meta = new JsonObject();
                root[MetaSectionKey] = meta;
            }
            meta["lastSavedAt"] = now.UtcDateTime.ToString("O");
            meta["lastSavedBy"] = username;
        });
    }

    private Mutex AcquireMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, _mutexName);
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(MutexAcquireTimeoutMs);
        }
        catch (AbandonedMutexException)
        {
            // Previous holder crashed — we now own the mutex, file may need integrity check
            // but our atomic-replace pattern means it's either intact-old or intact-new.
            acquired = true;
            _log.LogWarning(
                "Runtime overrides mutex was abandoned by a previous holder. File state should be intact due to atomic-replace, but check {Path} if subsequent reads look stale.",
                _path);
        }
        if (!acquired)
        {
            mutex.Dispose();
            throw new TimeoutException(
                $"Could not acquire runtime overrides mutex '{_mutexName}' within {MutexAcquireTimeoutMs} ms — another process or thread is holding it. Path: {_path}");
        }
        return mutex;
    }

    private void EnsureDirectoryExists()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private void WriteAtomic(JsonObject root)
    {
        var tmp = _path + ".tmp";
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!File.Exists(_path))
        {
            // First-time write — no Replace, just Move. No backup needed because there
            // was no prior file to back up.
            File.Move(tmp, _path);
            return;
        }

        var backupName = _path + ".bak." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");

        try
        {
            // Primary path: NTFS-local atomic File.Replace + same-call backup creation.
            File.Replace(tmp, _path, backupName);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // UNC / SMB / non-NTFS fallback: explicit copy-as-backup, then atomic move.
            // Not as bullet-proof as File.Replace under crash, but acceptable when the
            // file lives on a shared drive (HA cluster scenario). Cross-host write race
            // is the controller's ETag/If-Match concern, not ours.
            _log.LogDebug(ex,
                "File.Replace failed for {Path}, falling back to copy+move (likely UNC/non-NTFS).", _path);
            File.Copy(_path, backupName, overwrite: false);
            File.Move(tmp, _path, overwrite: true);
        }

        RotateBackups();
    }

    private void RotateBackups()
    {
        var dir = Path.GetDirectoryName(_path);
        var basename = Path.GetFileName(_path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(basename)) return;

        var pattern = basename + ".bak.*";
        IEnumerable<string> backups;
        try
        {
            backups = Directory.EnumerateFiles(dir, pattern);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        var sorted = backups
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        foreach (var stale in sorted.Skip(MaxBackupsToKeep))
        {
            try { stale.Delete(); }
            catch (IOException ex) { _log.LogDebug(ex, "Could not rotate stale backup {File}.", stale.FullName); }
            catch (UnauthorizedAccessException ex) { _log.LogDebug(ex, "Could not rotate stale backup {File}.", stale.FullName); }
        }
    }

    internal static JsonNode? NavigateSection(JsonObject root, string sectionPath)
    {
        if (string.IsNullOrEmpty(sectionPath)) return root;
        var parts = sectionPath.Split(':', StringSplitOptions.RemoveEmptyEntries);
        JsonNode? current = root;
        foreach (var p in parts)
        {
            if (current is not JsonObject obj || obj[p] is null) return null;
            current = obj[p];
        }
        return current;
    }

    /// <summary>
    /// Canonicalize a JSON node so semantically identical sections produce the same hash.
    /// Object keys are sorted ordinally; arrays preserve order (semantically meaningful);
    /// numbers and strings round-trip via JsonSerializer to normalize whitespace.
    /// </summary>
    internal static string CanonicalizeJson(JsonNode? node)
    {
        if (node is null) return "null";
        return node switch
        {
            JsonObject obj => CanonicalizeObject(obj),
            JsonArray arr => CanonicalizeArray(arr),
            JsonValue val => val.ToJsonString(),
            _ => node.ToJsonString()
        };
    }

    private static string CanonicalizeObject(JsonObject obj)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(JsonSerializer.Serialize(key));
            sb.Append(':');
            sb.Append(CanonicalizeJson(obj[key]));
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string CanonicalizeArray(JsonArray arr)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < arr.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(CanonicalizeJson(arr[i]));
        }
        sb.Append(']');
        return sb.ToString();
    }
}

/// <summary>
/// Snapshot of the runtime-overrides state, returned by
/// <c>GET /api/admin/settings/status</c>.
/// </summary>
public sealed record RuntimeOverridesStatus(
    string OverridesPath,
    bool RestartRequired,
    DateTimeOffset? RestartRequiredSince,
    IReadOnlyList<string> RestartRequiredFor,
    DateTimeOffset? LastSavedAt,
    string? LastSavedBy);

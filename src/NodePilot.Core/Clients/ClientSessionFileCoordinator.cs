using System.Security.Cryptography;
using System.Text;

namespace NodePilot.Core.Clients;

/// <summary>
/// Coordinates the DPAPI session file shared by the CLI and MCP processes. Refresh takes an
/// origin-bound cross-process lock because the API rotates a bearer token exactly once; file
/// mutations take a shorter path-bound lock and replace the file atomically in place, so a
/// reader sees either the old or the new complete blob, never a truncated one. Lock files stay
/// on disk by design: ownership is the open <see cref="FileStream"/> with
/// <see cref="FileShare.None"/>, a handle Windows releases when the process exits.
/// </summary>
public static class ClientSessionFileCoordinator
{
    private const int RetryDelayMilliseconds = 15;
    private const int IoRetryCount = 100;

    /// <summary>
    /// Acquires the refresh lease shared by every process using the same canonical session file
    /// and server origin. Waiting is cancellable and avoids the thread affinity of holding a
    /// named <see cref="Mutex"/> across asynchronous HTTP work.
    /// </summary>
    public static Task<IDisposable> AcquireRefreshLockAsync(
        string sessionPath,
        string server,
        CancellationToken cancellationToken)
        => AcquireAsync(RefreshLockPath(sessionPath, server), cancellationToken);

    /// <summary>Serializes short Save/Delete mutations across CLI and MCP processes.</summary>
    public static IDisposable AcquireMutationLock(
        string sessionPath,
        CancellationToken cancellationToken = default)
        => Acquire(MutationLockPath(sessionPath), cancellationToken);

    /// <summary>Reads a complete generation, retrying transient sharing violations.</summary>
    public static byte[]? ReadAllBytesIfExists(string path)
    {
        var canonicalPath = CanonicalPath(path);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.Exists(canonicalPath) ? File.ReadAllBytes(canonicalPath) : null;
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < IoRetryCount)
            {
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }

    /// <summary>
    /// Writes a unique file beside the destination, flushes it, then replaces or moves it on the
    /// same volume. The caller should hold <see cref="AcquireMutationLock"/>.
    /// </summary>
    public static void WriteAllBytesAtomically(string path, ReadOnlySpan<byte> contents)
    {
        var canonicalPath = CanonicalPath(path);
        var directory = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidOperationException("Session file must have a parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(canonicalPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var temp = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                temp.Write(contents);
                temp.Flush(flushToDisk: true);
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(canonicalPath))
                        File.Replace(tempPath, canonicalPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    else
                        File.Move(tempPath, canonicalPath);
                    break;
                }
                catch (IOException) when (attempt < IoRetryCount)
                {
                    // A reader may still hold the previous generation without FileShare.Delete,
                    // or the destination may have appeared or disappeared between Exists and
                    // Move/Replace. Retry with the same complete temp file on the same volume.
                    Thread.Sleep(RetryDelayMilliseconds);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
                // The destination was never partially exposed. An orphaned temp file is harmless
                // and its unique name keeps it from blocking a later session save.
            }
        }
    }

    /// <summary>Deletes the complete session generation. Caller holds the mutation lock.</summary>
    public static void DeleteIfExists(string path)
    {
        var canonicalPath = CanonicalPath(path);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(canonicalPath)) File.Delete(canonicalPath);
                return;
            }
            catch (IOException) when (attempt < IoRetryCount)
            {
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }

    private static async Task<IDisposable> AcquireAsync(string lockPath, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return OpenLockFile(lockPath);
            }
            catch (IOException)
            {
                await Task.Delay(RetryDelayMilliseconds, ct).ConfigureAwait(false);
            }
        }
    }

    private static IDisposable Acquire(string lockPath, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return OpenLockFile(lockPath);
            }
            catch (IOException)
            {
                if (ct.WaitHandle.WaitOne(RetryDelayMilliseconds))
                    ct.ThrowIfCancellationRequested();
            }
        }
    }

    private static FileStream OpenLockFile(string lockPath)
    {
        var directory = Path.GetDirectoryName(lockPath)
            ?? throw new InvalidOperationException("Session lock file must have a parent directory.");
        Directory.CreateDirectory(directory);
        return new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);
    }

    private static string RefreshLockPath(string sessionPath, string server)
    {
        var canonicalPath = CanonicalPath(sessionPath);
        var originHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalOrigin(server)))[..12]);
        return $"{canonicalPath}.{originHash}.refresh.lock";
    }

    private static string MutationLockPath(string sessionPath)
        => $"{CanonicalPath(sessionPath)}.mutation.lock";

    private static string CanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private static string CanonicalOrigin(string server)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server);
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            throw new ArgumentException("Server must be an absolute URI.", nameof(server));
        }

        return $"{uri.Scheme.ToLowerInvariant()}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}";
    }
}

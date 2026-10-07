using System.Security.Cryptography;
using System.Text;
using NodePilot.Core.Agents;

namespace NodePilot.Engine.Agents;

public sealed class AgentArtifactStore : IAsyncDisposable
{
    public const int TransferBlockBytes = 256 * 1024;
    private readonly string _root;
    private readonly Dictionary<string, Artifact> _artifacts = new(StringComparer.Ordinal);
    private long _bytes;
    private readonly object _sync = new();
    public IReadOnlyCollection<Artifact> Artifacts { get { lock (_sync) return _artifacts.Values.ToArray(); } }
    public string Root => _root;
    // Keep writable storage outside the installation, but isolate installations and test hosts.
    public static string BaseDirectory => GetBaseDirectory(AppContext.BaseDirectory);

    internal static string GetBaseDirectory(string applicationDirectory)
    {
        var installation = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory)).ToUpperInvariant();
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installation)))[..24];
        return Path.Combine(Path.GetTempPath(), "NodePilot", "agent-work", scope);
    }

    public AgentArtifactStore(Guid runId, string? baseDirectory = null)
    {
        _root = Path.Combine(baseDirectory ?? BaseDirectory, runId.ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public async Task<Artifact> CollectAsync(string source, long length,
        Func<long, int, CancellationToken, Task<byte[]>> read, CancellationToken ct,
        Func<CancellationToken, Task<byte[]>>? verifySourceHash = null)
    {
        lock (_sync)
        {
            if (length < 0 || length > AgentOptions.MaxCollectedBytes - _bytes)
                throw new AgentBudgetExceededException("Collected logs exceed the shared 250 MB limit.");
            _bytes += length;
        }
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_root, id + ".log");
        try
        {
            using var hash = verifySourceHash is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, TransferBlockBytes, true))
            {
                long offset = 0;
                while (offset < length)
                {
                    ct.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(TransferBlockBytes, length - offset);
                    var block = await read(offset, count, ct);
                    if (block.Length == 0 || block.Length > count)
                        throw new IOException("Source changed or returned an invalid transfer block. Collect the file again.");
                    await file.WriteAsync(block, ct);
                    hash?.AppendData(block);
                    offset += block.Length;
                }
            }
            if (verifySourceHash is not null)
            {
                var expected = await verifySourceHash(ct);
                if (!hash!.GetHashAndReset().AsSpan().SequenceEqual(expected))
                    throw new IOException("Source changed during collection. No artifact was kept; collect the file again.");
            }
            var artifact = new Artifact(id, source, length, path);
            lock (_sync) _artifacts.Add(id, artifact);
            return artifact;
        }
        catch { lock (_sync) _bytes -= length; File.Delete(path); throw; }
    }

    public async Task<string> SearchAsync(string id, string query, int maxMatches, CancellationToken ct)
    {
        Artifact artifact;
        lock (_sync)
            artifact = _artifacts.GetValueOrDefault(id) ?? throw new ArgumentException("Unknown collected file.");
        if (query.Length is < 1 or > 1024) throw new ArgumentException("Search text must contain 1–1024 characters.");
        maxMatches = Math.Clamp(maxMatches, 1, 100);
        using var reader = new StreamReader(artifact.LocalPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192);
        var output = new StringBuilder();
        var fragment = new StringBuilder();
        var buffer = new char[8192];
        var line = 1L;
        var matches = 0;
        var matchedLine = -1L;
        var fragmentStart = 0L;
        void Inspect(bool lineEnd)
        {
            var text = fragment.ToString();
            var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || matchedLine == line) return;
            matchedLine = line;
            matches++;
            var start = text.Length <= 1200 ? 0 : Math.Max(0, index - 160);
            var snippet = text.Substring(start, Math.Min(1200, text.Length - start)).TrimEnd('\r');
            output.AppendLine($"{artifact.Source}:{line}: {(fragmentStart + start > 0 ? "[earlier characters omitted] " : "")}{snippet}{(start + 1200 < text.Length || !lineEnd ? " [later characters omitted]" : "")}");
        }
        while (matches < maxMatches && output.Length < 14_000)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) { Inspect(true); break; }
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n') { Inspect(true); fragment.Clear(); line++; fragmentStart = 0; }
                else fragment.Append(buffer[i]);
                if (fragment.Length >= 8192)
                {
                    Inspect(false);
                    var remove = fragment.Length - query.Length;
                    fragment.Remove(0, remove);
                    fragmentStart += remove;
                }
                if (matches >= maxMatches || output.Length >= 14_000) break;
            }
        }
        return output.Length == 0 ? "No matching text found." : output.ToString();
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return ValueTask.CompletedTask;
    }

    public sealed record Artifact(string Id, string Source, long Length, string LocalPath);
}

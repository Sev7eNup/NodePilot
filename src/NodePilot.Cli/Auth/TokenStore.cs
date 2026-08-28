using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodePilot.Cli.Settings;

namespace NodePilot.Cli.Auth;

/// <summary>
/// DPAPI-encrypted session store. One file per profile so multiple connections can
/// be authenticated in parallel (`np --profile prod auth login` next to `--profile dev`).
/// File path: <c>%APPDATA%\NodePilot\session-&lt;profile&gt;.dat</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _baseDir;

    public TokenStore() : this(ConfigStore.DefaultConfigDir()) { }

    public TokenStore(string baseDir)
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(_baseDir);
    }

    public string PathFor(string profile) => Path.Combine(_baseDir, $"session-{Sanitize(profile)}.dat");

    public StoredSession? Load(string profile)
    {
        var path = PathFor(profile);
        using var mutation = NodePilot.Core.Clients.ClientSessionFileCoordinator.AcquireMutationLock(path);
        return LoadPath(path);
    }

    private static StoredSession? LoadPath(string path)
    {
        try
        {
            var encrypted = NodePilot.Core.Clients.ClientSessionFileCoordinator.ReadAllBytesIfExists(path);
            if (encrypted is null) return null;
            var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: Entropy, scope: DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredSession>(plain, JsonOptions);
        }
        catch (CryptographicException)
        {
            // File present but undecryptable (different user, machine reinstall, etc.) — treat as
            // no session.
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string profile, StoredSession session)
    {
        var path = PathFor(profile);
        using var mutation = NodePilot.Core.Clients.ClientSessionFileCoordinator.AcquireMutationLock(path);
        Write(path, session);
    }

    public void Delete(string profile)
    {
        var path = PathFor(profile);
        using var mutation = NodePilot.Core.Clients.ClientSessionFileCoordinator.AcquireMutationLock(path);
        NodePilot.Core.Clients.ClientSessionFileCoordinator.DeleteIfExists(path);
    }

    /// <summary>
    /// Persists a rotation only while the session generation that was presented to the API is
    /// still current. This prevents a refresh response from resurrecting a concurrent logout or
    /// overwriting a newer login performed while the HTTP request was in flight.
    /// </summary>
    internal bool TrySaveIfCurrent(string profile, string expectedToken, StoredSession session)
    {
        var path = PathFor(profile);
        using var mutation = NodePilot.Core.Clients.ClientSessionFileCoordinator.AcquireMutationLock(path);
        var current = LoadPath(path);
        if (current is null || !string.Equals(current.Token, expectedToken, StringComparison.Ordinal))
            return false;

        Write(path, session);
        return true;
    }

    internal bool DeleteIfCurrent(string profile, string expectedToken)
    {
        var path = PathFor(profile);
        using var mutation = NodePilot.Core.Clients.ClientSessionFileCoordinator.AcquireMutationLock(path);
        var current = LoadPath(path);
        if (current is null || !string.Equals(current.Token, expectedToken, StringComparison.Ordinal))
            return false;

        NodePilot.Core.Clients.ClientSessionFileCoordinator.DeleteIfExists(path);
        return true;
    }

    private static void Write(string path, StoredSession session)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var encrypted = ProtectedData.Protect(
            plain, optionalEntropy: Entropy, scope: DataProtectionScope.CurrentUser);
        NodePilot.Core.Clients.ClientSessionFileCoordinator.WriteAllBytesAtomically(path, encrypted);
    }

    // Constant entropy distinguishes this blob from anything else the same user has
    // DPAPI-encrypted,
    // so a stolen session file cannot be Unprotected by a sibling app on the same machine.
    // Shared with the MCP server via Core — both read/write the same session blob.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(NodePilot.Core.Clients.ClientSessionSecurity.DpapiSessionEntropy);

    private static string Sanitize(string profile)
    {
        var sanitized = new StringBuilder(profile.Length);
        foreach (var c in profile)
            sanitized.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sanitized.Length == 0 ? "default" : sanitized.ToString();
    }
}

public sealed class StoredSession
{
    public string Server { get; set; } = "";
    public string Token { get; set; } = "";
    public string Username { get; set; } = "";
    public Guid UserId { get; set; }
    public string Role { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NodePilot.Core.Clients;

/// <summary>
/// DPAPI-encrypted session store shared by the <c>np</c> CLI and the <c>nodepilot-mcp</c> server:
/// the operator authenticates once with <c>np auth login</c> and both clients read the same file.
/// One file per profile so multiple connections can be authenticated in parallel
/// (<c>np --profile prod auth login</c> next to <c>--profile dev</c>).
/// File path: <c>%APPDATA%\NodePilot\session-&lt;profile&gt;.dat</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _baseDir;

    public TokenStore() : this(ClientConfigStore.DefaultConfigDir()) { }

    public TokenStore(string baseDir)
    {
        _baseDir = baseDir;
        Directory.CreateDirectory(_baseDir);
    }

    public string PathFor(string profile) => Path.Combine(_baseDir, $"session-{Sanitize(profile)}.dat");

    public StoredSession? Load(string profile)
    {
        var path = PathFor(profile);
        using var mutation = ClientSessionFileCoordinator.AcquireMutationLock(path);
        return LoadPath(path);
    }

    private static StoredSession? LoadPath(string path)
    {
        try
        {
            var encrypted = ClientSessionFileCoordinator.ReadAllBytesIfExists(path);
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
        using var mutation = ClientSessionFileCoordinator.AcquireMutationLock(path);
        Write(path, session);
    }

    public void Delete(string profile)
    {
        var path = PathFor(profile);
        using var mutation = ClientSessionFileCoordinator.AcquireMutationLock(path);
        ClientSessionFileCoordinator.DeleteIfExists(path);
    }

    /// <summary>
    /// Persists a rotation only while the session generation that was presented to the API is
    /// still current. This prevents a refresh response from resurrecting a concurrent logout or
    /// overwriting a newer login performed while the HTTP request was in flight.
    /// </summary>
    public bool TrySaveIfCurrent(string profile, string expectedToken, StoredSession session)
    {
        var path = PathFor(profile);
        using var mutation = ClientSessionFileCoordinator.AcquireMutationLock(path);
        var current = LoadPath(path);
        if (current is null || !string.Equals(current.Token, expectedToken, StringComparison.Ordinal))
            return false;

        Write(path, session);
        return true;
    }

    public bool DeleteIfCurrent(string profile, string expectedToken)
    {
        var path = PathFor(profile);
        using var mutation = ClientSessionFileCoordinator.AcquireMutationLock(path);
        var current = LoadPath(path);
        if (current is null || !string.Equals(current.Token, expectedToken, StringComparison.Ordinal))
            return false;

        ClientSessionFileCoordinator.DeleteIfExists(path);
        return true;
    }

    private static void Write(string path, StoredSession session)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var encrypted = ProtectedData.Protect(
            plain, optionalEntropy: Entropy, scope: DataProtectionScope.CurrentUser);
        ClientSessionFileCoordinator.WriteAllBytesAtomically(path, encrypted);
    }

    // Constant entropy distinguishes this blob from anything else the same user has
    // DPAPI-encrypted, so a stolen session file cannot be Unprotected by a sibling app on the
    // same machine.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(ClientSessionSecurity.DpapiSessionEntropy);

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

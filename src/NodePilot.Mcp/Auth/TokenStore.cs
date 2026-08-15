using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodePilot.Core.Clients;

namespace NodePilot.Mcp.Auth;

/// <summary>
/// DPAPI-encrypted session store — reads the SAME files the <c>np</c> CLI writes, so the
/// operator authenticates once via <c>np auth login</c> and the MCP server reuses it.
/// File path: <c>%APPDATA%\NodePilot\session-&lt;profile&gt;.dat</c>.
/// Entropy matches the CLI (<c>NodePilot.Cli/v1</c>) so the same blob round-trips.
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
    internal bool TrySaveIfCurrent(string profile, string expectedToken, StoredSession session)
    {
        var path = PathFor(profile);
        using var mutation = ClientSessionFileCoordinator.AcquireMutationLock(path);
        var current = LoadPath(path);
        if (current is null || !string.Equals(current.Token, expectedToken, StringComparison.Ordinal))
            return false;

        Write(path, session);
        return true;
    }

    internal bool DeleteIfCurrent(string profile, string expectedToken)
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

    // Must match the CLI's entropy so a session written by `np auth login` is readable here.
    // Shared constant in Core — no more hand-kept sync between the two executables.
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

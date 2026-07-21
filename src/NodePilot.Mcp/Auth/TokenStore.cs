using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NodePilot.Mcp.Config;

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
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(path);
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
        var plain = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var encrypted = ProtectedData.Protect(plain, optionalEntropy: Entropy, scope: DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(profile), encrypted);
    }

    // Must match the CLI's entropy so a session written by `np auth login` is readable here.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NodePilot.Cli/v1");

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
    public DateTime ExpiresAt { get; set; }

    public bool IsExpired(TimeSpan? skew = null)
        => DateTime.UtcNow >= ExpiresAt - (skew ?? TimeSpan.FromMinutes(1));
}

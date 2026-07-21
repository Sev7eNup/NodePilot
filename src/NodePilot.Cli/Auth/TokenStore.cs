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
        if (!File.Exists(path)) return null;
        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plain = ProtectedData.Unprotect(encrypted, optionalEntropy: Entropy, scope: DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredSession>(plain, JsonOptions);
        }
        catch (CryptographicException)
        {
            // File present but undecryptable (different user, machine reinstall, etc.) — treat as no session.
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
        var path = PathFor(profile);
        File.WriteAllBytes(path, encrypted);
    }

    public void Delete(string profile)
    {
        var path = PathFor(profile);
        if (File.Exists(path)) File.Delete(path);
    }

    // Constant entropy distinguishes our blob from anything else the same user has DPAPI-encrypted,
    // so a stolen session file cannot be Unprotected by a sibling app on the same machine.
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

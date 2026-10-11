using System.Security.Cryptography;
using System.Text;

namespace NodePilot.Api.Security;

/// <summary>
/// Machine-local key for the Engine Switcher. The key file is readable only by the local
/// Administrators group, not by the service identity that created it; the service keeps just
/// a SHA-256 hash (<c>switcher.key.sha256</c>) to check presented keys against. Delete the key
/// file to revoke it; the next service start creates a new pair.
/// </summary>
public sealed class SwitcherKeyStore
{
    public const string FileName = "switcher.key";
    public const string HashSuffix = ".sha256";
    public const string Header = "X-Switcher-Key";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly bool _enabled;
    private readonly object _gate = new();
    private string? _cachedHash;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public SwitcherKeyStore(IHostEnvironment env, IConfiguration config)
    {
        _enabled = OperatingSystem.IsWindows()
            && config.GetValue("Security:SwitcherKey:Enabled", true);
        _path = ResolvePath(env, config);
    }

    public bool Enabled => _enabled;

    internal static string ResolvePath(IHostEnvironment env, IConfiguration? config)
    {
        var configured = config?["Security:SwitcherKeyPath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(env.ContentRootPath, configured);
        var tokenPath = AdminBootstrap.ResolveTokenPath(env, config);
        return Path.Combine(Path.GetDirectoryName(tokenPath) ?? env.ContentRootPath, FileName);
    }

    internal static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>
    /// Creates the key and its hash at startup. Both files are kept only while the key file and a
    /// secure hash file exist; otherwise the pair is replaced. Unlike the JWT key, nothing is
    /// signed with this key, so rotating it loses nothing. The key file is not opened here:
    /// the service identity cannot read it.
    /// </summary>
    public static void EnsureProvisioned(
        IHostEnvironment env, IConfiguration config, ILogger logger)
    {
        if (!OperatingSystem.IsWindows() || !config.GetValue("Security:SwitcherKey:Enabled", true))
            return;

        var path = ResolvePath(env, config);
        var hashPath = path + HashSuffix;
        try
        {
            if (File.Exists(path) && File.Exists(hashPath))
            {
                var read = RestrictedFileWriter.ReadValidatedText(hashPath);
                if (read.Security.IsSecure && !string.IsNullOrWhiteSpace(read.Content)) return;
                logger.LogWarning(
                    "Switcher key hash '{Path}' is not usable ({Reason}); creating a new key.",
                    hashPath, read.Security.Reason ?? "empty file");
            }

            File.Delete(path);
            File.Delete(hashPath);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            if (!RestrictedFileWriter.WriteText(hashPath, Hash(key), failClosed: false)
                || !RestrictedFileWriter.WriteText(path, key, failClosed: false, administratorsOnly: true))
            {
                File.Delete(path);
                File.Delete(hashPath);
                logger.LogWarning(
                    "Switcher key '{Path}' could not be written with restrictive ACLs; " +
                    "the Switcher falls back to its normal sign-in.", path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Switcher key '{Path}' could not be provisioned: {Message}", path, ex.Message);
        }
    }

    /// <summary>
    /// Compares the hash of the presented key against the stored hash. Both files are re-checked
    /// every few seconds, so deleting the key file revokes it without a restart.
    /// </summary>
    public bool Validate(string? presented)
    {
        if (!_enabled || string.IsNullOrEmpty(presented)) return false;
        var expected = LoadHash();
        return !string.IsNullOrEmpty(expected)
            && SecretComparer.FixedTimeEquals(Hash(presented), expected);
    }

    private string? LoadHash()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _cachedAtUtc < CacheTtl) return _cachedHash;
            _cachedHash = null;
            try
            {
                var hashPath = _path + HashSuffix;
                if (File.Exists(_path) && File.Exists(hashPath))
                {
                    var read = RestrictedFileWriter.ReadValidatedText(hashPath);
                    if (read.Security.IsSecure) _cachedHash = read.Content?.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _cachedHash = null;
            }
            _cachedAtUtc = DateTime.UtcNow;
            return _cachedHash;
        }
    }
}

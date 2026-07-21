using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NodePilot.Api.Security;

/// <summary>
/// Resolves the JWT signing key from (1) config, then (2) a generated file under the content root.
/// Rejects keys that are known defaults, empty, or shorter than 32 UTF-8 bytes — a weak key lets
/// anyone forge tokens, so the process must fail fast rather than silently accept it.
/// </summary>
public static class JwtKeyResolver
{
    public const int MinKeyBytes = 32;
    public const string KeyFileName = "jwt-secret.key";

    // Historical default shipped in appsettings.json before 2026-04-18. Reject it explicitly so
    // upgraded deployments cannot keep using a world-known secret.
    private static readonly string[] BannedDefaults =
    {
        "NodePilot-Default-Secret-Key-Change-In-Production-32chars!",
    };

    public static string Resolve(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Jwt:Key"];
        string key;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            key = configured;
        }
        else
        {
            var keyFile = ResolveKeyFilePath(config, env);
            var dir = Path.GetDirectoryName(keyFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            if (!File.Exists(keyFile))
            {
                var parentSecurity = RestrictedFileWriter.ValidateParentDirectory(keyFile);
                if (!parentSecurity.IsSecure && !env.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        $"JWT signing-key parent security validation failed: {parentSecurity.Reason}. " +
                        "Use an NTFS/ReFS location whose directory chain cannot be modified by " +
                        "untrusted principals.");
                }

                // ACL-before-content: create the file empty with restricted permissions FIRST,
                // then write the secret. Closes a race condition (security-audit finding
                // H-3) where File.WriteAllText would otherwise leave the secret
                // world-readable for the few ms before TryRestrictToOwner finishes. The
                // helper deletes the file on ACL failure so a retry never re-uses a
                // partially-secured key.
                key = GenerateKey();
                RestrictedFileWriter.WriteText(keyFile, key, failClosed: true);
            }
            else
            {
                var read = RestrictedFileWriter.ReadValidatedText(keyFile);
                if (!read.Security.IsSecure
                    && read.Security.CanRotateSecurely
                    && config.GetValue<bool>("Jwt:RotateInsecureKeyFile"))
                {
                    RotateInsecureKeyFile(keyFile);
                    read = RestrictedFileWriter.ReadValidatedText(keyFile);
                }

                if (!read.Security.IsSecure && !env.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        $"JWT signing-key file security validation failed: {read.Security.Reason}. " +
                        "Move Jwt:KeyPath to an NTFS/ReFS location with owner-only ACLs, or set " +
                        "Jwt:RotateInsecureKeyFile=true for one startup to rotate an insecure key on " +
                        "an ACL-capable filesystem. Rotation immediately invalidates JWTs signed " +
                        "with the prior key; it does not delete server-side AuthSession rows.");
                }

                key = read.Content
                    ?? throw new InvalidOperationException(
                        "JWT signing-key file could not be read through its validated handle.");
            }
            key = key.Trim();
        }

        Validate(key);
        return key;
    }

    private static string GenerateKey()
    {
        var buf = new byte[48];
        System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
        try
        {
            return Convert.ToBase64String(buf);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buf);
        }
    }

    private static void RotateInsecureKeyFile(string keyFile)
    {
        // The replacement lives in the same directory so the final rename is atomic on the
        // supported filesystems. The old key is never copied to a backup and no secret content
        // is surfaced in an exception or log message.
        var replacement = keyFile + ".rotate-" + Guid.NewGuid().ToString("N");
        try
        {
            RestrictedFileWriter.WriteText(replacement, GenerateKey(), failClosed: true);
            File.Move(replacement, keyFile, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(replacement)) File.Delete(replacement);
            }
            catch
            {
                // Best effort only. The replacement itself is owner-only and contains a new
                // random key, so leaving it behind is safer than masking the primary failure.
            }
        }
    }

    // Jwt:KeyPath lets a production installer put the generated key under ProgramData while
    // the Install-Dir stays read-only for the service account. Relative values resolve against
    // ContentRoot so dev/test behaviour is unchanged when the key is absent.
    internal static string ResolveKeyFilePath(IConfiguration config, IHostEnvironment env)
    {
        var overridePath = config["Jwt:KeyPath"];
        if (string.IsNullOrWhiteSpace(overridePath))
            return Path.Combine(env.ContentRootPath, KeyFileName);
        return Path.IsPathRooted(overridePath)
            ? overridePath
            : Path.Combine(env.ContentRootPath, overridePath);
    }

    public static void Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Jwt:Key is empty. Configure Jwt:Key or let the app generate jwt-secret.key on first start.");

        if (BannedDefaults.Any(b => string.Equals(b, key, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Jwt:Key matches a known default value shipped with NodePilot. Remove it from configuration; " +
                "the app will generate a random key in jwt-secret.key automatically.");

        if (System.Text.Encoding.UTF8.GetByteCount(key) < MinKeyBytes)
            throw new InvalidOperationException(
                $"Jwt:Key must be at least {MinKeyBytes} bytes (UTF-8). Use a cryptographically random value.");
    }
}

/// <summary>
/// Singleton view of the resolved JWT signing key. Controllers and middleware inject
/// <see cref="IJwtKeyProvider"/> instead of re-running <see cref="JwtKeyResolver.Resolve"/>
/// per token mint — that path re-reads the file off disk every time and, more importantly,
/// fails late. Resolving once in the ctor makes misconfiguration a boot-time crash rather
/// than a runtime 500 on the first login request (security-audit finding M-2).
/// </summary>
public interface IJwtKeyProvider
{
    /// <summary>Validated signing key (never null/empty, guaranteed &gt;= MinKeyBytes).</summary>
    string Key { get; }
}

/// <summary>
/// Default <see cref="IJwtKeyProvider"/>. Resolves the key exactly once on construction —
/// register as a Singleton so the file-read cost is paid during host startup, not per
/// request. A misconfigured key throws here and the app fails to start.
/// </summary>
public sealed class JwtKeyProvider : IJwtKeyProvider
{
    public string Key { get; }

    public JwtKeyProvider(IConfiguration config, IHostEnvironment env)
    {
        Key = JwtKeyResolver.Resolve(config, env);
    }
}

/// <summary>
/// DI registration helper for <see cref="IJwtKeyProvider"/>. Program.cs must call
/// <c>builder.Services.AddJwtKeyProvider()</c> before anything that injects the interface
/// (AuthController, JwtBearer options wiring). Required because the Program.cs edit lives
/// in a separate change-set.
/// </summary>
public static class JwtKeyProviderExtensions
{
    public static IServiceCollection AddJwtKeyProvider(this IServiceCollection services)
        => services.AddSingleton<IJwtKeyProvider, JwtKeyProvider>();
}

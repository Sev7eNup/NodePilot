using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NodePilot.Api.Security;

/// <summary>
/// Guards first-user creation so that a bare-metal deployment cannot be hijacked by whoever
/// hits <c>POST /api/auth/login</c> first. On startup, when the Users table is empty, a
/// random token is written to <c>{ContentRoot}/admin-setup.token</c> and logged to the host
/// console. The first login call must present this token as the <c>X-Setup-Token</c> header;
/// after a successful admin creation the token file is deleted, so the bootstrap is a single
/// one-shot window visible only to whoever can read the server's logs/filesystem.
/// </summary>
public static class AdminBootstrap
{
    public const string TokenFileName = "admin-setup.token";
    public const string TokenHeader = "X-Setup-Token";

    // Security:AdminSetupTokenPath lets the production installer place this file in a
    // writable data directory (ProgramData) while the service install-dir stays read-only.
    // Absolute paths are used verbatim; relative values are resolved against ContentRoot.
    internal static string ResolveTokenPath(IHostEnvironment env, IConfiguration? config)
    {
        var overridePath = config?["Security:AdminSetupTokenPath"];
        if (string.IsNullOrWhiteSpace(overridePath))
            return Path.Combine(env.ContentRootPath, TokenFileName);
        return Path.IsPathRooted(overridePath)
            ? overridePath
            : Path.Combine(env.ContentRootPath, overridePath);
    }

    /// <summary>
    /// Create or refresh the token file when there are no users yet. Returns the path for
    /// logging. Returns null when no bootstrap is needed (users already exist, so there is
    /// no admin-creation branch to gate).
    /// </summary>
    public static string? EnsureBootstrapTokenIfNeeded(IHostEnvironment env, bool usersExist, ILogger logger, IConfiguration? config = null)
    {
        var path = ResolveTokenPath(env, config);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                logger.LogWarning("Could not create directory for bootstrap token '{Dir}': {Message}",
                    dir, ex.Message);
            }
        }
        if (usersExist)
        {
            // Users exist → the auto-admin branch is unreachable anyway. If a stale token
            // still lives on disk, remove it so a later accidental DB wipe cannot combine
            // with a leaked old token.
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { /* best-effort */ }
            }
            return null;
        }

        if (!File.Exists(path))
        {
            var buf = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(buf);
            var token = Convert.ToBase64String(buf);
            // ACL-before-content: empty file with owner-only ACL is created first, then the
            // token is written through the secured handle. Closes a race condition
            // (security-audit finding H-3) that File.WriteAllText + post-hoc
            // TryRestrictToOwner left open. Development remains best-effort, while every
            // other environment fails closed on an insecure file or parent chain.
            // intent — the token window is one-shot and short-lived. The writer still
            // deletes the partial file on failure, so we never end up with content + no ACL.
            if (!RestrictedFileWriter.WriteText(
                    path, token, failClosed: !env.IsDevelopment()))
            {
                logger.LogWarning(
                    "Bootstrap token at '{Path}' could not be written with restrictive ACLs. " +
                    "First-login bootstrap is disabled until the directory permission issue " +
                    "is resolved and the service is restarted.", path);
                return null;
            }
        }
        else
        {
            // A token may survive a restart. Never trust a pre-created/replaced file by path:
            // verify its complete parent chain and ACL while reading through the same locked
            // handle. Otherwise a writable deployment directory enables first-admin takeover.
            var read = RestrictedFileWriter.ReadValidatedText(path);
            if (!read.Security.IsSecure && !env.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"Admin bootstrap-token security validation failed: {read.Security.Reason}");
            }
        }

        logger.LogWarning(
            "Admin bootstrap is required. First POST /api/auth/login must include header {Header} " +
            "whose value matches the contents of {Path}. Delete the file after first login to disable bootstrap.",
            TokenHeader, path);
        return path;
    }

    /// <summary>
    /// Validates the presented token against the on-disk file. Returns true only on exact
    /// fixed-time match. When the file does not exist, bootstrap is considered closed and
    /// the method returns false (so the caller returns Unauthorized instead of mis-creating
    /// an admin silently).
    /// </summary>
    public static bool Validate(IHostEnvironment env, string? presented, IConfiguration? config = null)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        var path = ResolveTokenPath(env, config);
        if (!File.Exists(path)) return false;
        string? expected;
        try
        {
            var read = RestrictedFileWriter.ReadValidatedText(path);
            if (!read.Security.IsSecure && !env.IsDevelopment()) return false;
            expected = read.Content?.Trim();
        }
        catch { return false; }
        if (string.IsNullOrEmpty(expected)) return false;
        return SecretComparer.FixedTimeEquals(presented, expected);
    }

    /// <summary>
    /// Remove the token file after a successful admin creation so the bootstrap window
    /// closes permanently.
    /// </summary>
    public static void Consume(IHostEnvironment env, IConfiguration? config = null)
    {
        var path = ResolveTokenPath(env, config);
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

}

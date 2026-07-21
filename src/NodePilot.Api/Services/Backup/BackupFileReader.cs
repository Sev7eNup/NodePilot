using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Data.Security;

namespace NodePilot.Api.Services.Backup;

/// <summary>Raised when a <c>.npbackup</c> file is malformed or has the wrong schema.</summary>
public sealed class BackupFormatException(string message) : Exception(message);

/// <summary>
/// Parses and validates a <c>nodepilot-system-backup/v1</c> file (ADR 0001). Parsing is
/// passphrase-free (so preview can run without one); unlocking the secrets and verifying the
/// whole-file MAC requires the passphrase.
/// </summary>
public sealed class BackupFileReader
{
    public JsonObject Envelope { get; }
    public JsonObject Sections { get; }
    public string Schema { get; }
    public string? AppVersion { get; }

    private readonly byte[] _salt;
    private readonly int _iterations;
    private readonly byte[] _verifier;
    private readonly byte[] _mac;

    private BackupFileReader(JsonObject env, JsonObject sections, byte[] salt, int iterations, byte[] verifier, byte[] mac)
    {
        Envelope = env;
        Sections = sections;
        Schema = env["schema"]!.GetValue<string>();
        AppVersion = env["appVersion"]?.GetValue<string>();
        _salt = salt;
        _iterations = iterations;
        _verifier = verifier;
        _mac = mac;
    }

    public static BackupFileReader Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        JsonObject env;
        try
        {
            env = JsonNode.Parse(Encoding.UTF8.GetString(content)) as JsonObject
                ?? throw new BackupFormatException("Backup file root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException($"Backup file is not valid JSON: {ex.Message}");
        }

        var schema = env["schema"]?.GetValue<string>();
        if (schema is null || !BackupSections.SupportedSchemas.Contains(schema))
            throw new BackupFormatException($"Unsupported backup schema '{schema}'. Supported: {string.Join(", ", BackupSections.SupportedSchemas)}.");

        if (env["sections"] is not JsonObject sections)
            throw new BackupFormatException("Backup file has no 'sections' object.");
        if (env["crypto"] is not JsonObject crypto)
            throw new BackupFormatException("Backup file has no 'crypto' header.");
        if (env["mac"] is not JsonValue macVal || macVal.GetValueKind() != JsonValueKind.String)
            throw new BackupFormatException("Backup file has no 'mac'.");

        byte[] salt, verifier, mac;
        int iterations;
        try
        {
            salt = Convert.FromBase64String(crypto["salt"]!.GetValue<string>());
            verifier = Convert.FromBase64String(crypto["verifier"]!.GetValue<string>());
            iterations = crypto["iterations"]!.GetValue<int>();
            mac = Convert.FromBase64String(macVal.GetValue<string>());
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NullReferenceException)
        {
            throw new BackupFormatException("Backup 'crypto' header is malformed.");
        }

        return new BackupFileReader(env, sections, salt, iterations, verifier, mac);
    }

    /// <summary>
    /// Derives the passphrase protector and validates it against the file's verifier. Returns null
    /// for a wrong passphrase — the caller decides whether that's fatal (restore) or just means
    /// "integrity unverified" (preview).
    /// </summary>
    public PassphraseSecretProtector? TryUnlock(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) return null;
        var protector = PassphraseSecretProtector.Derive(passphrase, _salt, _iterations);
        return protector.VerifyPassphrase(_verifier) ? protector : null;
    }

    /// <summary>Recomputes the whole-file MAC over the canonical envelope and compares it (K5).</summary>
    public bool VerifyMac(PassphraseSecretProtector protector)
    {
        var canonical = BackupCanonicalJson.Canonicalize(Envelope, excludeKey: "mac");
        return protector.VerifyMac(canonical, _mac);
    }
}

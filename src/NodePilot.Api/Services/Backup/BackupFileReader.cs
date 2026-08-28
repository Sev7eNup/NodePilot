using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Data.Security;

namespace NodePilot.Api.Services.Backup;

public sealed class BackupFormatException(string message) : Exception(message);

/// <summary>
/// Reads the fully encrypted backup envelope. Parsing exposes only bounded KDF metadata; section
/// names, counts, application metadata and resource data become available only after the
/// passphrase verifier and authenticated AES-GCM payload have both been validated.
/// </summary>
public sealed class BackupFileReader
{
    internal const int MinKdfIterations = 100_000;
    internal const int MaxKdfIterations = 2_000_000;

    public JsonObject Envelope { get; private set; } = new();
    public JsonObject Sections { get; private set; } = new();
    public string Schema { get; }
    public string? AppVersion { get; private set; }
    public bool IntegrityVerified { get; private set; }

    private readonly byte[] _salt;
    private readonly int _iterations;
    private readonly byte[] _verifier;
    private readonly byte[] _payload;

    private BackupFileReader(string schema, byte[] salt, int iterations, byte[] verifier, byte[] payload)
    {
        Schema = schema;
        _salt = salt;
        _iterations = iterations;
        _verifier = verifier;
        _payload = payload;
    }

    public static BackupFileReader Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        JsonObject outer;
        try
        {
            outer = JsonNode.Parse(Encoding.UTF8.GetString(content)) as JsonObject
                ?? throw new BackupFormatException("Backup file root must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException($"Backup file is not valid JSON: {ex.Message}");
        }

        var schema = outer["schema"]?.GetValue<string>();
        if (schema is null || !BackupSections.SupportedSchemas.Contains(schema))
            throw new BackupFormatException(
                $"Unsupported backup schema '{schema}'. Supported: {string.Join(", ", BackupSections.SupportedSchemas)}.");
        if (outer["crypto"] is not JsonObject crypto)
            throw new BackupFormatException("Backup file has no 'crypto' header.");
        if (outer["payload"] is not JsonValue payloadValue || payloadValue.GetValueKind() != JsonValueKind.String)
            throw new BackupFormatException("Backup file has no encrypted 'payload'.");

        try
        {
            var salt = Convert.FromBase64String(crypto["salt"]!.GetValue<string>());
            var verifier = Convert.FromBase64String(crypto["verifier"]!.GetValue<string>());
            var iterations = crypto["iterations"]!.GetValue<int>();
            var payload = Convert.FromBase64String(payloadValue.GetValue<string>());
            if (iterations is < MinKdfIterations or > MaxKdfIterations)
                throw new BackupFormatException(
                    $"Backup 'crypto.iterations' must be between {MinKdfIterations} and {MaxKdfIterations}.");
            return new BackupFileReader(schema, salt, iterations, verifier, payload);
        }
        catch (BackupFormatException) { throw; }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NullReferenceException)
        {
            throw new BackupFormatException("Backup 'crypto' header or encrypted payload is malformed.");
        }
    }

    public PassphraseSecretProtector? TryUnlock(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) return null;
        var protector = PassphraseSecretProtector.Derive(passphrase, _salt, _iterations);
        if (!protector.VerifyPassphrase(_verifier)) return null;

        string json;
        try
        {
            json = protector.Unprotect(_payload);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or ArgumentException)
        {
            throw new BackupFormatException(
                $"Encrypted backup payload failed authentication and may be corrupt or tampered: {ex.Message}");
        }

        try
        {
            var payload = JsonNode.Parse(json) as JsonObject
                          ?? throw new BackupFormatException("Decrypted backup payload must be a JSON object.");
            if (payload["sections"] is not JsonObject sections)
                throw new BackupFormatException("Decrypted backup payload has no 'sections' object.");
            Envelope = payload;
            Sections = sections;
            AppVersion = payload["appVersion"]?.GetValue<string>();
            IntegrityVerified = true;
            return protector;
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException($"Decrypted backup payload is not valid JSON: {ex.Message}");
        }
    }
}

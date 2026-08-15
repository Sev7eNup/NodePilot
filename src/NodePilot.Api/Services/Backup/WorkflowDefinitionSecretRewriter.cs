using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Data.Security;

namespace NodePilot.Api.Services.Backup;

/// <summary>
/// How a workflow <c>DefinitionJson</c> is protected when it leaves the system (ADR 0001 K2).
/// Collaboration exports use the shared structural redactor; DR backups encrypt the complete
/// definition because arbitrary executable payloads cannot be classified safely by key name.
/// </summary>
public enum SecretHandling
{
    /// <summary>Replace secret values with <c>"***"</c> — the share/export-for-collaboration path.</summary>
    Redact,

    /// <summary>Seal the complete definition as an authenticated <c>$encDefinition</c> envelope
    /// under the backup passphrase — the DR backup path.</summary>
    EncryptForBackup,
}

/// <summary>
/// Redacts a structure-preserving sharing copy or seals the complete definition for DR backup.
/// Generalises the former <c>WorkflowsControllerBase.RedactSecretsInDefinition</c> while keeping
/// collaboration and backup confidentiality policies explicit and independently fail-closed.
/// </summary>
public static class WorkflowDefinitionSecretRewriter
{
    /// <summary>
    /// Config keys whose string values are treated as secrets. Re-exported from
    /// <see cref="NodePilot.Core.WorkflowDefinitions.WorkflowSecretKeys.SecretConfigKeys"/> (the
    /// single source of truth in Core) so the API redaction/export/backup paths and the MCP
    /// server's definition redaction can never disagree about what counts as a secret.
    /// </summary>
    public static readonly IReadOnlySet<string> SecretConfigKeys =
        NodePilot.Core.WorkflowDefinitions.WorkflowSecretKeys.SecretConfigKeys;

    /// <summary>The marker object key used for passphrase-encrypted values across the whole backup.</summary>
    public const string EncKey = "$enc";

    /// <summary>
    /// Marker for a backup-passphrase-encrypted complete workflow definition. Whole-definition
    /// sealing is deliberate: executable/free-form fields cannot be classified safely by inspecting
    /// their contents, and GUID references can be decrypted and remapped during restore.
    /// </summary>
    public const string DefinitionEncKey = "$encDefinition";

    /// <summary>
    /// Rewrites <paramref name="root"/> according to <paramref name="handling"/>. For
    /// <see cref="SecretHandling.EncryptForBackup"/>, <paramref name="protector"/> must be supplied.
    /// </summary>
    public static JsonNode Rewrite(JsonElement root, SecretHandling handling, PassphraseSecretProtector? protector)
    {
        // Redact is the pure, Data-free path — delegate to the shared Core helper so the API export,
        // the MCP definition-redaction and the AI chat assistant can never disagree about redaction.
        if (handling == SecretHandling.Redact)
            return NodePilot.Core.WorkflowDefinitions.WorkflowSecretRedactor.Redact(root);

        if (handling == SecretHandling.EncryptForBackup && protector is null)
            throw new ArgumentNullException(nameof(protector), "EncryptForBackup requires a passphrase protector.");

        // Seal the complete definition. Selective encryption can never be sound for arbitrary
        // PowerShell, request bodies, custom headers, or imported SCOrch payloads: an unrecognised
        // literal is still a potential credential. Keeping a single encrypted blob also avoids
        // leaking structure and identifiers through a DR archive.
        return new JsonObject
        {
            [DefinitionEncKey] = Convert.ToBase64String(protector!.Protect(root.GetRawText())),
        };
    }

    /// <summary>
    /// Reverses an <see cref="SecretHandling.EncryptForBackup"/> definition for restore: decrypts
    /// every <c>{"$enc":…}</c> back to its plaintext string, and remaps the <c>targetMachineId</c> /
    /// <c>credentialId</c> GUID references through the supplied resolvers (ADR 0001 K13). A resolver
    /// returning <c>null</c> records the original value in <paramref name="unresolved"/> and leaves it
    /// in place — the caller (restore validation, K12) is expected to have already aborted on those.
    /// Other strings (templates like <c>{{globals.X}}</c>, scripts, node ids) are preserved verbatim.
    /// </summary>
    public static JsonNode RestoreDefinition(
        JsonNode definition,
        PassphraseSecretProtector protector,
        Func<Guid, Guid?> resolveMachine,
        Func<Guid, Guid?> resolveCredential,
        List<string> unresolved)
    {
        var wholeDefinitionEnvelope = TryUnsealBackupDefinition(definition, protector, out var plaintextDefinition);
        var restored = RestoreWalk(
            plaintextDefinition, protector,
            decryptLegacyFieldEnvelopes: !wholeDefinitionEnvelope);
        RemapNodeInfrastructureReferences(
            restored, resolveMachine, resolveCredential, unresolved);
        return restored;
    }

    /// <summary>
    /// Decrypts the whole-definition envelope used by current backups. A deep clone is returned for
    /// older per-field-encrypted backups so the existing recursive restore remains backward compatible.
    /// </summary>
    public static JsonNode UnsealBackupDefinition(JsonNode definition, PassphraseSecretProtector protector)
    {
        TryUnsealBackupDefinition(definition, protector, out var plaintextDefinition);
        return plaintextDefinition;
    }

    private static bool TryUnsealBackupDefinition(
        JsonNode definition, PassphraseSecretProtector protector, out JsonNode plaintextDefinition)
    {
        if (definition is JsonObject envelope
            && envelope.Count == 1
            && envelope.TryGetPropertyValue(DefinitionEncKey, out var ciphertext)
            && ciphertext is JsonValue value
            && value.TryGetValue(out string? encoded)
            && !string.IsNullOrEmpty(encoded))
        {
            var plaintext = protector.Unprotect(Convert.FromBase64String(encoded));
            plaintextDefinition = JsonNode.Parse(plaintext)
                ?? throw new InvalidOperationException("Encrypted workflow definition contained JSON null.");
            return true;
        }

        plaintextDefinition = definition.DeepClone();
        return false;
    }

    private static JsonNode RestoreWalk(
        JsonNode node,
        PassphraseSecretProtector protector,
        bool decryptLegacyFieldEnvelopes)
    {
        switch (node)
        {
            // An {"$enc":"<b64>"} object is a sealed secret — decrypt it back to its string value.
            case JsonObject enc when decryptLegacyFieldEnvelopes
                && enc.Count == 1 && enc.TryGetPropertyValue(EncKey, out var b64)
                && b64 is JsonValue bv && bv.TryGetValue(out string? s) && s is not null:
                return JsonValue.Create(protector.Unprotect(Convert.FromBase64String(s)));
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (name, value) in obj)
                    result[name] = value is null ? null
                        : RestoreWalk(value, protector, decryptLegacyFieldEnvelopes);
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                    result.Add(item is null ? null
                        : RestoreWalk(item, protector, decryptLegacyFieldEnvelopes));
                return result;
            }
            default:
                return node.DeepClone();
        }
    }

    /// <summary>
    /// Runtime resolves infrastructure references only from each node's <c>data</c> object. Do
    /// not recursively rewrite same-named keys inside config payloads: they are ordinary child
    /// parameters/return data and changing them would silently corrupt application data.
    /// </summary>
    private static void RemapNodeInfrastructureReferences(
        JsonNode definition,
        Func<Guid, Guid?> resolveMachine,
        Func<Guid, Guid?> resolveCredential,
        List<string> unresolved)
    {
        if (definition is not JsonObject root || root["nodes"] is not JsonArray nodes) return;
        foreach (var node in nodes)
        {
            if (node is not JsonObject nodeObject || nodeObject["data"] is not JsonObject data) continue;
            RemapNodeReference(data, "targetMachineId", resolveMachine, unresolved);
            RemapNodeReference(data, "credentialId", resolveCredential, unresolved);
        }
    }

    private static void RemapNodeReference(
        JsonObject data,
        string key,
        Func<Guid, Guid?> resolver,
        List<string> unresolved)
    {
        if (data[key] is not JsonValue value
            || !value.TryGetValue(out string? raw)
            || !Guid.TryParse(raw, out var sourceId)) return;

        var target = resolver(sourceId);
        if (target is null)
        {
            unresolved.Add($"{key}={raw}");
            return;
        }
        data[key] = target.Value.ToString();
    }

}

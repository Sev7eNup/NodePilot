using System.Text.Json;
using System.Text.Json.Nodes;
using NodePilot.Data.Security;

namespace NodePilot.Api.Services.Backup;

/// <summary>
/// How secret-bearing string values inside a workflow <c>DefinitionJson</c> are treated when
/// the definition leaves the system (ADR 0001 K2). The same key list
/// (<see cref="WorkflowDefinitionSecretRewriter.SecretConfigKeys"/>) drives all three modes so the
/// contextual "share one workflow" export and the system backup never disagree about what's a secret.
/// </summary>
public enum SecretHandling
{
    /// <summary>Replace secret values with <c>"***"</c> — the share/export-for-collaboration path.</summary>
    Redact,

    /// <summary>Replace secret values with an <c>{"$enc":"&lt;base64&gt;"}</c> object encrypted under
    /// the backup passphrase — the DR backup path.</summary>
    EncryptForBackup,

    /// <summary>Leave values untouched. Internal/test only — never sent over the wire.</summary>
    PlainInternal,
}

/// <summary>
/// Structure-preserving rewrite of a workflow definition that handles the inline secret-bearing
/// config keys uniformly. Generalises the former <c>WorkflowsControllerBase.RedactSecretsInDefinition</c>
/// so both the workflow-sharing export and the system backup share one implementation.
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

        var node = JsonNode.Parse(root.GetRawText())
            ?? throw new InvalidOperationException("Workflow definition is not valid JSON.");
        return Walk(node, parentName: null, handling, protector);
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
        return RestoreWalk(definition, parentName: null, protector, resolveMachine, resolveCredential, unresolved);
    }

    private static JsonNode RestoreWalk(
        JsonNode node, string? parentName, PassphraseSecretProtector protector,
        Func<Guid, Guid?> resolveMachine, Func<Guid, Guid?> resolveCredential, List<string> unresolved)
    {
        switch (node)
        {
            // An {"$enc":"<b64>"} object is a sealed secret — decrypt it back to its string value.
            case JsonObject enc when enc.Count == 1 && enc.TryGetPropertyValue(EncKey, out var b64)
                && b64 is JsonValue bv && bv.TryGetValue(out string? s) && s is not null:
                return JsonValue.Create(protector.Unprotect(Convert.FromBase64String(s)));
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (name, value) in obj)
                    result[name] = value is null ? null
                        : RestoreWalk(value, name, protector, resolveMachine, resolveCredential, unresolved);
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                    result.Add(item is null ? null
                        : RestoreWalk(item, parentName, protector, resolveMachine, resolveCredential, unresolved));
                return result;
            }
            case JsonValue val when val.TryGetValue(out string? str) && str is not null:
            {
                var resolver = parentName switch
                {
                    "targetMachineId" => resolveMachine,
                    "credentialId" => resolveCredential,
                    _ => (Func<Guid, Guid?>?)null,
                };
                if (resolver is not null && Guid.TryParse(str, out var sourceId))
                {
                    var target = resolver(sourceId);
                    if (target is null)
                    {
                        unresolved.Add($"{parentName}={str}");
                        return JsonValue.Create(str);
                    }
                    return JsonValue.Create(target.Value.ToString());
                }
                return JsonValue.Create(str);
            }
            default:
                return node.DeepClone();
        }
    }

    private static JsonNode Walk(JsonNode node, string? parentName, SecretHandling handling, PassphraseSecretProtector? protector)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (name, value) in obj)
                    result[name] = value is null ? null : Walk(value, name, handling, protector);
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                    result.Add(item is null ? null : Walk(item, parentName, handling, protector));
                return result;
            }
            case JsonValue val when val.TryGetValue(out string? s) && s is not null:
            {
                if (!NodePilot.Core.WorkflowDefinitions.WorkflowSecretKeys.IsSecretValue(parentName, s))
                    return JsonValue.Create(s);

                return handling switch
                {
                    SecretHandling.Redact => JsonValue.Create("***"),
                    SecretHandling.EncryptForBackup => new JsonObject
                    {
                        [EncKey] = Convert.ToBase64String(protector!.Protect(s)),
                    },
                    _ => JsonValue.Create(s), // PlainInternal
                };
            }
            default:
                return node.DeepClone();
        }
    }
}

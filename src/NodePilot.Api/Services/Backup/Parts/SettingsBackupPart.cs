using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NodePilot.Api.Configuration;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports the runtime configuration overrides — and ONLY those (ADR 0001 K9): the raw contents of
/// <c>appsettings.runtime.json</c>, never the effective merged <c>IConfiguration</c> (which would
/// pull in host/env secrets). The transient <c>__meta</c> block (restart markers) is dropped. Any
/// <c>enc:v1:</c>-encrypted value is decrypted with the active <see cref="ISecretProtector"/> and
/// rewrapped under the backup passphrase so it stays portable.
/// </summary>
public sealed class SettingsBackupPart(RuntimeOverridesWriter overrides, ISecretProtector atRest) : IBackupPart
{
    public string Key => BackupSections.Settings;
    public IReadOnlyList<string> DependsOn => [];

    public Task<int> CountAsync(CancellationToken ct)
    {
        var root = overrides.ReadOrEmpty();
        var count = root.Count(kv => kv.Key != RuntimeOverridesWriter.MetaSectionKey);
        return Task.FromResult(count);
    }

    public Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var root = overrides.ReadOrEmpty();
        var result = new JsonObject();
        foreach (var (key, value) in root)
        {
            if (key == RuntimeOverridesWriter.MetaSectionKey) continue;
            result[key] = value is null ? null : Rewrite(value, ctx);
        }
        return Task.FromResult<JsonNode>(new JsonObject { ["runtimeJson"] = result });
    }

    private JsonNode? Rewrite(JsonNode node, BackupExportContext ctx)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var r = new JsonObject();
                foreach (var (k, v) in obj) r[k] = v is null ? null : Rewrite(v, ctx);
                return r;
            }
            case JsonArray arr:
            {
                var r = new JsonArray();
                foreach (var v in arr) r.Add(v is null ? null : Rewrite(v, ctx));
                return r;
            }
            case JsonValue val when val.TryGetValue(out string? s) && s is not null
                && EncryptingJsonConfigurationProvider.LooksEncrypted(s):
            {
                try
                {
                    var blob = Convert.FromBase64String(s[EncryptingJsonConfigurationProvider.EncryptedValuePrefix.Length..]);
                    var plaintext = atRest.Unprotect(blob);
                    return ctx.Enc(plaintext);
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    ctx.Warn("A runtime-settings secret value could not be decrypted on this host — exported as-is (still ciphertext).");
                    return JsonValue.Create(s);
                }
            }
            default:
                return node.DeepClone();
        }
    }
}

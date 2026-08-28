using System.Security.Cryptography;
using System.Text.Json.Nodes;
using NodePilot.Api.Configuration;
using NodePilot.Core.Interfaces;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports the runtime configuration overrides only (ADR 0001 K9): the raw contents of
/// <c>appsettings.runtime.json</c>, never the merged <c>IConfiguration</c>, which would pull in
/// host/env secrets. The transient <c>__meta</c> block is dropped. Encrypted values are decrypted
/// with <see cref="ISecretProtector"/> and rewrapped under the backup passphrase; legacy plaintext
/// secret fields are wrapped the same way.
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
            result[key] = value is null ? null : Rewrite(value, ctx, key);
        }
        return Task.FromResult<JsonNode>(new JsonObject { ["runtimeJson"] = result });
    }

    private JsonNode? Rewrite(JsonNode node, BackupExportContext ctx, string path)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var r = new JsonObject();
                foreach (var (k, v) in obj)
                    r[k] = v is null ? null : Rewrite(v, ctx, $"{path}.{k}");
                return r;
            }
            case JsonArray arr:
            {
                var r = new JsonArray();
                var index = 0;
                foreach (var v in arr)
                {
                    r.Add(v is null ? null : Rewrite(v, ctx, $"{path}.{index}"));
                    index++;
                }
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
            case JsonValue val when val.TryGetValue(out string? s) && !string.IsNullOrEmpty(s)
                && IsRegisteredSecretPath(path):
                return ctx.Enc(s);
            default:
                return node.DeepClone();
        }
    }

    private static bool IsRegisteredSecretPath(string path)
    {
        foreach (var descriptor in SettingsSchema.Sections)
        {
            var sectionPrefix = descriptor.SectionPath.Replace(':', '.') + ".";
            if (!path.StartsWith(sectionPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var actual = path[sectionPrefix.Length..].Split('.');
            foreach (var pattern in descriptor.SecretFieldPaths)
            {
                var expected = pattern.Split('.');
                if (actual.Length != expected.Length) continue;
                if (expected.Select((segment, index) =>
                        segment == "*" || segment.Equals(actual[index], StringComparison.OrdinalIgnoreCase))
                    .All(matches => matches))
                    return true;
            }
        }

        return false;
    }
}

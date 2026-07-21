using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports credentials with their passwords rewrapped for backup (ADR 0001): the at-rest ciphertext
/// is decrypted with the active <see cref="ISecretProtector"/> and re-encrypted under the backup
/// passphrase. A row whose ciphertext cannot be decrypted on this host (e.g. DPAPI scope mismatch)
/// is exported without a password and a warning is recorded — the same skip-don't-fail behaviour as
/// the credential re-encryption sweep.
/// </summary>
public sealed class CredentialBackupPart(NodePilotDbContext db, ISecretProtector atRest) : IBackupPart
{
    public string Key => BackupSections.Credentials;
    public IReadOnlyList<string> DependsOn => [];

    public Task<int> CountAsync(CancellationToken ct) => db.Credentials.CountAsync(ct);

    public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var creds = await db.Credentials.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var items = new JsonArray();
        foreach (var c in creds)
        {
            var item = new JsonObject
            {
                ["sourceId"] = c.Id.ToString(),
                ["name"] = c.Name,
                ["username"] = c.Username,
                ["domain"] = c.Domain,
                ["expiresAt"] = c.ExpiresAt?.ToString("O"),
            };

            try
            {
                var plaintext = atRest.Unprotect(c.EncryptedPassword);
                item["password"] = ctx.Enc(plaintext);
            }
            catch (CryptographicException)
            {
                item["passwordUnavailable"] = true;
                ctx.Warn($"Credential '{c.Name}' password could not be decrypted on this host (provider '{atRest.ProviderName}') — exported without password.");
            }

            items.Add(item);
        }
        return new JsonObject { ["items"] = items };
    }
}

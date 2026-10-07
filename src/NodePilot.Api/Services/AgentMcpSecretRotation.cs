using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Services;

internal static class AgentMcpSecretRotation
{
    internal static async Task<ReencryptionSummary> ReencryptAsync(NodePilotDbContext db, ISecretProtector protector, CancellationToken ct)
    {
        var rewritten = 0;
        var skipped = new List<ReencryptionSkip>();
        Guid? cursor = null;
        while (true)
        {
            var query = db.AgentMcpServers.AsNoTracking().Where(s => s.ProtectedSecrets != null);
            if (cursor.HasValue) query = query.Where(s => s.Id.CompareTo(cursor.Value) > 0);
            var rows = await query.OrderBy(s => s.Id).Take(100).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                byte[] replacement;
                try { replacement = protector.Protect(protector.Unprotect(row.ProtectedSecrets!)); }
                catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or InvalidOperationException)
                {
                    skipped.Add(new ReencryptionSkip(row.Id, row.Name, ex.GetType().Name));
                    continue;
                }
                // Do not overwrite credentials concurrently changed by an administrator.
                var changed = await db.AgentMcpServers.Where(s => s.Id == row.Id && s.UpdatedAt == row.UpdatedAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProtectedSecrets, replacement)
                        .SetProperty(x => x.SecretProvider, protector.ProviderName).SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);
                if (changed == 1) rewritten++;
                else skipped.Add(new ReencryptionSkip(row.Id, row.Name, "ConcurrentUpdate"));
            }
            cursor = rows[^1].Id;
        }
        return new ReencryptionSummary(rewritten, skipped.Count, skipped);
    }
}

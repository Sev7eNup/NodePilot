using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Data;

namespace NodePilot.Api.Services;

internal static class DatabaseSecretRotation
{
    internal static async Task<ReencryptionSummary> ReencryptAgentMcpAsync(NodePilotDbContext db, ISecretProtector protector, CancellationToken ct)
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

    internal static async Task<ReencryptionSummary> ReencryptNotificationRoutesAsync(
        NodePilotDbContext db, ISecretProtector protector, CancellationToken ct)
    {
        var rewritten = 0;
        var skipped = new List<ReencryptionSkip>();
        Guid? cursor = null;
        while (true)
        {
            var query = db.NotificationRoutes.AsNoTracking().Where(s => s.Secret != null);
            if (cursor.HasValue) query = query.Where(s => s.Id.CompareTo(cursor.Value) > 0);
            var rows = await query.OrderBy(s => s.Id).Take(100)
                .Select(s => new { s.Id, s.Secret, Name = s.Rule.Name }).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                string replacement;
                try { replacement = Convert.ToBase64String(protector.Protect(protector.Unprotect(Convert.FromBase64String(row.Secret!)))); }
                catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or InvalidOperationException)
                {
                    skipped.Add(new ReencryptionSkip(row.Id, row.Name, ex.GetType().Name));
                    continue;
                }
                var changed = await db.NotificationRoutes.Where(s => s.Id == row.Id && s.Secret == row.Secret)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Secret, replacement), ct);
                if (changed == 1) rewritten++;
                else skipped.Add(new ReencryptionSkip(row.Id, row.Name, "ConcurrentUpdate"));
            }
            cursor = rows[^1].Id;
        }
        return new ReencryptionSummary(rewritten, skipped.Count, skipped);
    }

    internal static async Task<ReencryptionSummary> ReencryptDispatchParametersAsync(
        NodePilotDbContext db, ISecretProtector protector, CancellationToken ct)
    {
        var rewritten = 0;
        var skipped = new List<ReencryptionSkip>();
        Guid? cursor = null;
        while (true)
        {
            var query = db.ExecutionDispatchOutbox.AsNoTracking().Where(s => s.ProtectedParameters != null);
            if (cursor.HasValue) query = query.Where(s => s.ExecutionId.CompareTo(cursor.Value) > 0);
            var rows = await query.OrderBy(s => s.ExecutionId).Take(100)
                .Select(s => new { s.ExecutionId, s.ProtectedParameters }).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                byte[] replacement;
                try { replacement = protector.Protect(protector.Unprotect(row.ProtectedParameters!)); }
                catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or InvalidOperationException)
                {
                    skipped.Add(new ReencryptionSkip(row.ExecutionId, row.ExecutionId.ToString(), ex.GetType().Name));
                    continue;
                }
                // Dispatch owns the lease fields. Only swap the exact payload read above;
                // a claimed/deleted or replaced row is a skip, never a successful conversion.
                var changed = await db.ExecutionDispatchOutbox.Where(s => s.ExecutionId == row.ExecutionId
                        && s.ProtectedParameters == row.ProtectedParameters)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProtectedParameters, replacement), ct);
                if (changed == 1) rewritten++;
                else skipped.Add(new ReencryptionSkip(row.ExecutionId, row.ExecutionId.ToString(), "ConcurrentUpdate"));
            }
            cursor = rows[^1].ExecutionId;
        }
        return new ReencryptionSummary(rewritten, skipped.Count, skipped);
    }
}

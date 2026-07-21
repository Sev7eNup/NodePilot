using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodePilot.Data;

namespace NodePilot.Api.Services.Backup.Parts;

/// <summary>
/// Exports user accounts. The BCrypt <c>passwordHash</c> is not re-hashed — it is a one-way hash —
/// but it is still sensitive, so it is placed behind the backup passphrase (<c>$enc</c>) like any
/// other secret. Transient login state (FailedLoginCount / LockedUntil) is not exported.
/// <c>securityStamp</c> / <c>passwordChangedAt</c> are exported so a restored user keeps a coherent
/// session-validity baseline.
/// </summary>
public sealed class UserBackupPart(NodePilotDbContext db) : IBackupPart
{
    public string Key => BackupSections.Users;
    public IReadOnlyList<string> DependsOn => [];

    public Task<int> CountAsync(CancellationToken ct) => db.Users.CountAsync(ct);

    public async Task<JsonNode> ExportAsync(BackupExportContext ctx, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking()
            .Include(u => u.ExternalIdentities)
            .OrderBy(u => u.Username)
            .ToListAsync(ct);
        var membershipsByUser = (await db.DirectoryMemberships.AsNoTracking()
                .OrderBy(m => m.Authority)
                .ThenBy(m => m.GroupKey)
                .ToListAsync(ct))
            .GroupBy(m => m.UserId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var items = new JsonArray();
        foreach (var u in users)
        {
            var item = new JsonObject
            {
                ["sourceId"] = u.Id.ToString(),
                ["username"] = u.Username,
                ["role"] = u.Role.ToString(),
                ["provider"] = u.Provider.ToString(),
                ["externalId"] = u.ExternalId,
                ["knownGroupSidsJson"] = u.KnownGroupSidsJson,
                ["isActive"] = u.IsActive,
                ["isBreakGlass"] = u.IsBreakGlass,
                ["isTombstoned"] = u.IsTombstoned,
                ["lastDirectorySyncAt"] = u.LastDirectorySyncAt?.ToString("O"),
                ["directorySyncStatus"] = u.DirectorySyncStatus,
                ["passwordChangedAt"] = u.PasswordChangedAt.ToString("O"),
                ["securityStamp"] = u.SecurityStamp,
                ["externalIdentities"] = new JsonArray(u.ExternalIdentities
                    .OrderBy(i => i.Authority, StringComparer.Ordinal)
                    .ThenBy(i => i.Subject, StringComparer.Ordinal)
                    .Select(i => (JsonNode)new JsonObject
                    {
                        ["authority"] = i.Authority,
                        ["subject"] = i.Subject,
                        ["createdAt"] = i.CreatedAt.ToString("O"),
                        ["lastSeenAt"] = i.LastSeenAt.ToString("O"),
                    }).ToArray()),
                ["directoryMemberships"] = new JsonArray(
                    membershipsByUser.GetValueOrDefault(u.Id, [])
                        .Select(m => (JsonNode)new JsonObject
                        {
                            ["authority"] = m.Authority,
                            ["groupKey"] = m.GroupKey,
                            ["lastSeenAt"] = m.LastSeenAt.ToString("O"),
                        }).ToArray()),
            };
            if (!string.IsNullOrEmpty(u.PasswordHash))
                item["passwordHash"] = ctx.Enc(u.PasswordHash);
            items.Add(item);
        }
        return new JsonObject { ["items"] = items };
    }
}

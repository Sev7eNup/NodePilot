using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Security;

/// <summary>Database invariant for the BreakGlassOnly enterprise default.</summary>
public static class BreakGlassAccountPolicy
{
    public static bool IsRecoveryCapable(User user) =>
        user.Provider == AuthProvider.Local
        && user.Role == UserRole.Admin
        && user.IsActive
        && !user.IsTombstoned
        && user.IsBreakGlass
        && !string.IsNullOrEmpty(user.PasswordHash);

    public static Task<bool> ExistsAsync(NodePilotDbContext db, CancellationToken ct) =>
        db.Users.AnyAsync(user => user.Provider == AuthProvider.Local
                               && user.Role == UserRole.Admin
                               && user.IsActive
                               && !user.IsTombstoned
                               && user.IsBreakGlass
                               && user.PasswordHash != null
                               && user.PasswordHash != string.Empty, ct);

    public static async Task<bool> MutationPreservesRecoveryAsync(
        NodePilotDbContext db,
        User mutatedUser,
        CancellationToken ct)
    {
        if (IsRecoveryCapable(mutatedUser)) return true;
        return await db.Users.AsNoTracking().AnyAsync(user => user.Id != mutatedUser.Id
            && user.Provider == AuthProvider.Local
            && user.Role == UserRole.Admin
            && user.IsActive
            && !user.IsTombstoned
            && user.IsBreakGlass
            && user.PasswordHash != null
            && user.PasswordHash != string.Empty, ct);
    }

    public static bool IsRecoveryCapableAfterColumnUpdate(
        User user,
        string columnName,
        object? newValue)
    {
        var provider = user.Provider;
        var role = user.Role;
        var active = user.IsActive;
        var tombstoned = user.IsTombstoned;
        var breakGlass = user.IsBreakGlass;
        var passwordHash = user.PasswordHash;

        if (columnName.Equals(nameof(User.Provider), StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<AuthProvider>(newValue?.ToString(), true, out var parsedProvider))
            provider = parsedProvider;
        if (columnName.Equals(nameof(User.Role), StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<UserRole>(newValue?.ToString(), true, out var parsedRole))
            role = parsedRole;
        if (columnName.Equals(nameof(User.IsActive), StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(newValue?.ToString(), out var parsedActive))
            active = parsedActive;
        if (columnName.Equals(nameof(User.IsTombstoned), StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(newValue?.ToString(), out var parsedTombstone))
            tombstoned = parsedTombstone;
        if (columnName.Equals(nameof(User.IsBreakGlass), StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(newValue?.ToString(), out var parsedBreakGlass))
            breakGlass = parsedBreakGlass;
        if (columnName.Equals(nameof(User.PasswordHash), StringComparison.OrdinalIgnoreCase))
            passwordHash = newValue?.ToString();

        return provider == AuthProvider.Local
               && role == UserRole.Admin
               && active
               && !tombstoned
               && breakGlass
               && !string.IsNullOrEmpty(passwordHash);
    }
}

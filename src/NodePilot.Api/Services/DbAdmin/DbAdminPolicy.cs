using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Services.DbAdmin;

/// <summary>
/// Central security policy for the DB-Admin viewer. Defines per-entity capabilities
/// (read/update/delete) and per-column restrictions (hidden, read-only).
///
/// Default stance: all entities are visible, but Update+Delete are OFF unless explicitly
/// whitelisted. Hidden columns are never returned in API responses and cannot be PATCH'd.
/// </summary>
public static class DbAdminPolicy
{
    public record EntityCapabilities(bool CanUpdate, bool CanDelete);

    public record ColumnRestriction(bool IsHidden, bool IsReadOnly);

    public record GuardResult(bool IsBlocked, string? Code = null, string? Message = null)
    {
        public static GuardResult Allow() => new(false);
        public static GuardResult Block(string code, string message) => new(true, code, message);
    }

    // --- Entity-level capabilities ---

    private static readonly Dictionary<string, EntityCapabilities> EntityCaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Workflow"]                  = new(CanUpdate: true,  CanDelete: true),
        ["WorkflowExecution"]         = new(CanUpdate: false, CanDelete: true),
        ["StepExecution"]             = new(CanUpdate: false, CanDelete: false),
        ["ManagedMachine"]            = new(CanUpdate: true,  CanDelete: true),
        ["Credential"]                = new(CanUpdate: true,  CanDelete: true),
        ["AuditLogEntry"]             = new(CanUpdate: false, CanDelete: false),  // compliance: immutable
        ["User"]                      = new(CanUpdate: true,  CanDelete: true),   // guarded separately
        ["RevokedToken"]              = new(CanUpdate: false, CanDelete: false),  // deleting re-activates sessions
        ["WorkflowVersion"]           = new(CanUpdate: false, CanDelete: false),  // append-only history
        ["IdempotencyKey"]            = new(CanUpdate: false, CanDelete: true),
        ["GlobalVariable"]            = new(CanUpdate: true,  CanDelete: true),
        ["SystemHealthHeartbeat"]     = new(CanUpdate: false, CanDelete: false),  // operational/derived
        ["WorkflowStats"]             = new(CanUpdate: false, CanDelete: false),  // aggregate, overwritten by refresher
    };

    // --- Column-level restrictions ---
    // Key: "EntityType.PropertyName" (case-insensitive)
    // Hidden  = never returned in API, cannot be PATCH'd
    // ReadOnly = returned but not editable

    private static readonly Dictionary<string, ColumnRestriction> ColumnRestrictions =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // User: password internals never exposed
        ["User.PasswordHash"]         = new(IsHidden: true,  IsReadOnly: true),
        ["User.PasswordChangedAt"]    = new(IsHidden: false, IsReadOnly: true),  // bypass lockout-invalidation
        ["User.SecurityStamp"]        = new(IsHidden: false, IsReadOnly: true),  // managed by session invalidation paths
        ["User.FailedLoginCount"]     = new(IsHidden: false, IsReadOnly: true),  // managed by auth middleware
        ["User.LockedUntil"]          = new(IsHidden: false, IsReadOnly: false), // admin may clear lock

        // Credential: encrypted blob never exposed via DB-admin
        ["Credential.EncryptedPassword"] = new(IsHidden: true, IsReadOnly: true),

        // GlobalVariable: Value always read-only (row-conditional IsSecret masking would
        // need per-row markers; blocking edits entirely and pointing users to the
        // dedicated Globals UI is cleaner and safer)
        ["GlobalVariable.Value"] = new(IsHidden: false, IsReadOnly: true),

        // Workflow.DefinitionJson: allowing this to be PATCHed via DbAdmin would bypass
        // domain validation, versioning, the edit-lock, and webhook-collision protection —
        // all of which are guaranteed by the WorkflowsController/WorkflowEditingController
        // path. Making it read-only via DbAdmin forces the operator onto the correct
        // mutation path; the column stays visible for forensics ("what's actually stored
        // right now"), but changes must go through the proper application surface.
        ["Workflow.DefinitionJson"] = new(IsHidden: false, IsReadOnly: true),
    };

    // Navigation properties (ICollection<*>) are always hidden — EF would need Include() to load them.
    // byte[] properties other than explicitly hidden ones are also hidden to avoid large binary blobs.

    public static EntityCapabilities GetCapabilities(string entityTypeName)
    {
        return EntityCaps.TryGetValue(entityTypeName, out var caps)
            ? caps
            : new EntityCapabilities(CanUpdate: false, CanDelete: false); // safe default
    }

    public static ColumnRestriction GetColumnRestriction(string entityTypeName, string propertyName)
    {
        var key = $"{entityTypeName}.{propertyName}";
        return ColumnRestrictions.TryGetValue(key, out var r) ? r : new(false, false);
    }

    public static bool IsColumnEffectivelyReadOnly(string entityTypeName, string propertyName,
        bool isPrimaryKey, Type clrType)
    {
        if (isPrimaryKey) return true;
        var r = GetColumnRestriction(entityTypeName, propertyName);
        if (r.IsHidden || r.IsReadOnly) return true;
        // Navigation properties (not simple scalar) — skip
        if (clrType.IsClass && clrType != typeof(string) && clrType != typeof(byte[])) return true;
        return false;
    }

    public static bool IsColumnHidden(string entityTypeName, string propertyName, Type clrType)
    {
        // byte[] columns (other than explicitly exposed ones) are hidden — binary blobs are
        // not editable in a text-cell UI and could be large.
        if (clrType == typeof(byte[])) return true;
        return GetColumnRestriction(entityTypeName, propertyName).IsHidden;
    }

    // --- User-specific guards (mirrors UsersController logic) ---

    /// <summary>
    /// Validates a PATCH on <see cref="User"/> before the mutation is applied.
    /// Must be called before db.SaveChangesAsync.
    /// </summary>
    public static async Task<GuardResult> PreUpdateUserGuardAsync(
        User user, string columnName, object? newValue,
        NodePilotDbContext db, Guid callerId, CancellationToken ct)
    {
        if (BreakGlassAccountPolicy.IsRecoveryCapable(user)
            && !BreakGlassAccountPolicy.IsRecoveryCapableAfterColumnUpdate(user, columnName, newValue)
            && !await db.Users.AsNoTracking().AnyAsync(other => other.Id != user.Id
                && other.Provider == AuthProvider.Local
                && other.Role == UserRole.Admin
                && other.IsActive
                && !other.IsTombstoned
                && other.IsBreakGlass
                && other.PasswordHash != null
                && other.PasswordHash != string.Empty, ct))
        {
            return GuardResult.Block(
                "last_break_glass_admin",
                "Cannot remove the last active local break-glass administrator.");
        }

        if (string.Equals(columnName, nameof(User.Role), StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<UserRole>(newValue?.ToString(), ignoreCase: true, out var newRole))
                return GuardResult.Block("invalid_value", $"Role must be one of {string.Join(", ", Enum.GetNames<UserRole>())}");

            // Self-demote block
            if (user.Id == callerId && user.Role == UserRole.Admin && newRole != UserRole.Admin)
                return GuardResult.Block("cannot_demote_self", "You cannot demote your own Admin account.");

            // Last-admin guard
            if (user.Role == UserRole.Admin && user.IsActive && newRole != UserRole.Admin)
            {
                var remainingActiveAdmins = await db.Users
                    .CountAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != user.Id, ct);
                if (remainingActiveAdmins == 0)
                    return GuardResult.Block("last_admin", "Cannot demote the last active admin.");
            }
        }

        if (string.Equals(columnName, nameof(User.IsActive), StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(newValue?.ToString(), out var active) && !active
            && user.Role == UserRole.Admin && user.IsActive)
        {
            var remainingActiveAdmins = await db.Users
                .CountAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != user.Id, ct);
            if (remainingActiveAdmins == 0)
                return GuardResult.Block("last_admin", "Cannot deactivate the last active admin.");
        }

        return GuardResult.Allow();
    }

    /// <summary>
    /// Validates a DELETE on <see cref="User"/> before the mutation is applied.
    /// </summary>
    public static async Task<GuardResult> PreDeleteUserGuardAsync(
        User user, NodePilotDbContext db, Guid callerId, CancellationToken ct)
    {
        if (user.Id == callerId)
            return GuardResult.Block("cannot_delete_self", "You cannot delete your own account.");

        if (BreakGlassAccountPolicy.IsRecoveryCapable(user)
            && !await db.Users.AsNoTracking().AnyAsync(other => other.Id != user.Id
                && other.Provider == AuthProvider.Local
                && other.Role == UserRole.Admin
                && other.IsActive
                && !other.IsTombstoned
                && other.IsBreakGlass
                && other.PasswordHash != null
                && other.PasswordHash != string.Empty, ct))
        {
            return GuardResult.Block(
                "last_break_glass_admin",
                "Cannot delete the last active local break-glass administrator.");
        }

        if (user.Role == UserRole.Admin && user.IsActive)
        {
            var remainingActiveAdmins = await db.Users
                .CountAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != user.Id, ct);
            if (remainingActiveAdmins == 0)
                return GuardResult.Block("last_admin", "Cannot delete the last active admin.");
        }

        return GuardResult.Allow();
    }
}

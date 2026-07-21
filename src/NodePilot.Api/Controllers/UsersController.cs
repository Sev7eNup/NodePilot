using System.Security.Claims;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin-only management of users. Before this controller existed, the only way to add
/// or deactivate users was direct DB manipulation — a foot-gun that broke audit trails and
/// invited accidental lockouts. This keeps the full CRUD inside the API with proper auth.
///
/// <para>
/// Every mutation emits a granular audit code (<c>USER_CREATED</c>,
/// <c>USER_ROLE_CHANGED</c>, <c>USER_ACTIVATED</c>/<c>USER_DEACTIVATED</c>,
/// <c>USER_PASSWORD_RESET</c>, <c>USER_DELETED</c>) so a compliance reviewer can
/// answer "who changed this user's role last quarter" / "when was this account
/// reset" from the audit trail alone — no DB introspection needed. Bundling several
/// changes into a single <c>USER_UPDATED</c> was rejected because SIEM rules want
/// to alert on password resets and role escalations independently.
/// </para>
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class UsersController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly IAuditWriter _audit;
    // H-1: shared with TokenValidityMiddleware so a stamp bump (role change / active toggle)
    // takes effect on the next request instead of waiting for the 30-second cache TTL.
    private readonly IMemoryCache _userStateCache;
    private readonly IWorkflowEngine? _workflowEngine;
    private readonly ILogger<UsersController>? _logger;

    public UsersController(
        NodePilotDbContext db,
        IAuditWriter audit,
        IMemoryCache userStateCache,
        IWorkflowEngine? workflowEngine = null,
        ILogger<UsersController>? logger = null)
    {
        _db = db;
        _audit = audit;
        _userStateCache = userStateCache;
        _workflowEngine = workflowEngine;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<List<UserResponse>>> GetAll(CancellationToken ct)
    {
        var users = await _db.Users
            .OrderBy(u => u.Username)
            .Select(u => new UserResponse(
                u.Id,
                u.Username,
                u.Role.ToString(),
                u.IsActive,
                u.CreatedAt,
                u.Provider.ToString(),
                u.ExternalIdentities.OrderBy(i => i.CreatedAt).Select(i => i.Authority).FirstOrDefault(),
                u.ExternalIdentities.OrderBy(i => i.CreatedAt).Select(i => i.Subject).FirstOrDefault(),
                u.LastDirectorySyncAt,
                u.DirectorySyncStatus ?? "Never",
                u.IsTombstoned,
                u.IsBreakGlass))
            .ToListAsync(ct);
        return Ok(users);
    }

    [HttpPost]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest(new { message = "username is required" });
        if (AuthController.ValidatePasswordPolicy(request.Password) is { } policyError)
            return BadRequest(new { message = policyError });
        if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
            return BadRequest(new { message = $"role must be one of {string.Join(", ", Enum.GetNames<UserRole>())}" });

        if (await _db.Users.AnyAsync(u => u.Username == request.Username, ct))
            return Conflict(new { message = "username already exists" });

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, AuthController.BCryptWorkFactor),
            Role = role,
            Provider = AuthProvider.Local,
            IsActive = true,
            IsBreakGlass = request.IsBreakGlass,
            CreatedAt = DateTime.UtcNow,
            PasswordChangedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);

        // RBAC Stufe A — every non-Admin gets a default grant on the Root folder so the new
        // user can immediately see and act within their global-role scope. Admin needs no
        // grant (global Admin bypasses folder ACLs). The companion follow-up migration
        // BackfillSharedFolderUserPermissions takes care of pre-existing users; this branch
        // covers the create-going-forward path so the runtime never has to re-seed.
        if (role != UserRole.Admin)
        {
            _db.SharedFolderPermissions.Add(new SharedFolderPermission
            {
                Id = Guid.NewGuid(),
                FolderId = SharedWorkflowFolder.RootFolderId,
                PrincipalType = FolderPrincipalType.User,
                PrincipalKey = user.Id.ToString("D"),
                Role = role == UserRole.Operator
                    ? SharedFolderRole.FolderEditor
                    : SharedFolderRole.FolderViewer,
                GrantedAt = DateTime.UtcNow,
                // GrantedByUserId left null — this is a system-issued default, not an
                // explicit admin grant. UI surfaces it as "system default" accordingly.
                GrantedByUserId = null,
            });
        }

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.UserCreated, "User", user.Id,
            AuditDetails.Json(
                ("username", user.Username),
                ("role", user.Role.ToString()),
                ("isBreakGlass", user.IsBreakGlass)), ct);

        return CreatedAtAction(nameof(GetAll), null,
            new UserResponse(
                user.Id, user.Username, user.Role.ToString(), user.IsActive, user.CreatedAt,
                user.Provider.ToString(), IsBreakGlass: user.IsBreakGlass));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
    {
        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        IReadOnlyList<Guid> committedExecutionIds = [];
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var attemptExecutionIds = new List<Guid>();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(_db, ct);
            var result = await UpdateCore(id, request, attemptExecutionIds, ct);
            if (result is NoContentResult)
            {
                await transaction.CommitAsync(ct);
                committedExecutionIds = attemptExecutionIds;
            }
            else
                await transaction.RollbackAsync(ct);
            return result;
        });
        await SignalCommittedExecutionCancellationsAsync(
            committedExecutionIds, "admin-authorization-change");
        return result;
    }

    private async Task<IActionResult> UpdateCore(
        Guid id,
        UpdateUserRequest request,
        ICollection<Guid> executionIdsToSignal,
        CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([id], ct);
        if (user is null) return NotFound();
        var wasRecoveryCapable = BreakGlassAccountPolicy.IsRecoveryCapable(user);

        // Track per-field changes so the audit step at the end of the method can emit the
        // right granular code. Capturing the "before" values up-front means the audit row
        // can record both old and new value without re-reading the entity.
        UserRole? oldRole = null;
        UserRole? newRole = null;
        bool? newActive = null;
        bool passwordReset = false;
        bool? oldBreakGlass = null;

        if (request.Role is not null)
        {
            if (!Enum.TryParse<UserRole>(request.Role, ignoreCase: true, out var role))
                return BadRequest(new { message = "invalid role" });
            // Prevent the last admin from demoting themselves — at least one Admin must remain
            // or the instance becomes unadministrable.
            if (user.Role == UserRole.Admin && user.IsActive && role != UserRole.Admin)
            {
                var remainingActiveAdmins = await _db.Users
                    .CountAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != user.Id, ct);
                if (remainingActiveAdmins == 0)
                    return BadRequest(new { message = "cannot demote the last active admin" });
            }
            if (user.Role != role)
            {
                oldRole = user.Role;
                newRole = role;
                user.Role = role;
                // H-1 (security audit 2026-05-15): bump SecurityStamp so existing JWT
                // sessions with the OLD role are rejected on the next request. Without
                // this a demoted Admin keeps Admin scope until their 12h token expires.
                UserSessionInvalidation.BumpSecurityStamp(user);
            }
        }

        if (request.IsActive is bool active)
        {
            if (active && user.IsTombstoned)
                return Conflict(new { message = "tombstoned users must be explicitly reactivated" });
            if (user.Role == UserRole.Admin && user.IsActive && !active)
            {
                var remainingActiveAdmins = await _db.Users
                    .CountAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != user.Id, ct);
                if (remainingActiveAdmins == 0)
                    return BadRequest(new { message = "cannot deactivate the last active admin" });
            }
            if (user.IsActive != active)
            {
                newActive = active;
                user.IsActive = active;
                // H-1: bump for active-toggle too. Defense-in-depth — IsActive=false also
                // triggers the middleware's IsActive check, but the stamp bump guarantees
                // the cached (up to 30s old) snapshot can't keep an existing token alive.
                UserSessionInvalidation.BumpSecurityStamp(user);
            }
        }

        if (!string.IsNullOrEmpty(request.Password))
        {
            if (user.Provider != AuthProvider.Local)
                return BadRequest(new { message = "external users do not have a local password" });
            if (AuthController.ValidatePasswordPolicy(request.Password) is { } pwError)
                return BadRequest(new { message = pwError });
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, AuthController.BCryptWorkFactor);

            // Stamp the user's PasswordChangedAt: TokenValidityMiddleware compares this to
            // the token's iat claim and rejects anything issued before the reset, so every
            // session this user had is invalidated the moment the admin saves. The user
            // logs back in, gets a fresh token, continues normally.
            user.PasswordChangedAt = DateTime.UtcNow;
            UserSessionInvalidation.BumpSecurityStamp(user);
            passwordReset = true;
        }

        if (request.IsBreakGlass is bool isBreakGlass)
        {
            if (user.Provider != AuthProvider.Local)
                return BadRequest(new { message = "only local users can be break-glass accounts" });
            if (user.IsBreakGlass != isBreakGlass)
            {
                oldBreakGlass = user.IsBreakGlass;
                user.IsBreakGlass = isBreakGlass;
                UserSessionInvalidation.BumpSecurityStamp(user);
            }
        }

        if (wasRecoveryCapable
            && !await BreakGlassAccountPolicy.MutationPreservesRecoveryAsync(_db, user, ct))
            return BadRequest(new
            {
                message = "cannot remove the last active local break-glass administrator",
            });

        var authorizationReduced = newActive == false
            || newRole is { } loweredRole && oldRole is { } previousRole && loweredRole < previousRole;
        if (authorizationReduced)
        {
            foreach (var executionId in await RevokeSessionsAndCancelExecutionsAsync(
                         user.Id, "admin-authorization-change", ct))
                executionIdsToSignal.Add(executionId);
        }

        await _db.SaveChangesAsync(ct);

        // H-1: drop the cached user-state snapshot so the next request re-reads from the DB
        // and picks up the bumped SecurityStamp / new IsActive. Without this the middleware
        // would keep the stale snapshot for up to 30 s, letting the demoted Admin act on
        // Admin-gated endpoints during that window.
        if (newRole is not null || newActive is not null || passwordReset || oldBreakGlass is not null)
            UserSessionInvalidation.InvalidateUserStateCache(_userStateCache, user.Id);

        // Emit one audit row per kind of change. Splitting (rather than one combined
        // USER_UPDATED) lets SIEM rules alert on password resets and role escalations
        // independently, and lets a compliance review filter for "every role change in Q3"
        // without parsing a details blob.
        if (newRole is not null)
        {
            await _audit.LogAsync(AuditActions.UserRoleChanged, "User", user.Id,
                AuditDetails.Json(
                    ("username", user.Username),
                    ("oldRole", oldRole!.Value.ToString()),
                    ("newRole", newRole!.Value.ToString())), ct);
        }
        if (newActive is not null)
        {
            await _audit.LogAsync(
                newActive.Value ? AuditActions.UserActivated : AuditActions.UserDeactivated,
                "User", user.Id,
                AuditDetails.Json(("username", user.Username)), ct);
        }
        if (passwordReset)
        {
            await _audit.LogAsync(AuditActions.UserPasswordReset, "User", user.Id,
                AuditDetails.Json(("username", user.Username)), ct);
        }
        if (oldBreakGlass is not null)
        {
            await _audit.LogAsync(AuditActions.UserBreakGlassChanged, "User", user.Id,
                AuditDetails.Json(
                    ("username", user.Username),
                    ("oldValue", oldBreakGlass.Value),
                    ("newValue", user.IsBreakGlass)), ct);
        }

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        IReadOnlyList<Guid> committedExecutionIds = [];
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var attemptExecutionIds = new List<Guid>();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(_db, ct);
            var result = await DeleteCore(id, attemptExecutionIds, ct);
            if (result is NoContentResult)
            {
                await transaction.CommitAsync(ct);
                committedExecutionIds = attemptExecutionIds;
            }
            else
                await transaction.RollbackAsync(ct);
            return result;
        });
        await SignalCommittedExecutionCancellationsAsync(
            committedExecutionIds, "admin-user-deleted");
        return result;
    }

    private async Task<IActionResult> DeleteCore(
        Guid id,
        ICollection<Guid> executionIdsToSignal,
        CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([id], ct);
        if (user is null) return NotFound();

        // Never hard-delete the last admin — soft-deactivation is safer and preserves audit.
        if (user.Role == UserRole.Admin && user.IsActive)
        {
            var remainingActiveAdmins = await _db.Users
                .CountAsync(u => u.Role == UserRole.Admin && u.IsActive && u.Id != user.Id, ct);
            if (remainingActiveAdmins == 0)
                return BadRequest(new { message = "cannot delete the last active admin" });
        }

        // Prevent an admin from deleting themselves while signed-in — would leave a broken
        // session and potentially orphan the only admin account.
        var callerIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(callerIdStr, out var callerId) && callerId == id)
            return BadRequest(new { message = "cannot delete your own account" });

        if (BreakGlassAccountPolicy.IsRecoveryCapable(user)
            && !await _db.Users.AsNoTracking().AnyAsync(other => other.Id != user.Id
                && other.Provider == AuthProvider.Local
                && other.Role == UserRole.Admin
                && other.IsActive
                && !other.IsTombstoned
                && other.IsBreakGlass
                && other.PasswordHash != null
                && other.PasswordHash != string.Empty, ct))
        {
            return BadRequest(new
            {
                message = "cannot delete the last active local break-glass administrator",
            });
        }

        // Enterprise identities are tombstoned instead of hard-deleted. This preserves
        // audit/FK history and prevents the same external subject from reappearing through
        // JIT provisioning on its next login.
        var snapshotUsername = user.Username;
        var snapshotRole = user.Role.ToString();

        user.IsActive = false;
        user.IsTombstoned = true;
        UserSessionInvalidation.BumpSecurityStamp(user);
        foreach (var executionId in await RevokeSessionsAndCancelExecutionsAsync(
                     id, "admin-user-deleted", ct))
            executionIdsToSignal.Add(executionId);
        await _db.SaveChangesAsync(ct);
        UserSessionInvalidation.InvalidateUserStateCache(_userStateCache, id);

        await _audit.LogAsync(AuditActions.UserDeleted, "User", id,
            AuditDetails.Json(("username", snapshotUsername), ("role", snapshotRole)), ct);

        return NoContent();
    }

    private async Task<IReadOnlyList<Guid>> RevokeSessionsAndCancelExecutionsAsync(
        Guid userId,
        string reason,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sessions = await _db.AuthSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var session in sessions)
            session.RevokedAt = now;

        return await ExternalExecutionCancellation.CancelAsync(
            _db,
            [userId],
            now,
            reason,
            "Execution cancelled because its effective principal was offboarded.",
            ct);
    }

    private async Task SignalCommittedExecutionCancellationsAsync(
        IReadOnlyCollection<Guid> executionIds,
        string reason)
    {
        if (_workflowEngine is null || executionIds.Count == 0) return;
        using var signalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await ExternalExecutionCancellation.SignalAfterCommitAsync(
                _workflowEngine, executionIds, reason, signalTimeout.Token, _logger);
        }
        catch (OperationCanceledException) when (signalTimeout.IsCancellationRequested)
        {
            _logger?.LogError(
                "Post-commit admin execution cancellation signal timed out for {Count} execution(s)",
                executionIds.Count);
        }
    }

    [HttpPost("{id:guid}/reactivate")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        var provider = await _db.Users.AsNoTracking()
            .Where(candidate => candidate.Id == id)
            .Select(candidate => (AuthProvider?)candidate.Provider)
            .SingleOrDefaultAsync(ct);
        if (provider is null) return NotFound();
        if (provider == AuthProvider.Local)
            return await ReactivateLocalAsync(id, ct);

        await using var localGate = await AdminAccountMutationGate.EnterLocalAsync(ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        IReadOnlyList<Guid> committedExecutionIds = [];
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            committedExecutionIds = [];
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await AdminAccountMutationGate.AcquireTransactionLockAsync(_db, ct);

            var user = await _db.Users.SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (user is null)
            {
                await transaction.RollbackAsync(ct);
                return (IActionResult)NotFound();
            }
            if (user.Provider == AuthProvider.Local)
            {
                await transaction.RollbackAsync(ct);
                return Conflict(new { message = "user provider changed; retry reactivation" });
            }
            if (!user.IsTombstoned)
            {
                await transaction.RollbackAsync(ct);
                return Conflict(new { message = "user is not tombstoned" });
            }

            user.IsTombstoned = false;
            user.IsActive = true;
            user.LastDirectorySyncAt = null;
            user.DirectorySyncStatus = "ReactivationReauthRequired";
            UserSessionInvalidation.BumpSecurityStamp(user);
            _db.DirectoryMemberships.RemoveRange(await _db.DirectoryMemberships
                .Where(membership => membership.UserId == id)
                .ToListAsync(ct));
            var executionIds = await RevokeSessionsAndCancelExecutionsAsync(
                id, "admin-external-reactivation-revalidation", ct);

            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(AuditActions.UserActivated, "User", id,
                AuditDetails.Json(
                    ("username", user.Username),
                    ("reactivatedFromTombstone", true),
                    ("externalRevalidationRequired", true)), ct);
            await transaction.CommitAsync(ct);
            committedExecutionIds = executionIds;
            return NoContent();
        });

        if (result is NoContentResult)
            UserSessionInvalidation.InvalidateUserStateCache(_userStateCache, id);
        await SignalCommittedExecutionCancellationsAsync(
            committedExecutionIds, "admin-external-reactivation-revalidation");
        return result;
    }

    private async Task<IActionResult> ReactivateLocalAsync(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.FindAsync([id], ct);
        if (user is null) return NotFound();
        if (!user.IsTombstoned) return Conflict(new { message = "user is not tombstoned" });

        user.IsTombstoned = false;
        user.IsActive = true;
        UserSessionInvalidation.BumpSecurityStamp(user);
        await _db.SaveChangesAsync(ct);
        UserSessionInvalidation.InvalidateUserStateCache(_userStateCache, id);
        await _audit.LogAsync(AuditActions.UserActivated, "User", id,
            AuditDetails.Json(("username", user.Username), ("reactivatedFromTombstone", true)), ct);
        return NoContent();
    }
}

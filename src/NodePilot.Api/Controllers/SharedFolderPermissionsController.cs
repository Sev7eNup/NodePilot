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
/// Grant/revoke/list folder-permissions for the org-level <see cref="SharedWorkflowFolder"/>
/// tree. V1 accepts <see cref="FolderPrincipalType.User"/> (PrincipalKey = User.Id-as-string)
/// and <see cref="FolderPrincipalType.Group"/> (PrincipalKey = AD-Group-SID) grants.
/// Role-typed grants remain reserved.
/// </summary>
[ApiController]
[Route("api/shared-workflow-folders/{folderId:guid}/permissions")]
[Authorize]
public class SharedFolderPermissionsController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IResourceAuthorizationService _authz;
    private readonly IMemoryCache? _cache;

    public SharedFolderPermissionsController(
        NodePilotDbContext db,
        IAuditWriter audit,
        IResourceAuthorizationService authz,
        IMemoryCache? cache = null)
    {
        _db = db;
        _audit = audit;
        _authz = authz;
        _cache = cache;
    }

    [HttpGet]
    public async Task<ActionResult<List<SharedFolderPermissionResponse>>> GetAll(Guid folderId, CancellationToken ct)
    {
        if (!await _authz.CanAccessFolderAsync(User, folderId, ResourceOp.Read, ct))
            return NotFound();
        // Only Admin-on-folder can SEE the permission list (otherwise a FolderViewer
        // could enumerate every user with access — info-leak about other principals).
        if (!await _authz.CanAccessFolderAsync(User, folderId, ResourceOp.Admin, ct))
            return new ObjectResult(new { message = "Insufficient folder permissions to view grants" })
            { StatusCode = StatusCodes.Status403Forbidden };

        var rows = await _db.SharedFolderPermissions.AsNoTracking()
            .Where(p => p.FolderId == folderId)
            .ToListAsync(ct);

        // Resolve display-names for User-type principals in one batch query. The
        // PrincipalKey for User-rows is Guid.ToString("D") — parse it back to look up
        // the user. For Group-rows there's no name resolution in V1; the UI shows the
        // raw SID, an AD-aware Group-name-lookup is V2.
        var userIds = rows
            .Where(p => p.PrincipalType == FolderPrincipalType.User
                     && Guid.TryParse(p.PrincipalKey, out _))
            .Select(p => Guid.Parse(p.PrincipalKey))
            .Distinct()
            .ToList();
        var nameById = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        var responses = rows.Select(p =>
        {
            string? display = null;
            if (p.PrincipalType == FolderPrincipalType.User
                && Guid.TryParse(p.PrincipalKey, out var userId))
            {
                display = nameById.GetValueOrDefault(userId);
            }
            return new SharedFolderPermissionResponse(
                p.Id, p.FolderId, p.PrincipalType, p.PrincipalKey, display,
                p.Role, p.GrantedAt, p.GrantedByUserId)
            {
                PrincipalAuthority = p.PrincipalType == FolderPrincipalType.Group
                    ? EffectiveGroupAuthority(p)
                    : null,
            };
        }).ToList();

        return Ok(responses);
    }

    [HttpPost]
    public async Task<ActionResult<SharedFolderPermissionResponse>> Grant(
        Guid folderId, GrantSharedFolderPermissionRequest req, CancellationToken ct)
    {
        if (!await _authz.CanAccessFolderAsync(User, folderId, ResourceOp.Read, ct))
            return NotFound();
        if (!await _authz.CanAccessFolderAsync(User, folderId, ResourceOp.Admin, ct))
            return new ObjectResult(new { message = "Insufficient folder permissions" })
            { StatusCode = StatusCodes.Status403Forbidden };

        // V1 accepts User and Group. Role-typed grants remain reserved.
        if (req.PrincipalType == FolderPrincipalType.Role)
            return BadRequest(new { message = "PrincipalType=Role is reserved." });

        if (string.IsNullOrWhiteSpace(req.PrincipalKey))
            return BadRequest(new { message = "PrincipalKey is required." });

        // Validate PrincipalKey format per type AND normalise to the canonical form the
        // authorization path compares against. Without normalisation, a User-grant created
        // via the API with a mixed-case Guid string is stored verbatim, while
        // ResourceAuthorizationService computes user-key as `userId.ToString("D")` (always
        // lowercase). On a case-sensitive collation (Postgres default) the grant then never
        // matches and the user silently has no access.
        string principalKey;
        string principalAuthority;
        if (req.PrincipalType == FolderPrincipalType.User)
        {
            if (!Guid.TryParse(req.PrincipalKey, out var userId))
                return BadRequest(new { message = "For PrincipalType=User, PrincipalKey must be a Guid string." });
            var userExists = await _db.Users.AnyAsync(u => u.Id == userId, ct);
            if (!userExists) return BadRequest(new { message = "User not found." });
            principalKey = userId.ToString("D"); // canonical lowercase Guid form
            principalAuthority = string.Empty;
        }
        else // Group
        {
            principalAuthority = string.IsNullOrWhiteSpace(req.PrincipalAuthority)
                ? ExternalIdentity.ActiveDirectoryAuthority
                : req.PrincipalAuthority.Trim();
            if (principalAuthority.Length > 512)
                return BadRequest(new { message = "Group PrincipalAuthority must not exceed 512 characters." });
            if (req.PrincipalKey.Length > 256)
                return BadRequest(new { message = "Group PrincipalKey must not exceed 256 characters." });
            if (principalAuthority == ExternalIdentity.ActiveDirectoryAuthority)
            {
            // SID format check: S-1-<auth>-<sub-auth>+. Permissive — exact AD-validity
            // is impossible without an LDAP roundtrip and a syntactically-valid SID
            // for an unknown group is fine (will simply never match a user's group set).
            // Case-insensitive because operators paste SIDs in either case; we normalise
            // to uppercase on store below.
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    req.PrincipalKey, @"^S-\d+-\d+(-\d+)+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(1)))
                return BadRequest(new { message = "For PrincipalType=Group, PrincipalKey must be a Windows SID (e.g. S-1-5-21-...)." });
            // AD canonical form is uppercase; resolver compares case-insensitively but we
            // normalise on write so the audit log shows the canonical SID and exact-match
            // queries elsewhere don't have to depend on collation.
                principalKey = req.PrincipalKey.ToUpperInvariant();
            }
            else
            {
                if (!NodePilot.Api.Security.Oidc.OidcIdentityMapper.IsValidIssuer(principalAuthority))
                    return BadRequest(new { message = "OIDC/SCIM group PrincipalAuthority must be an absolute HTTPS issuer URL." });
                principalKey = req.PrincipalKey;
            }
        }

        var folderExists = await _db.SharedWorkflowFolders.AnyAsync(f => f.Id == folderId, ct);
        if (!folderExists) return NotFound();

        // Re-grant for the same (folder, principal) updates the role rather than stacks.
        var existing = await _db.SharedFolderPermissions
            .FirstOrDefaultAsync(p => p.FolderId == folderId
                                   && p.PrincipalType == req.PrincipalType
                                   && (p.PrincipalAuthority == principalAuthority
                                       || (principalAuthority == ExternalIdentity.ActiveDirectoryAuthority
                                           && p.PrincipalAuthority == ""))
                                   && p.PrincipalKey == principalKey, ct);
        SharedFolderPermission perm;
        bool isNew = existing is null;
        if (existing is not null)
        {
            var oldRole = existing.Role;
            existing.Role = req.Role;
            existing.PrincipalAuthority = principalAuthority;
            existing.GrantedAt = DateTime.UtcNow;
            existing.GrantedByUserId = this.GetCurrentUserId();
            perm = existing;
            if (req.Role < oldRole)
                await InvalidateAffectedSessionsAsync(existing, ct);
        }
        else
        {
            perm = new SharedFolderPermission
            {
                Id = Guid.NewGuid(),
                FolderId = folderId,
                PrincipalType = req.PrincipalType,
                PrincipalAuthority = principalAuthority,
                PrincipalKey = principalKey,
                Role = req.Role,
                GrantedAt = DateTime.UtcNow,
                GrantedByUserId = this.GetCurrentUserId(),
            };
            _db.SharedFolderPermissions.Add(perm);
        }
        await _db.SaveChangesAsync(ct);
        _authz.InvalidateAll();

        await _audit.LogAsync(
            isNew ? AuditActions.FolderPermissionGranted : AuditActions.FolderPermissionUpdated,
            "SharedWorkflowFolder", folderId,
            AuditDetails.Json(
                ("principalType", req.PrincipalType.ToString()),
                ("principalAuthority", principalAuthority),
                ("principalKey", principalKey),
                ("role", req.Role.ToString())), ct);

        string? display = null;
        if (perm.PrincipalType == FolderPrincipalType.User
            && Guid.TryParse(perm.PrincipalKey, out var resolvedUserId))
        {
            display = await _db.Users.AsNoTracking()
                .Where(u => u.Id == resolvedUserId).Select(u => u.Username).FirstOrDefaultAsync(ct);
        }
        return Ok(new SharedFolderPermissionResponse(
            perm.Id, perm.FolderId, perm.PrincipalType, perm.PrincipalKey, display,
            perm.Role, perm.GrantedAt, perm.GrantedByUserId)
        {
            PrincipalAuthority = perm.PrincipalType == FolderPrincipalType.Group
                ? EffectiveGroupAuthority(perm)
                : null,
        });
    }

    [HttpPut("{permissionId:guid}")]
    public async Task<IActionResult> Update(Guid folderId, Guid permissionId,
        UpdateSharedFolderPermissionRequest req, CancellationToken ct)
    {
        if (!await _authz.CanAccessFolderAsync(User, folderId, ResourceOp.Admin, ct))
            return new ObjectResult(new { message = "Insufficient folder permissions" })
            { StatusCode = StatusCodes.Status403Forbidden };

        var perm = await _db.SharedFolderPermissions
            .FirstOrDefaultAsync(p => p.Id == permissionId && p.FolderId == folderId, ct);
        if (perm is null) return NotFound();

        var oldRole = perm.Role;
        perm.Role = req.Role;
        perm.GrantedAt = DateTime.UtcNow;
        perm.GrantedByUserId = this.GetCurrentUserId();
        if (req.Role < oldRole)
            await InvalidateAffectedSessionsAsync(perm, ct);
        await _db.SaveChangesAsync(ct);
        _authz.InvalidateAll();

        await _audit.LogAsync(AuditActions.FolderPermissionUpdated, "SharedWorkflowFolder", folderId,
            AuditDetails.Json(
                ("principalKey", perm.PrincipalKey),
                ("principalAuthority", perm.PrincipalType == FolderPrincipalType.Group
                    ? EffectiveGroupAuthority(perm) : null),
                ("oldRole", oldRole.ToString()),
                ("newRole", req.Role.ToString())), ct);
        return NoContent();
    }

    [HttpDelete("{permissionId:guid}")]
    public async Task<IActionResult> Revoke(Guid folderId, Guid permissionId, CancellationToken ct)
    {
        if (!await _authz.CanAccessFolderAsync(User, folderId, ResourceOp.Admin, ct))
            return new ObjectResult(new { message = "Insufficient folder permissions" })
            { StatusCode = StatusCodes.Status403Forbidden };

        var perm = await _db.SharedFolderPermissions
            .FirstOrDefaultAsync(p => p.Id == permissionId && p.FolderId == folderId, ct);
        if (perm is null) return NotFound();

        _db.SharedFolderPermissions.Remove(perm);
        await InvalidateAffectedSessionsAsync(perm, ct);
        await _db.SaveChangesAsync(ct);
        _authz.InvalidateAll();

        await _audit.LogAsync(AuditActions.FolderPermissionRevoked, "SharedWorkflowFolder", folderId,
            AuditDetails.Json(
                ("principalType", perm.PrincipalType.ToString()),
                ("principalAuthority", perm.PrincipalType == FolderPrincipalType.Group
                    ? EffectiveGroupAuthority(perm) : null),
                ("principalKey", perm.PrincipalKey),
                ("role", perm.Role.ToString())), ct);
        return NoContent();
    }

    /// <summary>
    /// Folder-grant revocation is a session-security mutation. Bump the same stamp used
    /// for role changes so REST tokens are rejected and the SignalR sweeper disconnects
    /// connections whose captured folder/group claims are now stale.
    /// </summary>
    private async Task InvalidateAffectedSessionsAsync(
        SharedFolderPermission permission, CancellationToken ct)
    {
        List<User> affected;
        if (permission.PrincipalType == FolderPrincipalType.User
            && Guid.TryParse(permission.PrincipalKey, out var userId))
        {
            affected = await _db.Users.Where(u => u.Id == userId).ToListAsync(ct);
        }
        else if (permission.PrincipalType == FolderPrincipalType.Group)
        {
            var authority = EffectiveGroupAuthority(permission);
            var affectedIds = await _db.DirectoryMemberships.AsNoTracking()
                .Where(membership => membership.Authority == authority
                                  && membership.GroupKey == permission.PrincipalKey)
                .Select(membership => membership.UserId)
                .Distinct()
                .ToListAsync(ct);
            affected = await _db.Users
                .Where(user => affectedIds.Contains(user.Id))
                .ToListAsync(ct);
        }
        else
        {
            affected = [];
        }

        foreach (var user in affected)
        {
            UserSessionInvalidation.BumpSecurityStamp(user);
            if (_cache is not null)
                UserSessionInvalidation.InvalidateUserStateCache(_cache, user.Id);
        }
    }

    private static string EffectiveGroupAuthority(SharedFolderPermission permission) =>
        string.IsNullOrWhiteSpace(permission.PrincipalAuthority)
            ? ExternalIdentity.ActiveDirectoryAuthority
            : permission.PrincipalAuthority;
}

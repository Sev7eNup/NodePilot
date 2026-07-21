using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodePilot.Api.Audit;
using NodePilot.Api.Security.Oidc;
using NodePilot.Core.Audit;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/admin/scim-groups")]
public sealed class AdminScimGroupsController(
    NodePilotDbContext db,
    IAuditWriter audit,
    IOptions<EnterpriseOidcOptions> oidcOptions) : ControllerBase
{
    [HttpGet("tombstones")]
    public async Task<IActionResult> ListTombstones(CancellationToken ct)
    {
        var authority = oidcOptions.Value.Authority;
        var groups = await db.ScimGroups.AsNoTracking()
            .Where(group => group.Authority == authority && group.IsTombstoned)
            .OrderBy(group => group.DisplayName)
            .Select(group => new
            {
                group.Id,
                group.ExternalId,
                group.DisplayName,
                group.UpdatedAt,
            })
            .ToListAsync(ct);
        return Ok(groups);
    }

    [HttpPost("{id:guid}/reactivate")]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        var authority = oidcOptions.Value.Authority;
        var group = await db.ScimGroups.SingleOrDefaultAsync(
            candidate => candidate.Id == id && candidate.Authority == authority, ct);
        if (group is null) return NotFound();
        if (!group.IsTombstoned) return Conflict(new { message = "SCIM group is not tombstoned" });

        group.IsTombstoned = false;
        group.IsActive = true;
        group.UpdatedAt = DateTime.UtcNow;
        await audit.LogAsync(
            AuditActions.ScimGroupReactivated,
            "ScimGroup",
            group.Id,
            AuditDetails.Json(
                ("externalId", group.ExternalId),
                ("displayName", group.DisplayName)),
            ct);
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
/// CRUD + move endpoints for the org-level <see cref="SharedWorkflowFolder"/> tree.
/// </summary>
[ApiController]
[Route("api/shared-workflow-folders")]
[Authorize]
public class SharedWorkflowFoldersController : ControllerBase
{
    private readonly NodePilotDbContext _db;
    private readonly IAuditWriter _audit;
    private readonly IResourceAuthorizationService _authz;

    public SharedWorkflowFoldersController(NodePilotDbContext db, IAuditWriter audit, IResourceAuthorizationService authz)
    {
        _db = db;
        _audit = audit;
        _authz = authz;
    }

    /// <summary>Returns the full folder tree the caller can read, plus per-folder
    /// capabilities for client-side affordance gating. Folders the caller can't read
    /// are filtered out — including their entire descendant subtree.</summary>
    [HttpGet]
    public async Task<ActionResult<List<SharedFolderResponse>>> GetAll(CancellationToken ct)
    {
        var folders = await _db.SharedWorkflowFolders.AsNoTracking()
            .OrderBy(f => f.Depth).ThenBy(f => f.Name)
            .ToListAsync(ct);

        // Workflow counts per folder for the tree display.
        var counts = await _db.Workflows.AsNoTracking()
            .GroupBy(w => w.FolderId)
            .Select(g => new { FolderId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FolderId, x => x.Count, ct);

        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);

        var results = new List<SharedFolderResponse>();
        foreach (var f in folders)
        {
            if (!accessible.IsUnrestricted && !accessible.FolderIds.Contains(f.Id))
                continue;

            var caps = await _authz.GetFolderCapabilitiesAsync(User, f.Id, ct);
            results.Add(new SharedFolderResponse(
                f.Id, f.ParentFolderId, f.Name, f.Path, f.Depth,
                f.CreatedAt, f.CreatedByUserId,
                counts.GetValueOrDefault(f.Id, 0),
                new SharedFolderCapabilities(caps.CanRead, caps.CanRun, caps.CanEdit, caps.CanAdmin)));
        }
        return Ok(results);
    }

    [HttpPost]
    public async Task<ActionResult<SharedFolderResponse>> Create(CreateSharedFolderRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Name is required" });
        if (req.Name.Length > 120)
            return BadRequest(new { message = "Name max length is 120 characters" });

        // Parent defaults to Root. Caller needs FolderEditor on the parent (creating a
        // child is a parent-edit). Root has the global Admin + bootstrap-default
        // permissions; non-Admin users with FolderEditor on Root may create top-level
        // folders.
        var parentId = req.ParentFolderId ?? SharedWorkflowFolder.RootFolderId;
        var parent = await _db.SharedWorkflowFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == parentId, ct);
        if (parent is null) return BadRequest(new { message = "Parent folder not found" });
        if (!await _authz.CanAccessFolderAsync(User, parentId, ResourceOp.Edit, ct))
            return new ObjectResult(new { message = "Insufficient folder permissions on the parent folder" })
            { StatusCode = StatusCodes.Status403Forbidden };

        if (parent.Depth + 1 > SharedWorkflowFolder.MaxDepth)
            return BadRequest(new { message = $"Folder depth limit ({SharedWorkflowFolder.MaxDepth}) would be exceeded" });

        // Sibling name uniqueness is also enforced at the DB level via the unique index;
        // the explicit check produces a cleaner 400 than translating the DbUpdateException.
        var siblingExists = await _db.SharedWorkflowFolders
            .AnyAsync(f => f.ParentFolderId == parentId && f.Name == req.Name, ct);
        if (siblingExists)
            return Conflict(new { message = $"A folder named '{req.Name}' already exists in the parent" });

        var folder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(),
            ParentFolderId = parentId,
            Name = req.Name,
            Path = parent.Path == "/" ? $"/{req.Name}" : $"{parent.Path}/{req.Name}",
            Depth = parent.Depth + 1,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = this.GetCurrentUserId(),
        };
        _db.SharedWorkflowFolders.Add(folder);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.FolderCreated, "SharedWorkflowFolder", folder.Id,
            AuditDetails.Json(("name", folder.Name), ("path", folder.Path), ("parentId", parentId)), ct);

        // Drop the per-request authz cache: it was loaded before the new folder existed, so
        // the upcoming capability lookup would walk an ancestry chain that doesn't include
        // this row and return None — even though the new folder inherits Edit from its parent.
        _authz.InvalidateAll();
        var caps = await _authz.GetFolderCapabilitiesAsync(User, folder.Id, ct);
        return CreatedAtAction(nameof(GetAll), null,
            new SharedFolderResponse(folder.Id, folder.ParentFolderId, folder.Name, folder.Path,
                folder.Depth, folder.CreatedAt, folder.CreatedByUserId, 0,
                new SharedFolderCapabilities(caps.CanRead, caps.CanRun, caps.CanEdit, caps.CanAdmin)));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Rename(Guid id, UpdateSharedFolderRequest req, CancellationToken ct)
    {
        if (id == SharedWorkflowFolder.RootFolderId)
            return BadRequest(new { message = "Root folder cannot be renamed" });
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 120)
            return BadRequest(new { message = "Name is required and max 120 chars" });

        var folder = await _db.SharedWorkflowFolders.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (folder is null) return NotFound();
        if (!await _authz.CanAccessFolderAsync(User, id, ResourceOp.Read, ct)) return NotFound();
        if (!await _authz.CanAccessFolderAsync(User, id, ResourceOp.Edit, ct))
            return new ObjectResult(new { message = "Insufficient folder permissions" })
            { StatusCode = StatusCodes.Status403Forbidden };

        // Sibling-name uniqueness check before save.
        var clash = await _db.SharedWorkflowFolders
            .AnyAsync(f => f.ParentFolderId == folder.ParentFolderId && f.Name == req.Name && f.Id != id, ct);
        if (clash) return Conflict(new { message = $"A sibling folder named '{req.Name}' already exists" });

        var oldPath = folder.Path;
        folder.Name = req.Name;
        // Recompute Path for this folder + every descendant. Done in-memory + bulk update
        // because folder rename is admin-rare; no need for a tuned SQL pass.
        var allFolders = await _db.SharedWorkflowFolders.ToListAsync(ct);
        RecomputePathsRecursive(folder, allFolders);

        await _db.SaveChangesAsync(ct);
        _authz.InvalidateAll();
        await _audit.LogAsync(AuditActions.FolderUpdated, "SharedWorkflowFolder", folder.Id,
            AuditDetails.Json(("oldPath", oldPath), ("newPath", folder.Path)), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/move")]
    public async Task<IActionResult> Move(Guid id, MoveSharedFolderRequest req, CancellationToken ct)
    {
        if (id == SharedWorkflowFolder.RootFolderId)
            return BadRequest(new { message = "Root folder cannot be moved" });

        var folder = await _db.SharedWorkflowFolders.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (folder is null) return NotFound();
        if (!await _authz.CanAccessFolderAsync(User, id, ResourceOp.Read, ct)) return NotFound();
        if (!await _authz.CanAccessFolderAsync(User, id, ResourceOp.Edit, ct))
            return new ObjectResult(new { message = "Insufficient permissions on source folder" })
            { StatusCode = StatusCodes.Status403Forbidden };

        var newParentId = req.NewParentFolderId ?? SharedWorkflowFolder.RootFolderId;
        if (newParentId == folder.Id)
            return BadRequest(new { message = "A folder cannot be its own parent" });
        if (!await _authz.CanAccessFolderAsync(User, newParentId, ResourceOp.Edit, ct))
            return new ObjectResult(new { message = "Insufficient permissions on destination folder" })
            { StatusCode = StatusCodes.Status403Forbidden };

        var newParent = await _db.SharedWorkflowFolders.FirstOrDefaultAsync(f => f.Id == newParentId, ct);
        if (newParent is null) return BadRequest(new { message = "Destination folder not found" });

        // Cycle check: the destination must not be a descendant of the moved folder.
        var allFolders = await _db.SharedWorkflowFolders.ToListAsync(ct);
        if (IsDescendant(allFolders, ancestor: folder.Id, candidate: newParentId))
            return BadRequest(new { message = "Cannot move a folder into its own descendant" });
        if (newParent.Depth + 1 + DescendantMaxDepth(allFolders, folder.Id) > SharedWorkflowFolder.MaxDepth)
            return BadRequest(new { message = $"Move would exceed folder depth limit ({SharedWorkflowFolder.MaxDepth})" });

        // Moving a subtree changes the inherited permissions of every workflow below it.
        // Do not let a co-editor move an in-flight edit session out from under its owner.
        // The caller's own locks are safe: Edit is required on both the source and the new
        // parent, so the owner retains an authorised path after the move.
        var subtreeFolderIds = DescendantFolderIds(allFolders, folder.Id);
        var callerId = this.GetCurrentUserId();
        var containsForeignLock = await _db.Workflows.AsNoTracking()
            .AnyAsync(w => subtreeFolderIds.Contains(w.FolderId)
                           && w.CheckedOutByUserId != null
                           && w.CheckedOutByUserId != callerId, ct);
        if (containsForeignLock)
        {
            return new ObjectResult(new
            {
                message = "Folder subtree contains a workflow checked out by another user and cannot be moved.",
            }) { StatusCode = StatusCodes.Status423Locked };
        }

        // Sibling name uniqueness in the new location.
        if (allFolders.Any(f => f.ParentFolderId == newParentId && f.Name == folder.Name && f.Id != id))
            return Conflict(new { message = $"A sibling named '{folder.Name}' already exists in the destination" });

        var oldPath = folder.Path;
        folder.ParentFolderId = newParentId;
        folder.Depth = newParent.Depth + 1;
        RecomputePathsRecursive(folder, allFolders);

        await _db.SaveChangesAsync(ct);
        _authz.InvalidateAll();
        await _audit.LogAsync(AuditActions.FolderMoved, "SharedWorkflowFolder", folder.Id,
            AuditDetails.Json(("oldPath", oldPath), ("newPath", folder.Path),
                ("newParentId", newParentId)), ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (id == SharedWorkflowFolder.RootFolderId)
            return BadRequest(new { message = "Root folder cannot be deleted" });

        var folder = await _db.SharedWorkflowFolders.FirstOrDefaultAsync(f => f.Id == id, ct);
        if (folder is null) return NotFound();
        if (!await _authz.CanAccessFolderAsync(User, id, ResourceOp.Read, ct)) return NotFound();
        if (!await _authz.CanAccessFolderAsync(User, id, ResourceOp.Edit, ct))
            return new ObjectResult(new { message = "Insufficient folder permissions" })
            { StatusCode = StatusCodes.Status403Forbidden };

        // Delete is allowed only if the folder is empty (no workflows, no sub-folders).
        // Forces the operator to move/delete contents explicitly — better than
        // accidentally cascading workflows to nowhere.
        var hasChildren = await _db.SharedWorkflowFolders.AnyAsync(f => f.ParentFolderId == id, ct);
        var hasWorkflows = await _db.Workflows.AnyAsync(w => w.FolderId == id, ct);
        if (hasChildren || hasWorkflows)
            return Conflict(new { message = "Folder is not empty — move or delete sub-folders and workflows first" });

        var path = folder.Path;
        _db.SharedWorkflowFolders.Remove(folder);
        await _db.SaveChangesAsync(ct);
        _authz.InvalidateAll();
        await _audit.LogAsync(AuditActions.FolderDeleted, "SharedWorkflowFolder", id,
            AuditDetails.Json(("path", path)), ct);
        return NoContent();
    }

    /// <summary>Move a workflow into a different shared folder. Requires FolderEditor on
    /// both source and destination.</summary>
    [HttpPost("/api/workflows/{workflowId:guid}/move-folder")]
    public async Task<IActionResult> MoveWorkflow(Guid workflowId, MoveWorkflowToFolderRequest req, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return NotFound();
        if (!await _authz.CanAccessWorkflowAsync(User, workflow.FolderId, ResourceOp.Read, ct)) return NotFound();
        if (!await _authz.CanAccessWorkflowAsync(User, workflow.FolderId, ResourceOp.Edit, ct))
            return new ObjectResult(new { message = "Insufficient permissions on source folder" })
            { StatusCode = StatusCodes.Status403Forbidden };
        if (!await _authz.CanAccessFolderAsync(User, req.TargetFolderId, ResourceOp.Edit, ct))
            return new ObjectResult(new { message = "Insufficient permissions on destination folder" })
            { StatusCode = StatusCodes.Status403Forbidden };

        var callerId = this.GetCurrentUserId();
        if (workflow.CheckedOutByUserId is not null && workflow.CheckedOutByUserId != callerId)
        {
            return new ObjectResult(new
            {
                message = "Workflow is checked out by another user and cannot be moved.",
                lockedByUserId = workflow.CheckedOutByUserId,
                lockedAt = workflow.CheckedOutAt,
            }) { StatusCode = StatusCodes.Status423Locked };
        }

        var dest = await _db.SharedWorkflowFolders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == req.TargetFolderId, ct);
        if (dest is null) return BadRequest(new { message = "Destination folder not found" });

        var oldFolderId = workflow.FolderId;
        var updatedAt = DateTime.UtcNow;
        var updatedBy = this.GetCurrentUsername();
        var moved = await _db.Workflows
            .Where(w => w.Id == workflowId
                        && w.FolderId == oldFolderId
                        && w.Version == workflow.Version
                        && w.CheckedOutByUserId == workflow.CheckedOutByUserId
                        && w.CheckedOutAt == workflow.CheckedOutAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.FolderId, req.TargetFolderId)
                .SetProperty(w => w.UpdatedAt, updatedAt)
                .SetProperty(w => w.UpdatedBy, updatedBy), ct);

        if (moved == 0)
        {
            var current = await _db.Workflows.AsNoTracking()
                .FirstOrDefaultAsync(w => w.Id == workflowId, ct);
            if (current is null) return NotFound();
            if (!await _authz.CanAccessWorkflowAsync(User, current.FolderId, ResourceOp.Read, ct))
                return NotFound();
            if (!await _authz.CanAccessWorkflowAsync(User, current.FolderId, ResourceOp.Edit, ct))
            {
                return new ObjectResult(new { message = "Insufficient permissions on the workflow's current folder" })
                { StatusCode = StatusCodes.Status403Forbidden };
            }
            if (current.CheckedOutByUserId is not null && current.CheckedOutByUserId != callerId)
            {
                return new ObjectResult(new
                {
                    message = "Workflow is checked out by another user and cannot be moved.",
                    lockedByUserId = current.CheckedOutByUserId,
                    lockedAt = current.CheckedOutAt,
                }) { StatusCode = StatusCodes.Status423Locked };
            }

            return Conflict(new
            {
                code = "workflow_move_conflict",
                message = "Workflow changed concurrently. Reload the workflow and retry the move.",
            });
        }

        await _audit.LogAsync(AuditActions.WorkflowMoved, "Workflow", workflow.Id,
            AuditDetails.Json(("fromFolderId", oldFolderId), ("toFolderId", req.TargetFolderId),
                ("name", workflow.Name)), ct);
        return NoContent();
    }

    private static void RecomputePathsRecursive(SharedWorkflowFolder folder, List<SharedWorkflowFolder> all)
    {
        var byParent = all.GroupBy(f => f.ParentFolderId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());
        var stack = new Stack<SharedWorkflowFolder>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            // Recompute current.Path from parent.Path + Name.
            if (current.ParentFolderId is null)
                current.Path = "/";
            else
            {
                var parent = all.FirstOrDefault(f => f.Id == current.ParentFolderId.Value);
                if (parent is not null)
                {
                    current.Path = parent.Path == "/" ? $"/{current.Name}" : $"{parent.Path}/{current.Name}";
                    current.Depth = parent.Depth + 1;
                }
            }
            if (byParent.TryGetValue(current.Id, out var children))
                foreach (var c in children) stack.Push(c);
        }
    }

    private static bool IsDescendant(List<SharedWorkflowFolder> all, Guid ancestor, Guid candidate)
    {
        var byId = all.ToDictionary(f => f.Id);
        var current = candidate;
        var depth = 0;
        while (depth <= SharedWorkflowFolder.MaxDepth + 1 && byId.TryGetValue(current, out var folder))
        {
            if (folder.ParentFolderId is null) return false;
            if (folder.ParentFolderId.Value == ancestor) return true;
            current = folder.ParentFolderId.Value;
            depth++;
        }
        return false;
    }

    private static int DescendantMaxDepth(List<SharedWorkflowFolder> all, Guid rootId)
    {
        var byParent = all.GroupBy(f => f.ParentFolderId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());
        var max = 0;
        var stack = new Stack<(Guid id, int depth)>();
        stack.Push((rootId, 0));
        while (stack.Count > 0)
        {
            var (id, d) = stack.Pop();
            if (d > max) max = d;
            if (byParent.TryGetValue(id, out var children))
                foreach (var c in children) stack.Push((c.Id, d + 1));
        }
        return max;
    }

    private static HashSet<Guid> DescendantFolderIds(List<SharedWorkflowFolder> all, Guid rootId)
    {
        var byParent = all.GroupBy(f => f.ParentFolderId)
            .ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());
        var result = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(rootId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!result.Add(current)) continue;
            if (byParent.TryGetValue(current, out var children))
                foreach (var child in children) stack.Push(child.Id);
        }
        return result;
    }
}

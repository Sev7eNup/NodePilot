using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Core.Audit;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Api.Controllers;

/// <summary>
/// CRUD + move endpoints for the organizational <see cref="GlobalVariableFolder"/> tree. Lets
/// operators group global variables by topic (databases, API keys, environments …) without the
/// folder ever changing how <c>{{globals.NAME}}</c> resolves — names stay globally unique and
/// folders are cosmetic.
///
/// <para>Read is Admin/Operator (matches the variable list); all mutations are Admin-only, like
/// the rest of global-variable management. There is deliberately no per-folder RBAC.</para>
/// </summary>
[ApiController]
[Route("api/global-variable-folders")]
[Authorize(Roles = "Admin,Operator")]
public class GlobalVariableFoldersController : ControllerBase
{
    private readonly IGlobalVariableFolderStore _store;
    private readonly IAuditWriter _audit;

    public GlobalVariableFoldersController(IGlobalVariableFolderStore store, IAuditWriter audit)
    {
        _store = store;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<List<GlobalVariableFolderResponse>>> GetAll(CancellationToken ct)
    {
        var rows = await _store.GetAllAsync(ct);
        return Ok(rows.Select(Project).ToList());
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<GlobalVariableFolderResponse>> Create(CreateGlobalVariableFolderRequest req, CancellationToken ct)
    {
        try
        {
            var folder = await _store.CreateAsync(req.ParentFolderId, req.Name, this.GetCurrentUserId(), ct);
            await _audit.LogAsync(AuditActions.GlobalVariableFolderCreated, "GlobalVariableFolder", folder.Id,
                AuditDetails.Json(("name", folder.Name), ("path", folder.Path), ("parentId", folder.ParentFolderId)), ct);
            return CreatedAtAction(nameof(GetAll), null, Project(new GlobalVariableFolderWithCount(folder, 0)));
        }
        catch (Exception ex) { return MapError(ex); }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Rename(Guid id, UpdateGlobalVariableFolderRequest req, CancellationToken ct)
    {
        try
        {
            var folder = await _store.RenameAsync(id, req.Name, ct);
            await _audit.LogAsync(AuditActions.GlobalVariableFolderUpdated, "GlobalVariableFolder", folder.Id,
                AuditDetails.Json(("newPath", folder.Path)), ct);
            return NoContent();
        }
        catch (Exception ex) { return MapError(ex); }
    }

    [HttpPost("{id:guid}/move")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Move(Guid id, MoveGlobalVariableFolderRequest req, CancellationToken ct)
    {
        try
        {
            var folder = await _store.MoveAsync(id, req.NewParentFolderId, ct);
            await _audit.LogAsync(AuditActions.GlobalVariableFolderMoved, "GlobalVariableFolder", folder.Id,
                AuditDetails.Json(("newPath", folder.Path), ("newParentId", folder.ParentFolderId)), ct);
            return NoContent();
        }
        catch (Exception ex) { return MapError(ex); }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try
        {
            await _store.DeleteAsync(id, ct);
            await _audit.LogAsync(AuditActions.GlobalVariableFolderDeleted, "GlobalVariableFolder", id, null, ct);
            return NoContent();
        }
        catch (Exception ex) { return MapError(ex); }
    }

    // Typed store exceptions → HTTP status. KeyNotFound → 404, conflict → 409, the rest
    // (depth cap, cycle, root-protected, bad parent, bad name) → 400 with the message.
    private ActionResult MapError(Exception ex) => ex switch
    {
        KeyNotFoundException => NotFound(),
        GlobalVariableFolderConflictException => Conflict(new { message = ex.Message }),
        ArgumentException or InvalidOperationException => BadRequest(new { message = ex.Message }),
        _ => throw ex,
    };

    private static GlobalVariableFolderResponse Project(GlobalVariableFolderWithCount f)
        => new(f.Folder.Id, f.Folder.ParentFolderId, f.Folder.Name, f.Folder.Path, f.Folder.Depth,
               f.Folder.CreatedAt, f.Folder.CreatedByUserId, f.VariableCount);
}

using Microsoft.AspNetCore.Mvc;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Api.Security;

/// <summary>
/// Shared folder-RBAC gate for workflow-/folder-shaped endpoints — the single implementation
/// behind <c>WorkflowsControllerBase.RequireWorkflowAccessAsync</c> and every controller that
/// doesn't inherit that base (<c>ExecutionsController</c>, <c>AiChatController</c>,
/// <c>WorkflowTelemetryController</c>, <c>SharedWorkflowFoldersController</c>,
/// <c>SharedFolderPermissionsController</c>, <c>ExecutionDebugController</c>). Centralizing the
/// gate keeps the read-mask-then-operation sequence consistent across controllers.
///
/// <para>Semantics: returns <c>null</c> when access is permitted (caller continues the
/// action). Returns 404 when the caller cannot even read the folder — masking existence so
/// id-probing via a 403/404 differential is blocked. Returns 403 when the caller can read
/// but lacks the requested op.</para>
/// </summary>
public static class ResourceAuthorizationGateExtensions
{
    /// <summary>
    /// Workflow-shaped gate. Returns <see cref="ActionResult"/> (not <c>IActionResult</c>)
    /// so the result implicitly converts to <c>ActionResult&lt;TValue&gt;</c>-typed returns
    /// at every call site.
    /// </summary>
    public static async Task<ActionResult?> RequireWorkflowAccessAsync(
        this ControllerBase controller,
        IResourceAuthorizationService authz,
        Workflow workflow,
        ResourceOp op,
        CancellationToken ct,
        string? forbiddenMessage = null)
    {
        var canRead = await authz.CanAccessWorkflowAsync(controller.User, workflow.FolderId, ResourceOp.Read, ct);
        if (!canRead) return controller.NotFound();
        if (op == ResourceOp.Read) return null;

        var canDoOp = await authz.CanAccessWorkflowAsync(controller.User, workflow.FolderId, op, ct);
        if (!canDoOp) return InsufficientFolderPermissions(forbiddenMessage);
        return null;
    }

    /// <summary>Folder-shaped variant of <see cref="RequireWorkflowAccessAsync"/>.</summary>
    public static async Task<ActionResult?> RequireFolderAccessAsync(
        this ControllerBase controller,
        IResourceAuthorizationService authz,
        Guid folderId,
        ResourceOp op,
        CancellationToken ct,
        string? forbiddenMessage = null)
    {
        var canRead = await authz.CanAccessFolderAsync(controller.User, folderId, ResourceOp.Read, ct);
        if (!canRead) return controller.NotFound();
        if (op == ResourceOp.Read) return null;

        var canDoOp = await authz.CanAccessFolderAsync(controller.User, folderId, op, ct);
        if (!canDoOp) return InsufficientFolderPermissions(forbiddenMessage);
        return null;
    }

    /// <summary>
    /// <paramref name="message"/> lets multi-folder operations (move: source vs destination)
    /// keep their differentiated 403 text while still flowing through the one gate — pass
    /// nothing for the canonical body.
    /// </summary>
    private static ObjectResult InsufficientFolderPermissions(string? message = null)
        => new(new { message = message ?? "Insufficient folder permissions for this operation" })
        { StatusCode = StatusCodes.Status403Forbidden };
}

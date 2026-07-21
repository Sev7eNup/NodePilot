using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Admin/Operator-managed pool of constants available to every workflow via
/// <c>{{globals.NAME}}</c>. Think SCOrch "Variables": shared config (API endpoints,
/// environment tags, third-party tokens) lifted out of individual workflow definitions
/// so they can be rotated centrally.
///
/// <para>
/// Read access is Admin/Operator — Viewer roles intentionally cannot list globals
/// because a secret Variable's name often leaks its nature ("STRIPE_PROD_KEY").
/// Secret values are never returned; the response shape masks them with <c>"***"</c>
/// regardless of role.
/// </para>
/// </summary>
[ApiController]
[Route("api/global-variables")]
[Authorize(Roles = "Admin,Operator")]
public class GlobalVariablesController : ControllerBase
{
    // Strict name grammar: letters, digits, underscore, hyphen. No dots (would collide with
    // the {{globals.X.Y}} template grammar) and no whitespace (template-lookup parses on
    // whitespace). Max length matches the DB column.
    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9_\-]{1,100}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private readonly IGlobalVariableStore _store;
    private readonly IGlobalVariableFolderStore _folders;
    private readonly IAuditWriter _audit;

    public GlobalVariablesController(IGlobalVariableStore store, IGlobalVariableFolderStore folders, IAuditWriter audit)
    {
        _store = store;
        _folders = folders;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<List<GlobalVariableResponse>>> GetAll(CancellationToken ct)
    {
        var rows = await _store.GetAllAsync(ct);
        return Ok(rows.Select(Project).ToList());
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<GlobalVariableResponse>> Create(
        CreateGlobalVariableRequest request, CancellationToken ct)
    {
        if (!NameRegex.IsMatch(request.Name))
            return BadRequest(new { message = "Name must match [A-Za-z0-9_-]{1,100}" });
        if (request.Value is null)
            return BadRequest(new { message = "Value is required on create" });

        var folderId = request.FolderId ?? GlobalVariableFolder.RootFolderId;
        if (!await _folders.ExistsAsync(folderId, ct))
            return BadRequest(new { message = "Folder not found" });

        var v = await _store.CreateAsync(
            request.Name, request.Value, request.IsSecret, request.Description,
            folderId, this.GetCurrentUsername(), ct);

        await _audit.LogAsync(AuditActions.GlobalVariableCreated, "GlobalVariable", v.Id,
            AuditDetails.Json(("name", v.Name), ("isSecret", v.IsSecret)), ct);

        ApiMetrics.GlobalVariableOperations.Add(1,
            new("operation", "create"),
            new("result", "success"));

        return CreatedAtAction(nameof(GetAll), new { id = v.Id }, Project(v));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, UpdateGlobalVariableRequest request, CancellationToken ct)
    {
        if (!NameRegex.IsMatch(request.Name))
            return BadRequest(new { message = "Name must match [A-Za-z0-9_-]{1,100}" });

        // null folderId = "keep the variable's current folder" (mirrors the value:null = unchanged
        // convention). This prevents `np globals import --upsert` and any caller that doesn't echo a
        // folderId from silently relocating the variable to Root. To move, pass an explicit folder id
        // (or use POST /{id}/move-folder). Existence is only checked for an explicit (non-null) id.
        var folderId = request.FolderId;
        if (folderId is not null && !await _folders.ExistsAsync(folderId.Value, ct))
            return BadRequest(new { message = "Folder not found" });

        try
        {
            await _store.UpdateAsync(id, request.Name, request.Value, request.IsSecret, request.Description,
                folderId, this.GetCurrentUsername(), ct);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        // The "demotion" guard (security-audit finding M-24 — secret → non-secret without a new
        // value) and other domain validations throw InvalidOperationException. That's a bad-input
        // error from the caller, not a server error — map it to 400 instead of letting it fall
        // through to the generic 500 handler.
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }

        await _audit.LogAsync(AuditActions.GlobalVariableUpdated, "GlobalVariable", id,
            AuditDetails.Json(("name", request.Name), ("valueChanged", request.Value is not null), ("isSecret", request.IsSecret)), ct);

        ApiMetrics.GlobalVariableOperations.Add(1,
            new("operation", "update"),
            new("result", "success"));

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        try { await _store.DeleteAsync(id, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        await _audit.LogAsync(AuditActions.GlobalVariableDeleted, "GlobalVariable", id, null, ct);

        ApiMetrics.GlobalVariableOperations.Add(1,
            new("operation", "delete"),
            new("result", "success"));

        return NoContent();
    }

    /// <summary>
    /// Reassigns a variable to a different organizational folder. Purely cosmetic — the folder
    /// never affects how <c>{{globals.NAME}}</c> resolves.
    /// </summary>
    [HttpPost("{id:guid}/move-folder")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> MoveToFolder(Guid id, MoveGlobalVariableRequest request, CancellationToken ct)
    {
        if (!await _folders.ExistsAsync(request.FolderId, ct))
            return BadRequest(new { message = "Folder not found" });

        try { await _store.MoveToFolderAsync(id, request.FolderId, this.GetCurrentUsername(), ct); }
        catch (KeyNotFoundException) { return NotFound(); }

        await _audit.LogAsync(AuditActions.GlobalVariableMoved, "GlobalVariable", id,
            AuditDetails.Json(("folderId", request.FolderId)), ct);

        ApiMetrics.GlobalVariableOperations.Add(1,
            new("operation", "move"),
            new("result", "success"));

        return NoContent();
    }

    // Secrets are replaced with the literal string "***" so the API surface never leaks the
    // ciphertext (Base64-DPAPI output isn't useful outside this host but no reason to ship it)
    // nor the plaintext (the whole point of IsSecret=true). Non-secret values go through as-is.
    private static GlobalVariableResponse Project(GlobalVariable v)
        => new(v.Id, v.Name, v.IsSecret ? "***" : v.Value, v.IsSecret, v.Description,
               v.FolderId, v.CreatedAt, v.UpdatedAt, v.UpdatedBy);
}

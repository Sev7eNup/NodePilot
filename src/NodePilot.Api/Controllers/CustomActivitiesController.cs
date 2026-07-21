using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Audit;
using NodePilot.Api.Security;
using NodePilot.Core.Activities;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;

namespace NodePilot.Api.Controllers;

/// <summary>
/// CRUD + lifecycle for user-authored custom activities ("Custom Nodes" in the designer palette).
/// Governance: a definition is created disabled (Draft); Admin+Operator may edit/delete it while
/// disabled; once an Admin enables it, every mutation is Admin-only. Enable/Disable + import
/// freshly-imported (always-disabled) definitions are Admin-gated for go-live. The lightweight
/// <see cref="GetCatalog"/> feeds the designer palette for all roles; full detail incl. the script is
/// author-only.
/// </summary>
[ApiController]
[Route("api/custom-activities")]
[Authorize]
public sealed class CustomActivitiesController(ICustomActivityDefinitionStore store, IAuditWriter audit) : ControllerBase
{
    private const string ExportSchema = "nodepilot-customactivity-export/v1";

    // ---- Catalog (palette) -------------------------------------------------

    /// <summary>Lightweight catalog for the designer palette. All roles see enabled entries; authors may include drafts.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CustomActivityCatalogEntry>>> GetCatalog(
        [FromQuery] bool includeDisabled, CancellationToken ct)
    {
        var canSeeDrafts = User.IsInRole("Admin") || User.IsInRole("Operator");
        var rows = await store.GetAllAsync(includeDisabled && canSeeDrafts, ct);
        return Ok(rows.Select(ToCatalogEntry).ToList());
    }

    // ---- Full detail (author) ----------------------------------------------

    [HttpGet("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<CustomActivityResponse>> Get(Guid id, CancellationToken ct)
    {
        var def = await store.GetByIdAsync(id, ct);
        return def is null ? NotFound() : Ok(ToResponse(def));
    }

    [HttpGet("{id:guid}/versions")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<IReadOnlyList<CustomActivityVersionResponse>>> GetVersions(Guid id, CancellationToken ct)
    {
        var def = await store.GetByIdAsync(id, ct);
        if (def is null) return NotFound();
        var versions = await store.GetVersionsAsync(id, ct);
        return Ok(versions.Select(ToVersionResponse).ToList());
    }

    // ---- Create / Update / Delete ------------------------------------------

    [HttpPost]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<CustomActivitySaveResponse>> Create(CreateCustomActivityRequest request, CancellationToken ct)
    {
        var inputs = request.Inputs ?? [];
        var outputs = request.Outputs ?? [];
        var icon = string.IsNullOrWhiteSpace(request.Icon) ? "extension" : request.Icon!;
        var engine = string.IsNullOrWhiteSpace(request.Engine) ? "auto" : request.Engine!;

        var error = CustomActivityValidation.Validate(request.Key, request.Name, icon, engine, inputs, outputs, requireKey: true);
        if (error is not null) return BadRequest(new { message = error });
        if (string.IsNullOrWhiteSpace(request.ScriptTemplate))
            return BadRequest(new { message = "ScriptTemplate is required." });

        var newInput = new CustomActivityDefinitionInput
        {
            Key = request.Key, Name = request.Name, Description = request.Description, Icon = icon, Color = request.Color,
            ScriptTemplate = request.ScriptTemplate, Engine = engine, RunsRemote = request.RunsRemote, Isolated = request.Isolated,
            MemoryLimitMb = request.MemoryLimitMb, MaxProcesses = request.MaxProcesses,
            DefaultTimeoutSeconds = request.DefaultTimeoutSeconds, SuccessExitCodes = request.SuccessExitCodes,
            InputParametersJson = CustomActivityParameters.Serialize(inputs),
            OutputParametersJson = CustomActivityParameters.Serialize(outputs),
        };
        CustomActivityDefinition def;
        try { def = await store.CreateAsync(newInput, this.GetCurrentUsername(), ct); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }

        await audit.LogAsync(AuditActions.CustomActivityCreated, "CustomActivity", def.Id,
            AuditDetails.Json(("key", def.Key), ("name", def.Name)), ct);

        return CreatedAtAction(nameof(Get), new { id = def.Id },
            new CustomActivitySaveResponse(ToResponse(def), Lint(def.ScriptTemplate)));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<CustomActivitySaveResponse>> Update(Guid id, UpdateCustomActivityRequest request, CancellationToken ct)
    {
        var existing = await store.GetByIdAsync(id, ct);
        if (existing is null) return NotFound();
        if (MutationForbidden(existing, out var forbid)) return forbid!;

        var inputs = request.Inputs ?? [];
        var outputs = request.Outputs ?? [];
        var icon = string.IsNullOrWhiteSpace(request.Icon) ? "extension" : request.Icon!;
        var engine = string.IsNullOrWhiteSpace(request.Engine) ? "auto" : request.Engine!;

        var error = CustomActivityValidation.Validate(null, request.Name, icon, engine, inputs, outputs, requireKey: false);
        if (error is not null) return BadRequest(new { message = error });
        if (string.IsNullOrWhiteSpace(request.ScriptTemplate))
            return BadRequest(new { message = "ScriptTemplate is required." });

        var updInput = new CustomActivityDefinitionInput
        {
            Key = existing.Key, Name = request.Name, Description = request.Description, Icon = icon, Color = request.Color,
            ScriptTemplate = request.ScriptTemplate, Engine = engine, RunsRemote = request.RunsRemote, Isolated = request.Isolated,
            MemoryLimitMb = request.MemoryLimitMb, MaxProcesses = request.MaxProcesses,
            DefaultTimeoutSeconds = request.DefaultTimeoutSeconds, SuccessExitCodes = request.SuccessExitCodes,
            InputParametersJson = CustomActivityParameters.Serialize(inputs),
            OutputParametersJson = CustomActivityParameters.Serialize(outputs),
            ChangeNote = request.ChangeNote,
        };
        CustomActivityDefinition def;
        try { def = await store.UpdateAsync(id, updInput, request.ConcurrencyToken, this.GetCurrentUsername(), ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (CustomActivityConcurrencyException ex) { return Conflict(new { message = ex.Message }); }

        await audit.LogAsync(AuditActions.CustomActivityUpdated, "CustomActivity", def.Id,
            AuditDetails.Json(("key", def.Key), ("version", def.Version)), ct);

        return Ok(new CustomActivitySaveResponse(ToResponse(def), Lint(def.ScriptTemplate)));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var existing = await store.GetByIdAsync(id, ct);
        if (existing is null) return NotFound();
        if (MutationForbidden(existing, out var forbid)) return forbid!;

        await store.SoftDeleteAsync(id, ct);
        await audit.LogAsync(AuditActions.CustomActivityDeleted, "CustomActivity", id,
            AuditDetails.Json(("key", existing.Key)), ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/rollback/{version:int}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<CustomActivityResponse>> Rollback(Guid id, int version, CancellationToken ct)
    {
        var existing = await store.GetByIdAsync(id, ct);
        if (existing is null) return NotFound();
        if (MutationForbidden(existing, out var forbid)) return forbid!;

        CustomActivityDefinition def;
        try { def = await store.RollbackAsync(id, version, this.GetCurrentUsername(), ct); }
        catch (KeyNotFoundException) { return NotFound(); }

        await audit.LogAsync(AuditActions.CustomActivityRolledBack, "CustomActivity", id,
            AuditDetails.Json(("toVersion", version), ("newVersion", def.Version)), ct);
        return Ok(ToResponse(def));
    }

    // ---- Enable / Disable (Admin only) -------------------------------------

    [HttpPost("{id:guid}/enable")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> Enable(Guid id, CancellationToken ct) => SetEnabled(id, true, ct);

    [HttpPost("{id:guid}/disable")]
    [Authorize(Roles = "Admin")]
    public Task<IActionResult> Disable(Guid id, CancellationToken ct) => SetEnabled(id, false, ct);

    private async Task<IActionResult> SetEnabled(Guid id, bool enabled, CancellationToken ct)
    {
        var def = await store.GetByIdAsync(id, ct);
        if (def is null) return NotFound();
        await store.SetEnabledAsync(id, enabled, this.GetCurrentUsername(), ct);
        await audit.LogAsync(
            enabled ? AuditActions.CustomActivityEnabled : AuditActions.CustomActivityDisabled,
            "CustomActivity", id, AuditDetails.Json(("key", def.Key)), ct);
        return NoContent();
    }

    // ---- Import / Export ---------------------------------------------------

    [HttpGet("export")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<CustomActivityExportEnvelope>> Export(CancellationToken ct)
    {
        var rows = await store.GetAllAsync(includeDisabled: true, ct);
        var items = rows.Select(d => new CustomActivityExportItem(
            d.Key, d.Name, d.Description, d.Icon, d.Color, d.ScriptTemplate, d.Engine, d.RunsRemote,
            d.Isolated, d.MemoryLimitMb, d.MaxProcesses, d.DefaultTimeoutSeconds, d.SuccessExitCodes,
            CustomActivityParameters.ParseInputs(d.InputParametersJson),
            CustomActivityParameters.ParseOutputs(d.OutputParametersJson))).ToList();
        return Ok(new CustomActivityExportEnvelope(ExportSchema, 1, DateTime.UtcNow, items));
    }

    /// <summary>Imports definitions from an export envelope. Every imported definition lands DISABLED and must be reviewed + enabled by an Admin. Key collisions are skipped.</summary>
    [HttpPost("import")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<IReadOnlyList<CustomActivityResponse>>> Import(CustomActivityExportEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Schema != ExportSchema)
            return BadRequest(new { message = $"Unsupported schema '{envelope.Schema}', expected '{ExportSchema}'." });

        var imported = new List<CustomActivityResponse>();
        foreach (var item in envelope.Items)
        {
            var icon = string.IsNullOrWhiteSpace(item.Icon) ? "extension" : item.Icon;
            var engine = string.IsNullOrWhiteSpace(item.Engine) ? "auto" : item.Engine;
            var inputs = item.Inputs ?? [];
            var outputs = item.Outputs ?? [];
            if (CustomActivityValidation.Validate(item.Key, item.Name, icon, engine, inputs, outputs, requireKey: true) is not null)
                continue; // skip malformed entries
            if (await store.GetByKeyAsync(item.Key, ct) is not null)
                continue; // skip key collisions

            var input = new CustomActivityDefinitionInput
            {
                Key = item.Key, Name = item.Name, Description = item.Description, Icon = icon, Color = item.Color,
                ScriptTemplate = item.ScriptTemplate, Engine = engine, RunsRemote = item.RunsRemote, Isolated = item.Isolated,
                MemoryLimitMb = item.MemoryLimitMb, MaxProcesses = item.MaxProcesses,
                DefaultTimeoutSeconds = item.DefaultTimeoutSeconds, SuccessExitCodes = item.SuccessExitCodes,
                InputParametersJson = CustomActivityParameters.Serialize(inputs),
                OutputParametersJson = CustomActivityParameters.Serialize(outputs),
            };
            var def = await store.CreateAsync(input, this.GetCurrentUsername(), ct); // created disabled
            await audit.LogAsync(AuditActions.CustomActivityImported, "CustomActivity", def.Id,
                AuditDetails.Json(("key", def.Key)), ct);
            imported.Add(ToResponse(def));
        }
        return Ok(imported);
    }

    // ---- Helpers -----------------------------------------------------------

    /// <summary>Once enabled, only an Admin may mutate. Returns true (and a 403) when forbidden.</summary>
    private bool MutationForbidden(CustomActivityDefinition def, out ActionResult? result)
    {
        if (def.IsEnabled && !User.IsInRole("Admin"))
        {
            result = StatusCode(StatusCodes.Status403Forbidden, new
            {
                message = "This custom activity is enabled (live); only an administrator may modify it. Ask an admin to disable it first to edit as Operator."
            });
            return true;
        }
        result = null;
        return false;
    }

    private static IReadOnlyList<CustomActivityLintWarning> Lint(string script) =>
        WorkflowScriptLinter.LintScript(script).Select(w => new CustomActivityLintWarning(w.Rule, w.Message)).ToList();

    private static CustomActivityResponse ToResponse(CustomActivityDefinition d) => new(
        d.Id, d.Key, CustomActivityType.ForKey(d.Key), d.Name, d.Description, d.Icon, d.Color,
        d.ScriptTemplate, d.Engine, d.RunsRemote, d.Isolated, d.MemoryLimitMb, d.MaxProcesses,
        d.DefaultTimeoutSeconds, d.SuccessExitCodes,
        CustomActivityParameters.ParseInputs(d.InputParametersJson),
        CustomActivityParameters.ParseOutputs(d.OutputParametersJson),
        d.IsEnabled, d.Version, d.ConcurrencyToken, d.CreatedAt, d.UpdatedAt, d.CreatedBy, d.UpdatedBy, d.ChangeNote);

    private static CustomActivityCatalogEntry ToCatalogEntry(CustomActivityDefinition d) => new(
        d.Id, d.Key, CustomActivityType.ForKey(d.Key), d.Name, d.Description, d.Icon, d.Color,
        d.RunsRemote, "always",
        CustomActivityParameters.ParseInputs(d.InputParametersJson),
        CustomActivityParameters.ParseOutputs(d.OutputParametersJson),
        d.IsEnabled, d.Version);

    private static CustomActivityVersionResponse ToVersionResponse(CustomActivityDefinitionVersion v) => new(
        v.Version, v.Name, v.Description, v.Icon, v.Color, v.ScriptTemplate, v.Engine, v.RunsRemote, v.Isolated,
        v.MemoryLimitMb, v.MaxProcesses, v.DefaultTimeoutSeconds, v.SuccessExitCodes,
        CustomActivityParameters.ParseInputs(v.InputParametersJson),
        CustomActivityParameters.ParseOutputs(v.OutputParametersJson),
        v.CreatedAt, v.CreatedBy, v.ChangeNote);
}

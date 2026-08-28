using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Edit-lock lifecycle (lock, unlock, publish, force-unlock), version history
/// (versions, rollback), and one-off step testing. These mutations use the
/// SCOrch-style edit lock checked in <see cref="WorkflowsControllerBase"/>.
///
/// <para>Sibling controllers: <see cref="WorkflowsController"/> (CRUD/lifecycle),
/// <see cref="WorkflowImportExportController"/> (export/import).</para>
/// </summary>
[ApiController]
[Route("api/workflows")]
[Authorize]
public class WorkflowEditingController : WorkflowsControllerBase
{
    private readonly IStepTester _stepTester;
    private readonly IStepTestContextProvider _testContextProvider;
    private readonly NodePilot.Api.Services.WorkflowVersionDefinitionProtector _versionDefinitions;

    public WorkflowEditingController(
        NodePilotDbContext db,
        ILogger<WorkflowEditingController> logger,
        IAuditWriter audit,
        NodePilot.Core.Interfaces.IResourceAuthorizationService authz,
        IStepTester stepTester,
        IStepTestContextProvider testContextProvider,
        NodePilot.Api.Services.WorkflowVersionDefinitionProtector versionDefinitions)
        : base(db, logger, audit, authz)
    {
        _stepTester = stepTester;
        _testContextProvider = testContextProvider;
        _versionDefinitions = versionDefinitions;
    }

    // --- Optimistic-concurrency core (shared by Publish + Rollback) ------------------------

    /// <summary>
    /// Applies the field mutation and the history snapshot inside one execution-strategy
    /// transaction, with the workflow UPDATE as an atomic compare-and-swap on
    /// (folder, lock owner, lock timestamp, version). This prevents a concurrent
    /// force-unlock plus re-lock from stealing the lock, and a concurrent publish or update
    /// from causing a lost update. A return value of 0 means one of those raced ahead — the
    /// caller re-reads via <see cref="LostCompareAndSwapAsync"/> for the correct 423/409 verdict.
    ///
    /// <para>Publish and Rollback differ only in the setter chain and the snapshot row, so both
    /// come in as parameters. The snapshot is a <b>factory</b>, not an instance: each retry of
    /// the execution strategy clears the change tracker and must stage a fresh row.</para>
    /// </summary>
    private Task<int> CompareAndSwapWorkflowAsync(
        Guid id,
        Workflow workflow,
        Guid? meId,
        int oldVersion,
        Action<UpdateSettersBuilder<Workflow>> setters,
        Func<WorkflowVersion> snapshot,
        CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear(); // retry-safe: drop any snapshot added by a prior attempt
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var rows = await _db.Workflows
                .Where(w => w.Id == id
                            && w.FolderId == workflow.FolderId
                            && w.CheckedOutByUserId == meId
                            && w.CheckedOutAt == workflow.CheckedOutAt
                            && w.Version == oldVersion)
                .ExecuteUpdateAsync(setters, ct);
            if (rows == 0)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }
            _db.WorkflowVersions.Add(snapshot());
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return rows;
        });
    }

    /// <summary>
    /// The compare-and-swap matched no row: re-read and answer with whichever verdict actually
    /// applies now — 404 if the workflow vanished, the access gate's verdict if permissions
    /// changed, 423 if the lock moved, otherwise the version conflict.
    /// </summary>
    private async Task<ActionResult> LostCompareAndSwapAsync(Guid id, string conflictMessage, CancellationToken ct)
    {
        var current = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (current is null) return NotFound();
        if (await RequireWorkflowAccessAsync(current, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } deniedNow)
            return deniedNow;
        if (await EnsureWriteLockAsync(current, ct) is { } lockedNow) return lockedNow;
        return Conflict(new
        {
            code = "workflow_version_conflict",
            message = conflictMessage,
            currentVersion = current.Version,
        });
    }

    /// <summary>Same conflict body, but for the unique-index violation raised by a racing
    /// insert.</summary>
    private async Task<ActionResult> VersionUniqueConflictAsync(Guid id, string conflictMessage, CancellationToken ct)
        => Conflict(new
        {
            code = "workflow_version_conflict",
            message = conflictMessage,
            currentVersion = await _db.Workflows.AsNoTracking()
                .Where(w => w.Id == id)
                .Select(w => w.Version)
                .FirstOrDefaultAsync(ct),
        });

    // --- Versions / Rollback --------------------------------------------------------------

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<List<WorkflowVersionInfo>>> GetVersions(Guid id, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Read, ct) is { } d1) return d1;

        var historic = await _db.WorkflowVersions.AsNoTracking()
            .Where(v => v.WorkflowId == id)
            .OrderByDescending(v => v.Version)
            .Select(v => new WorkflowVersionInfo(
                v.Version, v.Name, v.CreatedAt, v.CreatedBy, v.ChangeNote, IsCurrent: false))
            .ToListAsync(ct);

        // Prepend a synthetic "current" entry so the client can render a full timeline
        // without having to also GET /api/workflows/{id}. The DefinitionJson for current
        // lives on the live row; historic entries are loaded on demand via /{version}.
        var all = new List<WorkflowVersionInfo>
        {
            new(workflow.Version, workflow.Name, workflow.UpdatedAt,
                workflow.CreatedBy, ChangeNote: null, IsCurrent: true)
        };
        all.AddRange(historic);
        return Ok(all);
    }

    [HttpGet("{id:guid}/versions/{version:int}")]
    public async Task<ActionResult<WorkflowVersionDetail>> GetVersion(Guid id, int version, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Read, ct) is { } d2) return d2;
        var capabilities = await _authz.GetWorkflowCapabilitiesAsync(User, workflow.FolderId, ct);

        if (version == workflow.Version)
            return Ok(new WorkflowVersionDetail(
                workflow.Version, workflow.Name, workflow.Description,
                ScopedDefinitionJson(workflow.DefinitionJson, capabilities.CanEdit),
                workflow.UpdatedAt, workflow.CreatedBy, null, IsCurrent: true));

        var row = await _db.WorkflowVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.WorkflowId == id && v.Version == version, ct);
        if (row is null) return NotFound();

        var historicDefinition = _versionDefinitions.Unprotect(row.DefinitionJson);
        return Ok(new WorkflowVersionDetail(
            row.Version, row.Name, row.Description,
            ScopedDefinitionJson(historicDefinition, capabilities.CanEdit),
            row.CreatedAt, row.CreatedBy, row.ChangeNote, IsCurrent: false));
    }

    [HttpPost("{id:guid}/rollback/{version:int}")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<WorkflowResponse>> Rollback(
        Guid id, int version, [FromBody] RollbackRequest? body, CancellationToken ct)
    {
        var meId = this.GetCurrentUserId();
        if (meId is null) return Unauthorized();

        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } d3) return d3;
        if (await EnsureWriteLockAsync(workflow, ct) is { } locked) return locked;
        if (version == workflow.Version)
            return BadRequest(new { message = "Cannot roll back to the current version." });

        var target = await _db.WorkflowVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.WorkflowId == id && v.Version == version, ct);
        if (target is null) return NotFound(new { message = $"Version {version} not found for this workflow." });

        // Validate the historic definition before applying it. Older versions can violate the
        // current schema, and rolling forward would push one the engine refuses to load at
        // fire time. Lint warnings are non-fatal, same as Create/Update.
        var targetDefinitionJson = _versionDefinitions.Unprotect(target.DefinitionJson);
        var rollbackSizeError = ValidateDefinitionJson(targetDefinitionJson);
        if (rollbackSizeError is not null) return rollbackSizeError;
        LintAndLogWarnings(targetDefinitionJson);

        // Pre-rollback values captured for the append-only history snapshot.
        var oldVersion = workflow.Version;
        var oldName = workflow.Name;
        var oldDescription = workflow.Description;
        var oldDefinitionJson = workflow.DefinitionJson;
        var now = DateTime.UtcNow;
        var updatedBy = this.GetCurrentUsername();

        var computed = new Workflow { DefinitionJson = targetDefinitionJson };
        PopulateComputedColumns(computed);

        // Roll-forward: snapshot the live row, apply the target as a new version — through the
        // shared compare-and-swap (see CompareAndSwapWorkflowAsync). The lock is intentionally
        // retained here (rollback ≠ publish), which is why the setter chain clears no lock fields.
        const string rollbackConflictMessage =
            "Workflow was updated concurrently. Reload the workflow and retry publish.";
        int updated;
        try
        {
            updated = await CompareAndSwapWorkflowAsync(
                id, workflow, meId, oldVersion,
                s => s
                    .SetProperty(w => w.Name, target.Name)
                    .SetProperty(w => w.Description, target.Description)
                    .SetProperty(w => w.DefinitionJson, targetDefinitionJson)
                    .SetProperty(w => w.Version, oldVersion + 1)
                    .SetProperty(w => w.TriggerTypesJson, computed.TriggerTypesJson)
                    .SetProperty(w => w.ActivityCount, computed.ActivityCount)
                    .SetProperty(w => w.UpdatedAt, now)
                    .SetProperty(w => w.UpdatedBy, updatedBy),
                () => new WorkflowVersion
                {
                    Id = Guid.NewGuid(),
                    WorkflowId = id,
                    Version = oldVersion,
                    Name = oldName,
                    Description = oldDescription,
                    DefinitionJson = _versionDefinitions.Protect(oldDefinitionJson),
                    CreatedAt = now,
                    CreatedBy = updatedBy,
                    ChangeNote = $"Superseded by rollback to v{version}",
                },
                ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            return await VersionUniqueConflictAsync(id, rollbackConflictMessage, ct);
        }

        if (updated == 0)
            return await LostCompareAndSwapAsync(id, rollbackConflictMessage, ct);

        var rolled = await _db.Workflows.AsNoTracking().FirstAsync(w => w.Id == id, ct);
        var reason = string.IsNullOrWhiteSpace(body?.Reason) ? $"Rolled back to v{version}" : body!.Reason!;
        await _audit.LogAsync(AuditActions.WorkflowRolledBack, "Workflow", rolled.Id,
            AuditDetails.Json(("toVersion", version), ("newVersion", rolled.Version), ("reason", reason)), ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, "rollback"),
            new("result", "success"));

        var lockOwnerName = await ResolveLockOwnerNameAsync(rolled, ct);
        return Ok(await ToScopedResponseAsync(rolled, lockOwnerName, ct));
    }

    // --- Edit-Lock (SCOrch-style) -------------------------------------------------------

    /// <summary>
    /// Atomic "Bearbeiten starten": flips <c>IsEnabled=false</c> AND assigns the lock to the
    /// caller. The combined operation makes the productive- to -edit transition a single audit
    /// event and prevents a race where a triggered run fires between the disable and the
    /// lock claim. Returns 409 Conflict if another user already holds the lock; 200 OK
    /// (idempotent) when the caller already holds it.
    /// </summary>
    [HttpPost("{id:guid}/lock")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<WorkflowResponse>> Lock(Guid id, CancellationToken ct)
    {
        var meId = this.GetCurrentUserId();
        if (meId is null) return Unauthorized();

        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } d4) return d4;

        if (workflow.CheckedOutByUserId == meId)
        {
            // Idempotent re-claim — return current state without re-auditing.
            var ownerName = await ResolveLockOwnerNameAsync(workflow, ct);
            return Ok(await ToScopedResponseAsync(workflow, ownerName, ct));
        }

        if (workflow.CheckedOutByUserId is not null)
        {
            var existingOwnerName = await ResolveLockOwnerNameAsync(workflow, ct);
            return Conflict(new
            {
                message = $"Workflow is already locked by {existingOwnerName ?? "another user"}.",
                lockedByUserId = workflow.CheckedOutByUserId,
                lockedByUserName = existingOwnerName,
                lockedAt = workflow.CheckedOutAt,
            });
        }

        var now = DateTime.UtcNow;
        var updatedBy = this.GetCurrentUsername();
        var updated = await _db.Workflows
            .Where(w => w.Id == id
                        && w.FolderId == workflow.FolderId
                        && w.CheckedOutByUserId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.IsEnabled, false)
                .SetProperty(w => w.CheckedOutByUserId, meId)
                .SetProperty(w => w.CheckedOutAt, now)
                .SetProperty(w => w.UpdatedAt, now)
                .SetProperty(w => w.UpdatedBy, updatedBy), ct);

        if (updated == 0)
        {
            var current = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (current is null) return NotFound();
            if (await RequireWorkflowAccessAsync(current, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } deniedNow)
                return deniedNow;
            if (current.CheckedOutByUserId == meId)
            {
                var ownerName = await ResolveLockOwnerNameAsync(current, ct);
                return Ok(await ToScopedResponseAsync(current, ownerName, ct));
            }

            if (current.CheckedOutByUserId is null)
            {
                return Conflict(new
                {
                    code = "workflow_scope_conflict",
                    message = "Workflow moved concurrently. Reload the workflow and retry locking it.",
                });
            }

            var existingOwnerName = await ResolveLockOwnerNameAsync(current, ct);
            return Conflict(new
            {
                message = $"Workflow is already locked by {existingOwnerName ?? "another user"}.",
                lockedByUserId = current.CheckedOutByUserId,
                lockedByUserName = existingOwnerName,
                lockedAt = current.CheckedOutAt,
            });
        }

        workflow = await _db.Workflows.AsNoTracking().FirstAsync(w => w.Id == id, ct);

        await _audit.LogAsync(AuditActions.WorkflowLocked, "Workflow", workflow.Id,
            AuditDetails.Json(("name", workflow.Name)), ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, "lock"),
            new("result", "success"));

        var lockOwnerName = await ResolveLockOwnerNameAsync(workflow, ct);
        return Ok(await ToScopedResponseAsync(workflow, lockOwnerName, ct));
    }

    /// <summary>
    /// "Bearbeitung beenden": clears the lock. <c>IsEnabled</c> is left untouched — the
    /// workflow stays disabled until the user explicitly hits Enable or Publish. Returns 423
    /// if the caller is not the lock owner (use <c>force-unlock</c> as Admin to break a
    /// foreign lock).
    /// </summary>
    [HttpPost("{id:guid}/unlock")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<WorkflowResponse>> Unlock(Guid id, CancellationToken ct)
    {
        var meId = this.GetCurrentUserId();
        if (meId is null) return Unauthorized();

        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } d5) return d5;

        if (workflow.CheckedOutByUserId is null)
        {
            // No-op — already unlocked. Return current state without auditing.
            return Ok(await ToScopedResponseAsync(workflow, lockOwnerUserName: null, ct));
        }

        // M-3 (security audit 2026-05-15): atomic compare-and-swap — only the lock owner clears
        // the lock, in a single statement. Closes the load to EnsureWriteLockAsync to SaveChanges
        // TOCTOU window where a force-unlock + re-lock by another user could land between the
        // in-memory check and the write, letting a non-owner's unlock stomp the new lock.
        var now = DateTime.UtcNow;
        var updatedBy = this.GetCurrentUsername();
        var updated = await _db.Workflows
            .Where(w => w.Id == id
                        && w.FolderId == workflow.FolderId
                        && w.CheckedOutByUserId == meId
                        && w.CheckedOutAt == workflow.CheckedOutAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.CheckedOutByUserId, (Guid?)null)
                .SetProperty(w => w.CheckedOutAt, (DateTime?)null)
                .SetProperty(w => w.UpdatedAt, now)
                .SetProperty(w => w.UpdatedBy, updatedBy), ct);

        if (updated == 0)
        {
            // Lock changed between read and write: re-read for the correct verdict.
            var current = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (current is null) return NotFound();
            if (await RequireWorkflowAccessAsync(current, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } deniedNow)
                return deniedNow;
            if (current.CheckedOutByUserId is null)
                return Ok(await ToScopedResponseAsync(current, lockOwnerUserName: null, ct));
            if (await EnsureWriteLockAsync(current, ct) is { } locked) return locked;
            // The same user acquired a new lock epoch after the observed one was released.
            // Do not clear it under the stale request.
            return Conflict(new
            {
                code = "workflow_lock_conflict",
                message = "Workflow lock changed concurrently. Reload the workflow and retry unlock.",
                lockedAt = current.CheckedOutAt,
            });
        }

        var unlocked = await _db.Workflows.AsNoTracking().FirstAsync(w => w.Id == id, ct);
        await _audit.LogAsync(AuditActions.WorkflowUnlocked, "Workflow", unlocked.Id,
            AuditDetails.Json(("name", unlocked.Name)), ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, "unlock"),
            new("result", "success"));

        return Ok(await ToScopedResponseAsync(unlocked, lockOwnerUserName: null, ct));
    }

    /// <summary>
    /// Saves the draft, enables the workflow, and releases the lock in one transaction.
    /// This prevents a partially published workflow if the client disconnects between operations.
    /// Requires the caller to hold the edit lock.
    /// </summary>
    [HttpPost("{id:guid}/publish")]
    [Authorize(Roles = "Admin,Operator")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBodyBytes, ValueLengthLimit = (int)MaxRequestBodyBytes)]
    public async Task<ActionResult<WorkflowResponse>> Publish(
        Guid id, PublishWorkflowRequest request, CancellationToken ct)
    {
        var meId = this.GetCurrentUserId();
        if (meId is null) return Unauthorized();

        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } d6) return d6;
        if (await EnsureWriteLockAsync(workflow, ct) is { } locked) return locked;

        var sizeError = ValidateDefinitionJson(request.DefinitionJson);
        if (sizeError is not null) return sizeError;
        if (NodePilot.Api.Security.WebhookHmacSecurity.ValidateDefinition(request.DefinitionJson) is { } hmacError)
        {
            return BadRequest(new
            {
                code = "weak_webhook_hmac_secret",
                message = hmacError,
            });
        }
        LintAndLogWarnings(request.DefinitionJson);

        // Pre-publish values captured for the history snapshot (the live row is untracked).
        var oldVersion = workflow.Version;
        var oldName = workflow.Name;
        var oldDescription = workflow.Description;
        var oldDefinitionJson = workflow.DefinitionJson;
        var oldCreatedBy = workflow.CreatedBy;
        var now = DateTime.UtcNow;
        var updatedBy = this.GetCurrentUsername();

        var computed = new Workflow { DefinitionJson = request.DefinitionJson };
        PopulateComputedColumns(computed);

        // Publish through the shared compare-and-swap (see CompareAndSwapWorkflowAsync); the
        // setter chain additionally enables the workflow and releases the edit lock.
        const string publishConflictMessage =
            "Workflow was published concurrently. Reload the workflow and retry publish.";
        int updated;
        try
        {
            updated = await CompareAndSwapWorkflowAsync(
                id, workflow, meId, oldVersion,
                s => s
                    .SetProperty(w => w.Name, request.Name)
                    .SetProperty(w => w.Description, request.Description)
                    .SetProperty(w => w.DefinitionJson, request.DefinitionJson)
                    .SetProperty(w => w.Version, oldVersion + 1)
                    .SetProperty(w => w.IsEnabled, true)
                    .SetProperty(w => w.PublishedByUserId, meId)
                    .SetProperty(w => w.CheckedOutByUserId, (Guid?)null)
                    .SetProperty(w => w.CheckedOutAt, (DateTime?)null)
                    .SetProperty(w => w.TriggerTypesJson, computed.TriggerTypesJson)
                    .SetProperty(w => w.ActivityCount, computed.ActivityCount)
                    .SetProperty(w => w.UpdatedAt, now)
                    .SetProperty(w => w.UpdatedBy, updatedBy),
                () => new WorkflowVersion
                {
                    Id = Guid.NewGuid(),
                    WorkflowId = id,
                    Version = oldVersion,
                    Name = oldName,
                    Description = oldDescription,
                    DefinitionJson = _versionDefinitions.Protect(oldDefinitionJson),
                    CreatedAt = now,
                    CreatedBy = oldCreatedBy ?? updatedBy,
                },
                ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            return await VersionUniqueConflictAsync(id, publishConflictMessage, ct);
        }

        if (updated == 0)
            return await LostCompareAndSwapAsync(id, publishConflictMessage, ct);

        var published = await _db.Workflows.AsNoTracking().FirstAsync(w => w.Id == id, ct);
        await _audit.LogAsync(AuditActions.WorkflowPublished, "Workflow", published.Id,
            AuditDetails.Json(("version", published.Version), ("name", published.Name)), ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, "publish"),
            new("result", "success"));

        return Ok(await ToScopedResponseAsync(published, lockOwnerUserName: null, ct));
    }

    /// <summary>
    /// Admin-only override: clears a foreign lock when the original owner is unavailable
    /// (left for the day with the workflow open, lost their session, etc.). Audit log
    /// captures the previous lock owner so post-mortem investigations can reconstruct
    /// who got bumped.
    /// </summary>
    [HttpPost("{id:guid}/force-unlock")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<WorkflowResponse>> ForceUnlock(Guid id, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        // Force-unlock is Admin-only via [Authorize(Roles="Admin")]; the global Admin
        // bypasses folder permissions in our service, so RequireWorkflowAccessAsync would
        // be a no-op here but adding it future-proofs against role-attribute removal.
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Admin, ct) is { } d7) return d7;

        if (workflow.CheckedOutByUserId is null)
            return Ok(await ToScopedResponseAsync(workflow, lockOwnerUserName: null, ct));

        var previousOwnerId = workflow.CheckedOutByUserId;
        var previousCheckedOutAt = workflow.CheckedOutAt;
        var previousOwnerName = await ResolveLockOwnerNameAsync(workflow, ct);

        var now = DateTime.UtcNow;
        var updatedBy = this.GetCurrentUsername();
        var unlocked = await _db.Workflows
            .Where(w => w.Id == id
                        && w.FolderId == workflow.FolderId
                        && w.CheckedOutByUserId == previousOwnerId
                        && w.CheckedOutAt == previousCheckedOutAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.CheckedOutByUserId, (Guid?)null)
                .SetProperty(w => w.CheckedOutAt, (DateTime?)null)
                .SetProperty(w => w.UpdatedAt, now)
                .SetProperty(w => w.UpdatedBy, updatedBy), ct);

        if (unlocked == 0)
        {
            var current = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (current is null) return NotFound();
            if (await RequireWorkflowAccessAsync(current, NodePilot.Core.Interfaces.ResourceOp.Admin, ct) is { } denied)
                return denied;
            if (current.CheckedOutByUserId is null)
                return Ok(await ToScopedResponseAsync(current, lockOwnerUserName: null, ct));

            var currentOwnerName = await ResolveLockOwnerNameAsync(current, ct);
            return Conflict(new
            {
                code = "workflow_lock_conflict",
                message = "Workflow lock changed concurrently. Reload the workflow and retry force-unlock.",
                lockedByUserId = current.CheckedOutByUserId,
                lockedByUserName = currentOwnerName,
                lockedAt = current.CheckedOutAt,
            });
        }

        workflow = await _db.Workflows.AsNoTracking().FirstAsync(w => w.Id == id, ct);

        var details = JsonSerializer.Serialize(new
        {
            name = workflow.Name,
            previousLockOwnerId = previousOwnerId,
            previousLockOwnerName = previousOwnerName,
        });
        await _audit.LogAsync(AuditActions.WorkflowForceUnlocked, "Workflow", workflow.Id, details, ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, "force_unlock"),
            new("result", "success"));

        return Ok(await ToScopedResponseAsync(workflow, lockOwnerUserName: null, ct));
    }

    /// <summary>
    /// Executes a single workflow step in isolation without creating an execution record.
    /// Useful for testing individual steps during development. Admin/Operator only.
    ///
    /// <para>Body fields: <c>MockVariables</c> for upstream variable injection,
    /// <c>ConfigOverride</c> for testing unsaved editor state without going through PUT first.
    /// Testing the persisted config requires Run; an override additionally requires Edit and
    /// the caller's active edit-lock because it can replace executable activity config.</para>
    /// </summary>
    [HttpPost("{workflowId:guid}/steps/{stepId}/test")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<StepTestResponse>> TestStep(
        Guid workflowId, string stepId,
        [FromBody] StepTestRequest? request,
        CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return NotFound();
        // Step-test runs the activity end-to-end (incl. WinRM, scripts, side effects),
        // so testing the persisted definition is a Run operation.
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Run, ct) is { } d8) return d8;

        var configOverride = request?.ConfigOverride;
        if (configOverride.HasValue)
        {
            // An override is unsaved executable editor state. Without the Edit + lock gates a
            // FolderOperator could replace a persisted runScript/HTTP/SQL config and execute it
            // with the workflow's stored target and credential despite lacking authoring rights.
            if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } editDenied)
                return editDenied;
            if (await EnsureWriteLockAsync(workflow, ct) is { } lockDenied)
                return lockDenied;
        }

        var result = await _stepTester.TestStepAsync(
            workflowId, stepId,
            StepTestAuthorizationSnapshot.Capture(workflow),
            request?.MockVariables,
            configOverride,
            ct);
        await _audit.LogAsync(AuditActions.WorkflowStepTested, "Workflow", workflow.Id,
            AuditDetails.Json(("stepId", stepId), ("success", result.Success), ("durationMs", result.DurationMs)),
            ct);
        return Ok(new StepTestResponse(
            result.Success, result.Output, result.ErrorOutput,
            result.OutputParameters, result.DurationMs, result.ErrorMessage));
    }

    /// <summary>
    /// Returns the upstream variable dump the step-test "with last run context" mode uses to
    /// pre-fill its mock editor. Walks the static graph upstream from <c>stepId</c> and joins
    /// against StepExecutions of the chosen execution (or the latest one). When the chosen
    /// run never ran the step in question, the values default to schema-only (null). Globals
    /// are always included.
    /// </summary>
    [HttpGet("{workflowId:guid}/steps/{stepId}/test-context")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<StepTestContextResponse>> GetTestContext(
        Guid workflowId, string stepId,
        [FromQuery] Guid? executionId,
        CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Read, ct) is { } d9) return d9;

        var ctx = await _testContextProvider.GetContextAsync(workflowId, stepId, executionId, ct);
        var vars = ctx.Variables
            .Select(v => new StepTestContextVariable(v.Key, v.Origin, v.Source, v.Value))
            .ToList();
        return Ok(new StepTestContextResponse(ctx.ExecutionId, ctx.ExecutedAt, ctx.Status, vars));
    }

    /// <summary>
    /// Lightweight dropdown source for the step-test UI: lists recent executions plus a
    /// <c>StepRan</c> flag so the UI can grey out runs where this specific step never fired.
    /// </summary>
    [HttpGet("{workflowId:guid}/steps/{stepId}/test-context/runs")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<List<StepTestContextRunInfo>>> ListTestContextRuns(
        Guid workflowId, string stepId,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Read, ct) is { } d10) return d10;

        var runs = await _testContextProvider.ListRunsAsync(workflowId, stepId, limit, ct);
        return Ok(runs.Select(r => new StepTestContextRunInfo(
            r.ExecutionId, r.StartedAt, r.Status, r.TriggeredBy, r.StepRan)).ToList());
    }
}

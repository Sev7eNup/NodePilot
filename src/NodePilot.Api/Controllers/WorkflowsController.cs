using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Core workflow CRUD + lifecycle (Enable/Disable/Duplicate). Edit-lock + version-history
/// endpoints live on <see cref="WorkflowEditingController"/>; export/import on
/// <see cref="WorkflowImportExportController"/>. All three controllers share the
/// <c>api/workflows</c> route prefix and the shared helpers in
/// <see cref="WorkflowsControllerBase"/>.
/// </summary>
[ApiController]
[Route("api/workflows")]
[Authorize]
public class WorkflowsController : WorkflowsControllerBase
{
    private readonly NodePilot.Api.Services.IWorkflowContractDeriver _contractDeriver;

    public WorkflowsController(
        NodePilotDbContext db,
        ILogger<WorkflowsController> logger,
        IAuditWriter audit,
        IResourceAuthorizationService authz,
        NodePilot.Api.Services.IWorkflowContractDeriver contractDeriver)
        : base(db, logger, audit, authz)
    {
        _contractDeriver = contractDeriver;
    }

    /// <summary>
    /// Returns the calling-contract for a workflow — declared inputs from
    /// <c>manualTrigger.parameters</c> + downstream-available outputs from
    /// <c>returnData.data</c> keys + engine-injected system outputs.
    ///
    /// <para>Read-only and non-sensitive (no secret values, just declarations) — Viewer
    /// role is allowed.</para>
    /// </summary>
    [HttpGet("{id:guid}/contract")]
    public async Task<ActionResult<WorkflowContractResponse>> GetContract(Guid id, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, ResourceOp.Read, ct) is { } denied) return denied;
        return Ok(_contractDeriver.Derive(workflow));
    }

    /// <summary>
    /// Same as <see cref="GetContract"/> but looks up by <c>Name</c> — exact-case wins,
    /// otherwise case-insensitive; ambiguous names (only possible because Workflow.Name
    /// has no unique index) return 409 instead of silently picking one. Mirrors the
    /// engine's resolution in <c>StartWorkflowActivity</c>/<c>ForEachActivity</c> so the UI
    /// never shows a contract for a workflow the runtime won't actually find.
    /// </summary>
    [HttpGet("by-name/{name}/contract")]
    public async Task<ActionResult<WorkflowContractResponse>> GetContractByName(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return NotFound();
        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        var query = _db.Workflows.AsNoTracking().AsQueryable();
        if (!accessible.IsUnrestricted)
        {
            if (accessible.FolderIds.Count == 0) return NotFound();
            query = query.Where(w => accessible.FolderIds.Contains(w.FolderId));
        }
        var result = await WorkflowNameResolver.ResolveByNameAsync(query, name, ct);
        if (result.Outcome == WorkflowNameResolver.Outcome.Ambiguous)
            return Conflict(new { message = $"Multiple workflows named '{name.Trim()}' — disambiguate with the GUID." });
        if (result.Workflow is not { } workflow) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, ResourceOp.Read, ct) is { } denied) return denied;
        return Ok(_contractDeriver.Derive(workflow));
    }

    [HttpGet]
    public async Task<ActionResult<List<WorkflowResponse>>> GetAll(CancellationToken ct)
    {
        const int StatsWindow = 20;
        // Hard cap on how many workflows a single list call returns. Without this cap, a
        // read-only user in an org with tens of thousands of workflows would load the entire
        // catalogue including the ROW_NUMBER window query for every workflow — both a DB-load
        // and a payload-size risk (the response grows linearly, and JSON serialization keeps
        // the whole set in memory). 500 comfortably covers the list-page UI (which paginates
        // and filters anyway); anyone who genuinely needs to pull every workflow programmatically
        // should use /api/workflows/export instead.
        const int HardLimitWorkflows = 500;

        // RBAC list-filter: collapse to "every workflow whose folder I can read". Global
        // Admin gets the unrestricted set and skips the IN-clause.
        var accessibleFolders = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        var query = _db.Workflows.AsNoTracking().AsQueryable();
        if (!accessibleFolders.IsUnrestricted)
        {
            if (accessibleFolders.FolderIds.Count == 0)
                return Ok(new List<WorkflowResponse>());
            query = query.Where(w => accessibleFolders.FolderIds.Contains(w.FolderId));
        }
        var workflows = await query
            .OrderByDescending(w => w.UpdatedAt)
            .Take(HardLimitWorkflows)
            .ToListAsync(ct);

        var wfIds = workflows.Select(w => w.Id).ToList();
        List<WorkflowExecutionListRow> executionWindow;
        if (wfIds.Count == 0)
        {
            executionWindow = [];
        }
        else
        {
            // Replaces an O(M^2) correlated subquery with a ROW_NUMBER window function.
            // Old shape:
            //
            //   SELECT … FROM WorkflowExecutions e
            //   WHERE e.WorkflowId IN (…)
            //     AND (SELECT COUNT(*) FROM WorkflowExecutions
            //          WHERE WorkflowId = e.WorkflowId AND StartedAt > e.StartedAt) < 20
            //
            // …grew quadratically with the executions table (45k rows in the user's setup
            // → ~14 s response time). The window function does a single ranked scan,
            // emitting only the top-20 per workflow.
            //
            // Window functions are supported by SQL Server ≥ 2012 and PostgreSQL ≥ 8.4 —
            // both supported App-DB-Provider. Status is mapped to a string column
            // (HasConversion<string>()), so the raw query returns it as string and we
            // re-parse to the enum after fetching.
            // Identifiers are double-quoted so PostgreSQL preserves the PascalCase table/column
            // names emitted by EF (unquoted PG identifiers fold to lowercase, which would point
            // at a non-existent `workflowexecutions` relation). SQL Server also accepts
            // double-quoted identifiers (as long as QUOTED_IDENTIFIER is ON, which is the default).
            var idPlaceholders = string.Join(",", wfIds.Select((_, i) => $"{{{i + 1}}}"));
            var sql = $@"
                SELECT ""Id"", ""WorkflowId"", ""Status"", ""StartedAt"", ""CompletedAt""
                FROM (
                    SELECT ""Id"", ""WorkflowId"", ""Status"", ""StartedAt"", ""CompletedAt"",
                           ROW_NUMBER() OVER (PARTITION BY ""WorkflowId"" ORDER BY ""StartedAt"" DESC, ""Id"" DESC) AS rn
                    FROM ""WorkflowExecutions""
                    WHERE ""WorkflowId"" IN ({idPlaceholders})
                ) AS ranked
                WHERE rn <= {{0}}";
            var sqlParams = new object[] { StatsWindow }.Concat(wfIds.Cast<object>()).ToArray();

            var raw = await _db.Database
                .SqlQueryRaw<WorkflowExecutionListRowRaw>(sql, sqlParams)
                .ToListAsync(ct);

            executionWindow = raw
                .Select(r => new WorkflowExecutionListRow(
                    r.Id, r.WorkflowId, ParseStatus(r.Status), r.StartedAt, r.CompletedAt))
                .ToList();
        }

        var execsByWf = executionWindow
            .GroupBy(e => e.WorkflowId)
            .ToDictionary(g => g.Key, g => g
                .OrderByDescending(e => e.StartedAt)
                .ThenByDescending(e => e.Id)
                .Take(StatsWindow)
                .ToList());

        // Resolve all distinct lock-owner ids in a single query — O(1) DB roundtrip
        // regardless of workflow count.
        var lockOwnerIds = workflows
            .Where(w => w.CheckedOutByUserId.HasValue)
            .Select(w => w.CheckedOutByUserId!.Value)
            .Distinct()
            .ToList();
        var lockOwnerNames = lockOwnerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking()
                .Where(u => lockOwnerIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.Username, ct);

        // RBAC: pre-resolve capabilities + paths for every distinct folder appearing in
        // the result set. The per-request cache in ResourceAuthorizationService ensures
        // each folder is walked at most once even before this batch step; doing it
        // up-front keeps the per-row Select() purely synchronous.
        var distinctFolderIds = workflows.Select(w => w.FolderId).Distinct().ToList();
        var folderCaps = new Dictionary<Guid, ResourceCapabilities>();
        foreach (var fid in distinctFolderIds)
            folderCaps[fid] = await _authz.GetWorkflowCapabilitiesAsync(User, fid, ct);
        var pathLookup = await _db.SharedWorkflowFolders.AsNoTracking()
            .Where(f => distinctFolderIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Path })
            .ToDictionaryAsync(x => x.Id, x => x.Path, ct);

        var responses = workflows.Select(w =>
        {
            var activityCount = w.ActivityCount;
            List<string> triggerTypes;
            try { triggerTypes = w.TriggerTypesJson is null ? [] : JsonSerializer.Deserialize<List<string>>(w.TriggerTypesJson) ?? []; }
            catch { triggerTypes = []; }
            execsByWf.TryGetValue(w.Id, out var wfExecs);
            wfExecs ??= new();

            var latest = wfExecs.FirstOrDefault();
            LastExecutionInfo? lastInfo = latest is null ? null : new LastExecutionInfo(
                latest.Id,
                latest.Status.ToString(),
                latest.StartedAt,
                latest.CompletedAt,
                latest.CompletedAt.HasValue
                    ? (long?)(latest.CompletedAt.Value - latest.StartedAt).TotalMilliseconds
                    : null);

            var window = wfExecs.Take(StatsWindow).ToList();
            var terminal = window
                .Where(e => e.Status == ExecutionStatus.Succeeded
                         || e.Status == ExecutionStatus.Failed
                         || e.Status == ExecutionStatus.Cancelled)
                .ToList();
            int successCount = terminal.Count(e => e.Status == ExecutionStatus.Succeeded);
            int totalCount = terminal.Count;

            var succWithDuration = terminal
                .Where(e => e.Status == ExecutionStatus.Succeeded && e.CompletedAt.HasValue)
                .Select(e => (e.CompletedAt!.Value - e.StartedAt).TotalMilliseconds)
                .ToList();
            double? avgMs = succWithDuration.Count > 0 ? succWithDuration.Average() : null;

            string? lockOwnerName = null;
            if (w.CheckedOutByUserId.HasValue)
                lockOwnerNames.TryGetValue(w.CheckedOutByUserId.Value, out lockOwnerName);

            folderCaps.TryGetValue(w.FolderId, out var caps);
            pathLookup.TryGetValue(w.FolderId, out var path);
            return ToScopedResponse(w, lockOwnerName, caps, path) with
            {
                ActivityCount = activityCount,
                TriggerTypes = triggerTypes,
                LastExecution = lastInfo,
                SuccessCount = successCount,
                TotalCount = totalCount,
                AvgDurationMs = avgMs
            };
        }).ToList();

        return Ok(responses);
    }

    private sealed record WorkflowExecutionListRow(
        Guid Id,
        Guid WorkflowId,
        ExecutionStatus Status,
        DateTime StartedAt,
        DateTime? CompletedAt);

    /// <summary>
    /// Raw projection used for the ROW_NUMBER raw-SQL query. Status is read as a string
    /// because EF's HasConversion mapping does not apply to <c>SqlQueryRaw&lt;T&gt;</c>
    /// result types — we re-parse it in C# via <see cref="ParseStatus"/>.
    /// </summary>
    private sealed record WorkflowExecutionListRowRaw(
        Guid Id,
        Guid WorkflowId,
        string Status,
        DateTime StartedAt,
        DateTime? CompletedAt);

    private static ExecutionStatus ParseStatus(string value) => value switch
    {
        "Pending" => ExecutionStatus.Pending,
        "Running" => ExecutionStatus.Running,
        "Succeeded" => ExecutionStatus.Succeeded,
        "Failed" => ExecutionStatus.Failed,
        "Cancelled" => ExecutionStatus.Cancelled,
        "Paused" => ExecutionStatus.Paused,
        "Skipped" => ExecutionStatus.Skipped,
        _ => Enum.TryParse<ExecutionStatus>(value, ignoreCase: true, out var parsed)
            ? parsed
            : ExecutionStatus.Pending,
    };

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkflowResponse>> GetById(Guid id, CancellationToken ct)
    {
        var w = await _db.Workflows.FindAsync([id], ct);
        if (w is null) return NotFound();
        if (await RequireWorkflowAccessAsync(w, ResourceOp.Read, ct) is { } denied) return denied;

        var lockOwnerName = await ResolveLockOwnerNameAsync(w, ct);
        return Ok(await ToScopedResponseAsync(w, lockOwnerName, ct));
    }

    /// <summary>
    /// Lookup a workflow by its name — exact-case wins, otherwise case-insensitive;
    /// ambiguous names return 409. Used by the designer's sub-workflow inline preview
    /// when a startWorkflow node references a child by name rather than GUID.
    /// </summary>
    [HttpGet("by-name/{name}")]
    public async Task<ActionResult<WorkflowResponse>> GetByName(string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return NotFound();
        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        var query = _db.Workflows.AsNoTracking().AsQueryable();
        if (!accessible.IsUnrestricted)
        {
            if (accessible.FolderIds.Count == 0) return NotFound();
            query = query.Where(w => accessible.FolderIds.Contains(w.FolderId));
        }
        var result = await WorkflowNameResolver.ResolveByNameAsync(query, name, ct);
        if (result.Outcome == WorkflowNameResolver.Outcome.Ambiguous)
            return Conflict(new { message = $"Multiple workflows named '{name.Trim()}' — disambiguate with the GUID." });
        if (result.Workflow is not { } w) return NotFound();
        if (await RequireWorkflowAccessAsync(w, ResourceOp.Read, ct) is { } denied) return denied;

        var lockOwnerName = await ResolveLockOwnerNameAsync(w, ct);
        return Ok(await ToScopedResponseAsync(w, lockOwnerName, ct));
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Operator")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBodyBytes, ValueLengthLimit = (int)MaxRequestBodyBytes)]
    public async Task<ActionResult<WorkflowResponse>> Create(CreateWorkflowRequest request, CancellationToken ct)
    {
        if (ValidateDefinitionJson(request.DefinitionJson) is { } sizeError) return sizeError;
        LintAndLogWarnings(request.DefinitionJson);

        // RBAC Stufe A — pin the target folder and verify Edit on it before persisting.
        // Without this check a global Operator with no FolderEditor grant on Root would
        // still be able to drop a workflow into Root just by hitting POST /api/workflows.
        var folderId = request.FolderId ?? SharedWorkflowFolder.RootFolderId;
        if (await RequireFolderAccessAsync(folderId, ResourceOp.Edit, ct) is { } folderDenied)
            return folderDenied;
        if (request.FolderId is not null
            && !await _db.SharedWorkflowFolders.AsNoTracking().AnyAsync(f => f.Id == folderId, ct))
        {
            return BadRequest(new { message = "folderId does not exist" });
        }
        // A freshly created workflow has nothing to fire on yet, so we treat it as a draft:
        // disabled and already locked-by-creator. The editor opens straight in edit mode
        // (no extra POST /lock roundtrip, no race where the user briefly sees a read-only
        // canvas before the auto-lock effect runs). User publishes when ready.
        var creatorId = this.GetCurrentUserId();
        var creatorUsername = this.GetCurrentUsername();
        var now = DateTime.UtcNow;
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            DefinitionJson = request.DefinitionJson,
            FolderId = folderId,
            IsEnabled = false,
            CheckedOutByUserId = creatorId,
            CheckedOutAt = creatorId is null ? null : now,
            CreatedBy = creatorUsername
        };
        PopulateComputedColumns(workflow);

        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.WorkflowCreated, "Workflow", workflow.Id,
            AuditDetails.Json(("name", workflow.Name), ("folderId", folderId.ToString())), ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, "create"),
            new("result", "success"));

        return CreatedAtAction(nameof(GetById), new { id = workflow.Id },
            await ToScopedResponseAsync(workflow, creatorId is null ? null : creatorUsername, ct));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = "Admin,Operator")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBodyBytes, ValueLengthLimit = (int)MaxRequestBodyBytes)]
    public async Task<IActionResult> Update(Guid id, UpdateWorkflowRequest request, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, ResourceOp.Edit, ct) is { } denied) return denied;

        if (await EnsureWriteLockAsync(workflow, ct) is { } locked) return locked;
        var lockOwnerId = this.GetCurrentUserId();

        var sizeError = ValidateDefinitionJson(request.DefinitionJson);
        if (sizeError is not null) return sizeError;
        LintAndLogWarnings(request.DefinitionJson);

        // Snapshot the pre-update state and mutate as one transaction. The guarded update
        // includes both the edit-lock owner and observed version, closing the load/check/write
        // race without relying on the EF change tracker.
        var updatedAt = DateTime.UtcNow;
        var updatedBy = this.GetCurrentUsername();
        var computed = new Workflow { DefinitionJson = request.DefinitionJson };
        PopulateComputedColumns(computed);

        int affected;
        try
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            affected = await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear(); // retry-safe: drop any snapshot added by a prior attempt
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                _db.WorkflowVersions.Add(new WorkflowVersion
                {
                    Id = Guid.NewGuid(),
                    WorkflowId = workflow.Id,
                    Version = workflow.Version,
                    Name = workflow.Name,
                    Description = workflow.Description,
                    DefinitionJson = workflow.DefinitionJson,
                    CreatedAt = updatedAt,
                    CreatedBy = workflow.CreatedBy ?? updatedBy,
                });
                await _db.SaveChangesAsync(ct);

                var rows = await _db.Workflows
                    .Where(w => w.Id == id
                                && w.CheckedOutByUserId == lockOwnerId
                                && w.CheckedOutAt == workflow.CheckedOutAt
                                && w.FolderId == workflow.FolderId
                                && w.Version == workflow.Version)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(w => w.Name, request.Name)
                        .SetProperty(w => w.Description, request.Description)
                        .SetProperty(w => w.DefinitionJson, request.DefinitionJson)
                        .SetProperty(w => w.Version, workflow.Version + 1)
                        .SetProperty(w => w.UpdatedAt, updatedAt)
                        .SetProperty(w => w.UpdatedBy, updatedBy)
                        .SetProperty(w => w.TriggerTypesJson, computed.TriggerTypesJson)
                        .SetProperty(w => w.ActivityCount, computed.ActivityCount), ct);

                if (rows == 0)
                {
                    await tx.RollbackAsync(ct);
                    return 0;
                }

                await tx.CommitAsync(ct);
                return rows;
            });
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _db.ChangeTracker.Clear();
            // Snapshot already captured by a concurrent request. Do not write over it; force
            // the editor to reload so the user sees the current version.
            return Conflict(new
            {
                code = "workflow_version_conflict",
                message = "Workflow was updated concurrently. Reload the workflow and retry your change.",
                currentVersion = await _db.Workflows.AsNoTracking()
                    .Where(w => w.Id == id)
                    .Select(w => w.Version)
                    .FirstOrDefaultAsync(ct),
            });
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }

        if (affected == 0)
        {
            return await WorkflowMutationConflictAsync(id, ct);
        }

        await _audit.LogAsync(AuditActions.WorkflowUpdated, "Workflow", workflow.Id,
            AuditDetails.Json(("version", (workflow.Version + 1).ToString()), ("name", request.Name)), ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, "update"),
            new("result", "success"));

        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, ResourceOp.Edit, ct) is { } denied) return denied;

        // M-3 (security audit 2026-05-15): atomic guarded delete. A row is removed only when
        // nobody holds the lock OR the caller is the lock owner — the WHERE guard closes the
        // load→EnsureWriteLockAsync→SaveChanges TOCTOU window where a foreign lock could be
        // acquired between the in-memory check and the write. Child rows (executions, versions,
        // stats) cascade at the DB level, exactly as the previous Remove()+SaveChanges did.
        var meId = this.GetCurrentUserId();
        var deleted = await _db.Workflows
            .Where(w => w.Id == id
                        && w.FolderId == workflow.FolderId
                        && w.CheckedOutByUserId == workflow.CheckedOutByUserId
                        && w.CheckedOutAt == workflow.CheckedOutAt
                        && (w.CheckedOutByUserId == null || w.CheckedOutByUserId == meId))
            .ExecuteDeleteAsync(ct);

        if (deleted == 0)
        {
            // Either it vanished, or a foreign lock was taken between the read and the delete.
            var current = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
            if (current is null) return NotFound();
            if (await RequireWorkflowAccessAsync(current, ResourceOp.Edit, ct) is { } deniedNow) return deniedNow;
            if (await EnsureWriteLockAsync(current, ct) is { } locked) return locked;
            return NotFound();
        }

        await _audit.LogAsync(AuditActions.WorkflowDeleted, "Workflow", id,
            AuditDetails.Json(("name", workflow.Name)), ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, "delete"),
            new("result", "success"));

        return NoContent();
    }

    /// <summary>
    /// Enables a workflow — flipping <c>IsEnabled=true</c>. Trigger Orchestrator picks it
    /// up on the next 5-second sync and starts honoring the workflow's triggers.
    /// Idempotent: returning 204 whether the workflow was already enabled or just flipped.
    /// Rejects with 423 if any user (including the caller) currently has the workflow
    /// checked out for editing — Enable-while-locked is semantically nonsense (locked = disabled).
    /// </summary>
    [HttpPost("{id:guid}/enable")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
    {
        var workflow = await _db.Workflows.FindAsync([id], ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, ResourceOp.Edit, ct) is { } denied) return denied;
        if (workflow.CheckedOutByUserId is not null)
        {
            return await EnableLockedResultAsync(workflow, ct);
        }
        if (NodePilot.Api.Security.WebhookHmacSecurity.ValidateDefinition(workflow.DefinitionJson) is { } hmacError)
        {
            return BadRequest(new
            {
                code = "weak_webhook_hmac_secret",
                message = hmacError,
            });
        }
        return await SetEnabled(workflow, true, requireUnlocked: true, ct);
    }

    /// <summary>
    /// Disables a workflow — flipping <c>IsEnabled=false</c>. External triggers stop firing
    /// within 5 s (Orchestrator sync window); in-flight executions keep running to completion.
    /// Manual <c>/execute</c> calls refuse to dispatch a disabled workflow — this is the kill
    /// switch for incident response ("stop that thing from firing again, fix it later").
    /// </summary>
    [HttpPost("{id:guid}/disable")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        var workflow = await _db.Workflows.FindAsync([id], ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, ResourceOp.Edit, ct) is { } denied) return denied;
        return await SetEnabled(workflow, false, requireUnlocked: false, ct);
    }

    private async Task<IActionResult> SetEnabled(Workflow workflow, bool enabled, bool requireUnlocked, CancellationToken ct)
    {
        if (workflow.IsEnabled == enabled)
            return NoContent(); // already in desired state; don't audit a no-op

        var updatedAt = DateTime.UtcNow;
        var updatedBy = this.GetCurrentUsername();
        var query = _db.Workflows.Where(w => w.Id == workflow.Id
                                             && w.FolderId == workflow.FolderId
                                             && w.IsEnabled != enabled);
        if (requireUnlocked)
            query = query.Where(w => w.CheckedOutByUserId == null);

        var affected = await query.ExecuteUpdateAsync(setters => setters
            .SetProperty(w => w.IsEnabled, enabled)
            .SetProperty(w => w.UpdatedAt, updatedAt)
            .SetProperty(w => w.UpdatedBy, updatedBy), ct);

        if (affected == 0)
        {
            var current = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflow.Id, ct);
            if (current is null) return NotFound();
            if (await RequireWorkflowAccessAsync(current, ResourceOp.Edit, ct) is { } deniedNow) return deniedNow;
            if (current.IsEnabled == enabled) return NoContent();
            if (requireUnlocked && current.CheckedOutByUserId is not null)
                return await EnableLockedResultAsync(current, ct);

            return Conflict(new
            {
                code = "workflow_state_conflict",
                message = "Workflow state changed concurrently. Reload the workflow and retry.",
            });
        }

        _db.ChangeTracker.Clear();

        await _audit.LogAsync(
            enabled ? AuditActions.WorkflowEnabled : AuditActions.WorkflowDisabled,
            "Workflow", workflow.Id,
            AuditDetails.Json(("name", workflow.Name)), ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, enabled ? "enable" : "disable"),
            new("result", "success"));

        return NoContent();
    }

    private async Task<IActionResult> WorkflowMutationConflictAsync(Guid id, CancellationToken ct)
    {
        var current = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (current is null) return NotFound();
        if (await RequireWorkflowAccessAsync(current, ResourceOp.Edit, ct) is { } deniedNow) return deniedNow;
        if (await EnsureWriteLockAsync(current, ct) is { } locked) return locked;

        return Conflict(new
        {
            code = "workflow_version_conflict",
            message = "Workflow was updated concurrently. Reload the workflow and retry your change.",
            currentVersion = current.Version,
        });
    }

    private async Task<IActionResult> EnableLockedResultAsync(Workflow workflow, CancellationToken ct)
    {
        var ownerName = await ResolveLockOwnerNameAsync(workflow, ct);
        return new ObjectResult(new
        {
            message = $"Cannot enable while workflow is checked out for editing (by {ownerName ?? "another user"}). Publish or check in first.",
            lockedByUserId = workflow.CheckedOutByUserId,
            lockedByUserName = ownerName,
            lockedAt = workflow.CheckedOutAt,
        }) { StatusCode = StatusCodes.Status423Locked };
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
               || message.Contains("IX_WorkflowVersions", StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost("{id:guid}/duplicate")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<ActionResult<WorkflowResponse>> Duplicate(Guid id, CancellationToken ct)
    {
        var source = await _db.Workflows.FindAsync([id], ct);
        if (source is null) return NotFound();
        // Duplicate = read source + create new in same folder; both checks against the
        // same folder. The new workflow lands in the source's folder, so Edit on that
        // folder is the right gate.
        if (await RequireWorkflowAccessAsync(source, ResourceOp.Edit, ct) is { } denied) return denied;

        var copy = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = source.Name + " (Copy)",
            Description = source.Description,
            DefinitionJson = source.DefinitionJson,
            FolderId = source.FolderId,
            Version = 1,
            // L-6 (security audit 2026-05-15): a duplicate is always born disabled, regardless
            // of the source's state. Copying IsEnabled=true would let a user clone a locked /
            // under-review workflow into an immediately-firing one, side-stepping the edit-lock.
            // The operator must explicitly Enable/Publish the copy after reviewing it.
            IsEnabled = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        copy.CreatedBy = this.GetCurrentUsername();
        _db.Workflows.Add(copy);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditActions.WorkflowDuplicated, "Workflow", copy.Id,
            AuditDetails.Json(("sourceId", source.Id), ("name", copy.Name)), ct);

        ApiMetrics.WorkflowOperations.Add(1,
            new(TelemetryConstants.Attributes.WorkflowOperation, "duplicate"),
            new("result", "success"));

        // Build with the async-scoped helper so capabilities/folderPath are correctly
        // populated for the duplicate (default-deny otherwise — see WorkflowResponse).
        var response = await ToScopedResponseAsync(copy, lockOwnerUserName: null, ct);
        return CreatedAtAction(nameof(GetById), new { id = copy.Id }, response);
    }
}

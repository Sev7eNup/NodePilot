using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.Validation;
using NodePilot.Data;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Shared infrastructure for the workflow controller family
/// (<see cref="WorkflowsController"/>, <see cref="WorkflowEditingController"/>,
/// <see cref="WorkflowImportExportController"/>). Holds DI dependencies, JSON / lock /
/// linter helpers, and the secret-redaction logic that every workflow-touching endpoint
/// must apply identically.
///
/// <para>The split exists because the original monolithic <c>WorkflowsController</c> grew
/// to ~1250 LOC across three distinct sub-domains (CRUD/lifecycle, edit-lock + versions,
/// import/export). All three keep the same <c>api/workflows</c> route prefix, so the
/// HTTP surface is unchanged.</para>
/// </summary>
public abstract class WorkflowsControllerBase : ControllerBase
{
    // Hard ceilings for a workflow definition JSON. 5 MiB is well above any real-world
    // workflow (hundreds of nodes + full PowerShell scripts inline) while being small
    // enough that a single request cannot OOM the process or make the TriggerOrchestrator
    // parse-scan pathologically slow. MaxDepth guards against deeply-nested JSON bombs.
    protected const int MaxDefinitionJsonBytes = 5 * 1024 * 1024;
    protected const int MaxDefinitionJsonDepth = 64;

    // Request-body cap for Create/Update/Import endpoints. A little larger than the JSON-
    // definition cap to leave room for envelope overhead. Without this attribute the default
    // ASP.NET 30 MiB body limit lets a single authenticated request allocate 6x more memory
    // than ValidateDefinitionJson later reports as "too large".
    protected const long MaxRequestBodyBytes = 6 * 1024 * 1024;

    // Secret-bearing config keys on trigger/activity nodes. When a workflow is exported —
    // OR returned to any role below Admin — the value for these keys is masked so webhook
    // secrets, API keys, and inline DB-connection passwords stay confined to who needs them.
    // connectionString is included because SqlActivity accepts inline Server=..;Password=..
    // by default; redacting on read matches what export already does.
    // Single source of truth for the secret-bearing config keys now lives in
    // WorkflowDefinitionSecretRewriter — the system backup (ADR 0001) and this
    // share/redaction path must agree on what counts as a secret.
    protected static readonly IReadOnlySet<string> SecretConfigKeys =
        NodePilot.Api.Services.Backup.WorkflowDefinitionSecretRewriter.SecretConfigKeys;

    // Fail-closed fallback (security-audit finding F-2): if secret redaction can't run, we
    // return this empty shell to non-privileged callers rather than risk leaking the raw
    // definition. Its shape matches what the React-Flow designer needs to render an empty
    // board without crashing the JSON.parse path on the client.
    private const string UnreadableDefinitionShell = "{\"nodes\":[],\"edges\":[],\"definitionUnreadable\":true}";

    protected readonly NodePilotDbContext _db;
    protected readonly ILogger _logger;
    protected readonly IAuditWriter _audit;
    protected readonly IResourceAuthorizationService _authz;

    protected WorkflowsControllerBase(NodePilotDbContext db, ILogger logger, IAuditWriter audit,
        IResourceAuthorizationService authz)
    {
        _db = db;
        _logger = logger;
        _audit = audit;
        _authz = authz;
    }

    /// <summary>
    /// RBAC gate for workflow-shaped endpoints — convenience forwarder to the shared
    /// <see cref="ResourceAuthorizationGateExtensions.RequireWorkflowAccessAsync"/> gate
    /// (404 masks existence when the caller can't read the folder; 403 when the caller can
    /// read but lacks the requested op; <c>null</c> = access permitted).
    /// </summary>
    protected Task<ActionResult?> RequireWorkflowAccessAsync(Workflow workflow, ResourceOp op, CancellationToken ct)
        => ResourceAuthorizationGateExtensions.RequireWorkflowAccessAsync(this, _authz, workflow, op, ct);

    /// <summary>Folder-shaped variant of <see cref="RequireWorkflowAccessAsync"/>.</summary>
    protected Task<ActionResult?> RequireFolderAccessAsync(Guid folderId, ResourceOp op, CancellationToken ct)
        => ResourceAuthorizationGateExtensions.RequireFolderAccessAsync(this, _authz, folderId, op, ct);

    /// <summary>
    /// Returns the <c>DefinitionJson</c> shape appropriate for the caller. Raw definitions
    /// are an effective resource capability: only callers with Edit on this workflow may
    /// receive inline secrets. Read-only callers get a copy with <see cref="SecretConfigKeys"/>
    /// values masked to "***" so that read-only accounts cannot exfiltrate webhook secrets,
    /// inline API keys, or connection-string passwords via the workflow-detail endpoint.
    /// </summary>
    protected string ScopedDefinitionJson(string raw, bool canEdit = false)
    {
        if (canEdit)
            return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var redacted = RedactSecretsInDefinition(doc.RootElement);
            return redacted.GetRawText();
        }
        catch (Exception ex)
        {
            // Fail-closed: a Viewer-role caller MUST NOT receive a definition we couldn't
            // redact. Returning the raw JSON here would leak webhook secrets / inline API
            // keys exactly along the pivot path the redaction was added to close.
            _logger.LogWarning(ex,
                "ScopedDefinitionJson: failed to parse workflow definition; returning empty shell to non-privileged caller");
            return UnreadableDefinitionShell;
        }
    }

    /// <summary>
    /// Builds a <see cref="WorkflowResponse"/> with role-scoped definition JSON, RBAC
    /// capabilities, and folder metadata. Used by every endpoint that returns a single
    /// workflow (GET, Create, Update, Rollback); list endpoints layer stats on top via
    /// the with-syntax.
    /// </summary>
    protected WorkflowResponse ToScopedResponse(Workflow w, string? lockOwnerUserName = null,
        ResourceCapabilities? capabilities = null, string? folderPath = null) => new(
        w.Id, w.Name, w.Description,
        ScopedDefinitionJson(w.DefinitionJson, capabilities?.CanEdit == true),
        w.Version, w.IsEnabled, w.CreatedAt, w.UpdatedAt, w.CreatedBy, w.UpdatedBy)
    {
        CheckedOutByUserId = w.CheckedOutByUserId,
        CheckedOutByUserName = lockOwnerUserName,
        CheckedOutAt = w.CheckedOutAt,
        FolderId = w.FolderId,
        FolderPath = folderPath,
        // Default-deny when the caller doesn't supply caps. The previous "all true"
        // default masked bugs where an endpoint forgot to call ToScopedResponseAsync.
        Capabilities = capabilities is null
            ? new WorkflowCapabilities(false, false, false, false, false)
            : new WorkflowCapabilities(capabilities.CanRead, capabilities.CanRun, capabilities.CanEdit, capabilities.CanDelete, capabilities.CanAdmin),
    };

    /// <summary>
    /// Async helper that resolves capabilities for a workflow and builds the scoped
    /// response. Endpoints that don't need separate capability lookup (because they're
    /// already inside a per-request scope where the cache is warm) call this for the
    /// most common case.
    /// </summary>
    protected async Task<WorkflowResponse> ToScopedResponseAsync(Workflow w, string? lockOwnerUserName,
        CancellationToken ct)
    {
        var caps = await _authz.GetWorkflowCapabilitiesAsync(User, w.FolderId, ct);
        var folderPath = await _db.SharedWorkflowFolders.AsNoTracking()
            .Where(f => f.Id == w.FolderId)
            .Select(f => f.Path).FirstOrDefaultAsync(ct);
        return ToScopedResponse(w, lockOwnerUserName, caps, folderPath);
    }

    /// <summary>
    /// Resolves the lock-owner's username for a single workflow (no-op when not locked).
    /// One small query — cheap enough to call inline from single-row endpoints.
    /// </summary>
    protected async Task<string?> ResolveLockOwnerNameAsync(Workflow w, CancellationToken ct)
    {
        if (w.CheckedOutByUserId is null) return null;
        return await _db.Users.AsNoTracking()
            .Where(u => u.Id == w.CheckedOutByUserId.Value)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Returns 423 Locked if the caller does not hold the workflow's edit-lock.
    /// <c>null</c> means "OK to mutate" — caller proceeds. Body carries the lock owner so the
    /// UI can render "Locked by Alice" without a follow-up query.
    /// </summary>
    protected async Task<ActionResult?> EnsureWriteLockAsync(Workflow w, CancellationToken ct)
    {
        var meId = this.GetCurrentUserId();
        if (w.CheckedOutByUserId == meId && meId is not null) return null;
        var ownerName = await ResolveLockOwnerNameAsync(w, ct);
        return new ObjectResult(new
        {
            message = w.CheckedOutByUserId is null
                ? "Workflow is not checked out for editing — click 'Bearbeiten' to start editing."
                : $"Workflow is locked by {ownerName ?? "another user"}.",
            lockedByUserId = w.CheckedOutByUserId,
            lockedByUserName = ownerName,
            lockedAt = w.CheckedOutAt,
            isYours = false,
        })
        {
            StatusCode = StatusCodes.Status423Locked,
        };
    }

    /// <summary>
    /// Reject oversized, pathologically-nested, or structurally invalid workflow definitions
    /// before they land in the DB. Returns null on success; returns a
    /// <c>BadRequestObjectResult</c> when the JSON violates a limit or the parser contract.
    /// Callers should early-return the result.
    /// </summary>
    protected BadRequestObjectResult? ValidateDefinitionJson(string? definitionJson)
    {
        if (string.IsNullOrEmpty(definitionJson))
            return BadRequest(new { message = "definitionJson is required" });
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(definitionJson);
        if (byteCount > MaxDefinitionJsonBytes)
            return BadRequest(new { message = $"definitionJson exceeds {MaxDefinitionJsonBytes} bytes ({byteCount} given)" });
        try
        {
            using var doc = JsonDocument.Parse(definitionJson,
                new JsonDocumentOptions { MaxDepth = MaxDefinitionJsonDepth });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return BadRequest(new { message = "definitionJson must be a JSON object" });

            var semanticValidation = WorkflowDefinitionValidator.Validate(doc.RootElement);
            if (!semanticValidation.IsValid)
                return BadRequest(new { message = $"definitionJson is structurally invalid: {semanticValidation.Error}" });
        }
        catch (JsonException ex)
        {
            return BadRequest(new { message = $"definitionJson is not valid JSON (or exceeds max depth {MaxDefinitionJsonDepth}): {ex.Message}" });
        }
        return null;
    }

    /// <summary>
    /// Populates the denormalized <see cref="Workflow.TriggerTypesJson"/> and
    /// <see cref="Workflow.ActivityCount"/> columns from the current
    /// <see cref="Workflow.DefinitionJson"/>. Call this whenever DefinitionJson changes
    /// (Create / Update / Publish / Rollback / Import) so the list and dashboard endpoints
    /// can skip a full JSON parse on every read.
    /// </summary>
    protected static void PopulateComputedColumns(Workflow workflow)
        => WorkflowMetadata.PopulateComputedColumns(workflow);

    /// <summary>
    /// Run the script linter over every <c>runScript</c> node. Warnings are logged but not
    /// raised as errors — the workflow author may have a legitimate need (e.g. SCOrch
    /// migration parity) for the flagged patterns. Audits pick up the log entries during
    /// review.
    /// </summary>
    protected void LintAndLogWarnings(string definitionJson)
    {
        foreach (var w in NodePilot.Api.Security.WorkflowScriptLinter.Lint(definitionJson))
        {
            _logger.LogWarning("Workflow script linter: step {StepId} rule {Rule}: {Message}",
                w.StepId, w.Rule, w.Message);
        }
    }

    /// <summary>
    /// Deep-clones the workflow definition while replacing string values for known
    /// secret-bearing config keys (webhook <c>secret</c>, <c>apiKey</c>, <c>password</c>,
    /// …) with the placeholder "***". Structure-preserving so the resulting JSON remains
    /// re-importable (the user has to fill the real value in on the target system).
    /// </summary>
    protected static JsonElement RedactSecretsInDefinition(JsonElement root)
    {
        // Delegates to the shared rewriter (see ADR 0001, the system-backup design) so this
        // redaction path and the backup's encrypt-for-backup mode walk the exact same
        // structure with the same list of secret-bearing keys.
        var node = NodePilot.Api.Services.Backup.WorkflowDefinitionSecretRewriter.Rewrite(
            root, NodePilot.Api.Services.Backup.SecretHandling.Redact, protector: null);
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }
}

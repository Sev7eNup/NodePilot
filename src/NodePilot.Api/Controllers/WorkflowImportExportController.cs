using System.Diagnostics;
using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Workflow data exchange — JSON envelope export/import plus the SCOrch <c>.ois_export</c>
/// XML migration path. Sibling controllers: <see cref="WorkflowsController"/> (CRUD/lifecycle),
/// <see cref="WorkflowEditingController"/> (edit-lock + versions).
/// </summary>
[ApiController]
[Route("api/workflows")]
[Authorize]
public class WorkflowImportExportController : WorkflowsControllerBase
{
    private readonly NodePilot.Core.Interfaces.IGlobalVariableStore _globals;

    public WorkflowImportExportController(
        NodePilotDbContext db,
        ILogger<WorkflowImportExportController> logger,
        IAuditWriter audit,
        NodePilot.Core.Interfaces.IResourceAuthorizationService authz,
        NodePilot.Core.Interfaces.IGlobalVariableStore globals)
        : base(db, logger, audit, authz)
    {
        _globals = globals;
    }

    [HttpGet("export")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> ExportAll(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // RBAC: export only what the caller may read. Global Admin gets everything.
        var accessible = await _authz.GetAccessibleFolderIdsAsync(User, ct);
        var all = _db.Workflows.AsNoTracking();
        // A restricted user with zero accessible folders still gets a (valid, empty)
        // envelope rather than an early-return — the audit emission below must run for
        // the empty case too. An attempted catalogue-pull from a viewer who lost their
        // last grant is exactly the SIEM signal "WORKFLOW_EXPORTED_BULK count=0" is for.
        var query = all.ScopeToAccessibleFolders(accessible) ?? all.Where(_ => false);
        var workflows = await query
            .OrderBy(w => w.Name)
            .ToListAsync(ct);

        var envelope = new WorkflowExportEnvelope(
            Schema: "nodepilot-workflow-export/v1",
            ExportVersion: 1,
            ExportedAt: DateTime.UtcNow,
            Workflow: null,
            Workflows: workflows.Select(ToExportItem).ToList());

        sw.Stop();
        ApiMetrics.ImportExportOperations.Add(1,
            new(TelemetryConstants.Attributes.ImportExportOperation, "export_all"),
            new("result", "success"));
        ApiMetrics.ImportExportDuration.Record(sw.Elapsed.TotalMilliseconds,
            new(TelemetryConstants.Attributes.ImportExportOperation, "export_all"),
            new("result", "success"));

        // Bulk export is the most interesting SIEM signal in this controller — somebody
        // just downloaded the entire workflow catalogue. Distinct verb (not _EXPORTED) so
        // a detection rule can alert on "WORKFLOW_EXPORTED_BULK > 0 per user per day"
        // without false positives from routine single-workflow exports. Empty results
        // (RBAC restricted user with zero accessible folders) are audited too — a probing
        // attempt is still an event.
        await _audit.LogAsync(AuditActions.WorkflowExportedBulk, "Workflow", null,
            AuditDetails.Json(
                ("count", workflows.Count.ToString()),
                ("rbacScope", accessible.IsUnrestricted ? "all" : "restricted"),
                ("durationMs", sw.Elapsed.TotalMilliseconds.ToString("F0"))),
            ct);

        return ExportEnvelopeResult(envelope, $"nodepilot-workflows-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
    }

    [HttpGet("{id:guid}/export")]
    [Authorize(Roles = "Admin,Operator")]
    public async Task<IActionResult> ExportOne(Guid id, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
        if (workflow is null) return NotFound();
        if (await RequireWorkflowAccessAsync(workflow, NodePilot.Core.Interfaces.ResourceOp.Read, ct) is { } d) return d;

        var envelope = new WorkflowExportEnvelope(
            Schema: "nodepilot-workflow-export/v1",
            ExportVersion: 1,
            ExportedAt: DateTime.UtcNow,
            Workflow: ToExportItem(workflow),
            Workflows: null);

        sw.Stop();
        ApiMetrics.ImportExportOperations.Add(1,
            new(TelemetryConstants.Attributes.ImportExportOperation, "export_one"),
            new("result", "success"));
        ApiMetrics.ImportExportDuration.Record(sw.Elapsed.TotalMilliseconds,
            new(TelemetryConstants.Attributes.ImportExportOperation, "export_one"),
            new("result", "success"));

        await _audit.LogAsync(AuditActions.WorkflowExported, "Workflow", workflow.Id,
            AuditDetails.Json(
                ("name", workflow.Name),
                ("durationMs", sw.Elapsed.TotalMilliseconds.ToString("F0"))),
            ct);

        var safeName = SanitizeFilename(workflow.Name);
        return ExportEnvelopeResult(envelope, $"{safeName}.workflow.json");
    }

    [HttpPost("import")]
    [Authorize(Roles = "Admin,Operator")]
    // H-16: 600 MiB was a wildly inflated ceiling (single-file workflows max at ~6 MiB; 500-item
    // bulk imports in realistic deployments stay well under 40 MiB total). The prior cap let an
    // authenticated Operator tie up a request worker for a long parse against half a GiB of JSON.
    [RequestSizeLimit(40 * 1024 * 1024)] // 40 MiB bulk-import ceiling
    public async Task<ActionResult<ImportWorkflowsResponse>> Import(
        WorkflowExportEnvelope envelope, [FromQuery] Guid? folderId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (envelope is null) return BadRequest(new { error = "Body is required." });
        if (envelope.ExportVersion != 1)
            return BadRequest(new { error = $"Unsupported exportVersion: {envelope.ExportVersion}. Expected 1." });

        // Folder targeting: ?folderId= picks the destination (query param — the body is the
        // export envelope, which must stay a pure sharing artifact without instance-local
        // folder ids). Missing → Root. RBAC = Edit on the CHOSEN folder, so a folder-scoped
        // Operator without Root-Edit can import into their own folder.
        var targetFolderId = folderId ?? NodePilot.Core.Models.SharedWorkflowFolder.RootFolderId;
        if (await RequireFolderAccessAsync(targetFolderId, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } folderDenied)
            return folderDenied;
        if (folderId is not null
            && !await _db.SharedWorkflowFolders.AsNoTracking().AnyAsync(f => f.Id == targetFolderId, ct))
        {
            return BadRequest(new { error = "folderId does not exist" });
        }
        var items = new List<WorkflowExportItem>();
        if (envelope.Workflow is not null) items.Add(envelope.Workflow);
        if (envelope.Workflows is { Count: > 0 }) items.AddRange(envelope.Workflows);
        if (items.Count == 0)
            return BadRequest(new { error = "Neither 'workflow' nor 'workflows' was provided." });

        // Cap the bulk-import batch so a single request cannot drive the DB and TriggerOrchestrator
        // into a DoS. 500 workflows is far more than any realistic migration.
        const int MaxImportItems = 500;
        if (items.Count > MaxImportItems)
            return BadRequest(new { error = $"Too many workflows in one import (got {items.Count}, max {MaxImportItems})." });

        var existingNames = await _db.Workflows.AsNoTracking()
            .Select(w => w.Name).ToListAsync(ct);
        var takenNames = new HashSet<string>(existingNames, StringComparer.Ordinal);

        // Pre-collect webhook paths from already-installed workflows so an import with a
        // colliding webhookTrigger.path is auto-disabled rather than silently hijacking the
        // existing route. The caller can re-enable manually after resolving the collision.
        var takenWebhookKeys = await CollectWebhookPathsAsync(ct);

        var created = new List<ImportedWorkflowInfo>();
        var errors = new List<string>();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                errors.Add($"workflows[{i}]: name is required");
                continue;
            }
            if (item.Definition.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"workflows[{i}] ({item.Name}): definition must be an object");
                continue;
            }

            var definitionJson = item.Definition.GetRawText();
            if (ValidateDefinitionJson(definitionJson) is not null)
            {
                errors.Add($"workflows[{i}] ({item.Name}): definition is invalid or exceeds size/depth limits; skipped");
                continue;
            }
            var hmacError = NodePilot.Api.Security.WebhookHmacSecurity.ValidateDefinition(definitionJson);

            var finalName = UniqueName(item.Name, takenNames);
            takenNames.Add(finalName);

            // Respect the source's IsEnabled flag when the envelope carries it. Defaulting to
            // enabled=false when it doesn't is a deliberate safety choice: an import that comes
            // in already-enabled could start firing triggers immediately, before the operator
            // has had a chance to review it. The UI/CLI then shows "Disabled — click Enable" so
            // the operator opts in explicitly. If the imported webhook path collides with an
            // already-running workflow, enabled=false is the only safe outcome anyway — the
            // collision check below is redundant when the caller didn't set IsEnabled, but acts
            // as an extra safety net for envelopes that explicitly request `IsEnabled: true`.
            var enabled = item.IsEnabled ?? false;
            if (hmacError is not null)
            {
                // Export intentionally redacts workflow secrets. Preserve import/edit usability,
                // but never honor IsEnabled=true until an operator installs a strong replacement
                // key; Enable and Publish enforce the same policy again.
                enabled = false;
                errors.Add(
                    $"workflows[{i}] ({item.Name}): {hmacError}; imported as DISABLED until the secret is replaced.");
            }
            var newWebhookKeys = ExtractWebhookPaths(definitionJson);
            var collisions = newWebhookKeys.Intersect(takenWebhookKeys).ToList();
            if (enabled && collisions.Count > 0)
            {
                enabled = false;
                errors.Add(
                    $"workflows[{i}] ({item.Name}): webhook path collision on [{string.Join(", ", collisions)}] — imported as DISABLED to protect the existing route. Edit the workflow and re-enable after resolving.");
            }
            foreach (var k in newWebhookKeys) takenWebhookKeys.Add(k);

            var workflow = new Workflow
            {
                Id = Guid.NewGuid(),
                Name = finalName,
                Description = item.Description,
                DefinitionJson = definitionJson,
                Version = 1,
                IsEnabled = enabled,
                FolderId = targetFolderId,
                // Import establishes runtime authority the same way Publish does: the importing
                // user becomes the effective principal. Without this every automated trigger
                // (schedule, webhook, file-watcher, database, event-log) is rejected at dispatch
                // with "missing_effective_principal", and cross-folder sub-workflow calls fail —
                // an imported-and-enabled workflow would otherwise be broken by construction.
                PublishedByUserId = this.GetCurrentUserId(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            PopulateComputedColumns(workflow);
            _db.Workflows.Add(workflow);
            created.Add(new ImportedWorkflowInfo(
                workflow.Id, finalName,
                finalName == item.Name ? null : item.Name));
        }

        if (created.Count > 0)
            await _db.SaveChangesAsync(ct);

        sw.Stop();
        ApiMetrics.ImportExportOperations.Add(1,
            new(TelemetryConstants.Attributes.ImportExportOperation, "import"),
            new("result", "success"));
        ApiMetrics.ImportExportDuration.Record(sw.Elapsed.TotalMilliseconds,
            new(TelemetryConstants.Attributes.ImportExportOperation, "import"),
            new("result", "success"));

        // Audit at the bulk level (not per imported workflow) — the operationally relevant
        // signal is "operator just imported N workflows", and per-workflow rows would flood
        // the table on legitimate bulk migrations. The id collection in details lets a
        // forensic query reconstruct which workflows arrived in this batch.
        if (created.Count > 0 || errors.Count > 0)
        {
            await _audit.LogAsync(AuditActions.WorkflowImported, "Workflow", null,
                AuditDetails.Json(
                    ("created", created.Count.ToString()),
                    ("errors", errors.Count.ToString()),
                    ("folderId", targetFolderId.ToString()),
                    ("workflowIds", string.Join(",", created.Select(w => w.Id.ToString()))),
                    ("durationMs", sw.Elapsed.TotalMilliseconds.ToString("F0"))),
                ct);
        }

        return Ok(new ImportWorkflowsResponse(created.Count, created, errors));
    }

    /// <summary>
    /// Imports workflows from a System Center Orchestrator <c>.ois_export</c> XML file.
    /// Request body: the raw XML payload (<c>Content-Type: application/xml</c> or
    /// <c>text/xml</c>). Best-effort translation — SCOrch semantics don't map 1:1 to
    /// NodePilot, so unmapped activities become <c>log</c> placeholders and the response
    /// <c>warnings</c> list reports every non-exact translation for operator review.
    /// Imported workflows are created DISABLED so a half-translated runbook doesn't
    /// start firing triggers on arrival.
    /// </summary>
    [HttpPost("import-scorch")]
    [Authorize(Roles = "Admin,Operator")]
    [Consumes("application/xml", "text/xml")]
    // H-16: CLAUDE.md documents a 50 MiB ceiling for Scorch XML imports; the prior 600 MiB cap
    // contradicted that and let a single authenticated request tie up the XML parser on an
    // attacker-supplied payload. Aligning here makes the doc match the code.
    [RequestSizeLimit(50 * 1024 * 1024)] // Scorch XML cap per CLAUDE.md docs
    public async Task<ActionResult<ScorchImportResponse>> ImportScorch([FromQuery] Guid? folderId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Folder targeting mirrors the JSON import: the body is raw XML, so the destination
        // can only travel as ?folderId= (missing → Root). RBAC = Edit on the chosen folder.
        var targetFolderId = folderId ?? NodePilot.Core.Models.SharedWorkflowFolder.RootFolderId;
        if (await RequireFolderAccessAsync(targetFolderId, NodePilot.Core.Interfaces.ResourceOp.Edit, ct) is { } folderDenied)
            return folderDenied;
        if (folderId is not null
            && !await _db.SharedWorkflowFolders.AsNoTracking().AnyAsync(f => f.Id == targetFolderId, ct))
        {
            return BadRequest(new { error = "folderId does not exist" });
        }
        // Buffer the body as BYTES and hand the stream to the XmlReader, rather than decoding it
        // to a string first. Two reasons:
        //   - Encoding: a StreamReader pinned to UTF-8 mis-decodes any export written in UTF-16 or
        //     a legacy code page. Feeding raw bytes lets the XmlReader honour the BOM and the
        //     document's own <?xml encoding=...?> declaration, which is the only correct source.
        //   - Memory: the previous path held the UTF-8 bytes, a UTF-16 string of the same document
        //     and the XDocument tree simultaneously. The string copy is now gone.
        // The buffer stays: XmlReader reads synchronously, and Kestrel's AllowSynchronousIO
        // defaults to false, so passing Request.Body straight through would throw at runtime on a
        // real server while a MemoryStream-backed unit test happily passed.
        using var buffered = new MemoryStream(
            Request.ContentLength is > 0 and <= int.MaxValue ? (int)Request.ContentLength.Value : 0);
        await Request.Body.CopyToAsync(buffered, ct);
        if (buffered.Length == 0)
            return BadRequest(new { error = "Request body is empty." });
        buffered.Position = 0;

        var importer = new NodePilot.Engine.Scorch.ScorchImporter();
        var parsed = importer.Parse(buffered);

        if (parsed.Workflows.Count == 0 && parsed.Variables.Count == 0)
            return BadRequest(new
            {
                error = "No workflows or variables could be extracted from this file.",
                details = parsed.Errors,
            });

        const int MaxImportItems = 500;
        var importItemCount = (long)parsed.Workflows.Count + parsed.Variables.Count;
        if (importItemCount > MaxImportItems)
            return BadRequest(new
            {
                error = $"Too many workflows and variables in one import (got {importItemCount}, max {MaxImportItems}).",
            });

        // 1. Create global variables first so workflow-import and any {{globals.X}} references
        //    already resolve when the operator opens the imported workflow. We never overwrite
        //    a pre-existing variable — the operator sees the collision and resolves it manually.
        var existingGlobals = await _globals.GetAllAsync(ct);
        var existingGlobalNames = new HashSet<string>(existingGlobals.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        var importedVariables = new List<ScorchImportedVariableInfo>();
        var variablesToCreate = new List<NodePilot.Engine.Scorch.ScorchVariable>();
        var triggeredBy = User.Identity?.Name;
        // Operators create global variables here just like Admins do. Gating this on Admin was
        // considered and rejected: an Operator may already run arbitrary script under the service
        // identity, so anyone able to import a runbook can put the same value straight into a
        // step. The gate would only split Orchestrator migrations into two manual passes without
        // removing a capability. Collisions are still never overwritten (see above).
        foreach (var v in parsed.Variables)
        {
            if (existingGlobalNames.Contains(v.Name))
            {
                importedVariables.Add(new ScorchImportedVariableInfo(
                    v.Name, null, CreatedNow: false, Skipped: true,
                    SkipReason: "A global variable with this name already exists."));
                continue;
            }
            // Plan all writes before opening the transaction. Adding the name now also makes a
            // duplicate within the same SCOrch file a deterministic collision rather than a
            // database-provider-specific unique-constraint failure halfway through the batch.
            variablesToCreate.Add(v);
            existingGlobalNames.Add(v.Name);
            importedVariables.Add(new ScorchImportedVariableInfo(
                v.Name, null, CreatedNow: true, Skipped: false, SkipReason: null));
        }

        // 2. Create workflows (disabled).
        var existingNames = await _db.Workflows.AsNoTracking()
            .Select(w => w.Name).ToListAsync(ct);
        var takenNames = new HashSet<string>(existingNames, StringComparer.Ordinal);

        var created = new List<ScorchImportedWorkflowInfo>();
        var workflowsToCreate = new List<Workflow>();
        var errors = new List<string>(parsed.Errors);

        foreach (var rb in parsed.Workflows)
        {
            if (ValidateDefinitionJson(rb.DefinitionJson) is not null)
            {
                errors.Add($"Runbook '{rb.Name}': generated definition is invalid or exceeds size/depth limits; skipped");
                continue;
            }

            var finalName = UniqueName(rb.Name, takenNames);
            takenNames.Add(finalName);

            var workflow = new Workflow
            {
                Id = Guid.NewGuid(),
                Name = finalName,
                Description = rb.Description,
                DefinitionJson = rb.DefinitionJson,
                Version = 1,
                IsEnabled = false,
                FolderId = targetFolderId,
                // Same as the workflow-import path: the importing user becomes the effective
                // principal, so automated triggers work once the operator enables the workflow.
                PublishedByUserId = this.GetCurrentUserId(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            PopulateComputedColumns(workflow);
            workflowsToCreate.Add(workflow);
            created.Add(new ScorchImportedWorkflowInfo(
                workflow.Id, finalName,
                finalName == rb.Name ? null : rb.Name,
                rb.ActivityCount, rb.HeuristicCount, rb.FallbackCount));
        }

        var variablesCreated = variablesToCreate.Count;
        if (workflowsToCreate.Count > 0 || variablesToCreate.Count > 0)
        {
            // GlobalVariableStore is scoped with this controller and therefore shares _db.
            // Its per-variable SaveChanges calls and the workflow insert must commit as one
            // unit: a later encryption/database failure must not leave a half-imported batch.
            // ExecuteInTransaction verifies exact row identities after an ambiguous commit
            // acknowledgement, so a retry never collides with an import that already committed.
            var strategy = _db.Database.CreateExecutionStrategy();
            var attempt = new ScorchImportAttempt(
                workflowsToCreate,
                variablesToCreate,
                triggeredBy,
                new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase));
            await strategy.ExecuteInTransactionAsync(
                attempt,
                async (state, token) =>
                {
                    // A retried attempt must not inherit entities whose state changed to
                    // Unchanged before an ambiguous commit. Variable IDs are attempt-local.
                    _db.ChangeTracker.Clear();
                    state.CreatedVariableIds.Clear();
                    _db.Workflows.AddRange(state.Workflows);
                    foreach (var v in state.Variables)
                    {
                        var createdVariable = await _globals.CreateAsync(
                            v.Name, v.Value, v.IsSecret, v.Description,
                            GlobalVariableFolder.RootFolderId, state.TriggeredBy, token);
                        state.CreatedVariableIds[v.Name] = createdVariable.Id;
                    }

                    await _db.SaveChangesAsync(token);
                },
                async (state, token) =>
                {
                    // A commit acknowledgement can be lost after the database committed. Verify
                    // the exact pre-generated workflow and captured variable identities before
                    // allowing the execution strategy to replay the import.
                    _db.ChangeTracker.Clear();
                    if (state.CreatedVariableIds.Count != state.Variables.Count) return false;

                    var workflowIds = state.Workflows.Select(workflow => workflow.Id).ToArray();
                    var variableIds = state.CreatedVariableIds.Values.ToArray();
                    var workflowsCommitted = workflowIds.Length == 0
                        || await _db.Workflows.AsNoTracking()
                            .CountAsync(workflow => workflowIds.Contains(workflow.Id), token)
                            == workflowIds.Length;
                    if (!workflowsCommitted) return false;

                    return variableIds.Length == 0
                           || await _db.GlobalVariables.AsNoTracking()
                               .CountAsync(variable => variableIds.Contains(variable.Id), token)
                               == variableIds.Length;
                },
                IsolationLevel.Serializable,
                ct);
        }

        if (created.Count > 0 || variablesCreated > 0)
        {
            var detailsJson = JsonSerializer.Serialize(new
            {
                created = created.Count,
                variables = variablesCreated,
                variablesSkipped = importedVariables.Count(v => v.Skipped),
                fallbacks = created.Sum(c => c.FallbackCount),
                heuristics = created.Sum(c => c.HeuristicCount),
                folderId = targetFolderId,
            });
            await _audit.LogAsync(AuditActions.WorkflowImportedScorch, "Workflow", null, detailsJson, ct);
        }

        sw.Stop();
        ApiMetrics.ImportExportOperations.Add(1,
            new(TelemetryConstants.Attributes.ImportExportOperation, "import_scorch"),
            new("result", "success"));
        ApiMetrics.ImportExportDuration.Record(sw.Elapsed.TotalMilliseconds,
            new(TelemetryConstants.Attributes.ImportExportOperation, "import_scorch"),
            new("result", "success"));

        return Ok(new ScorchImportResponse(created.Count, created, importedVariables, parsed.Warnings, errors));
    }

    /// <summary>
    /// Scans all currently-enabled workflows in the DB for <c>webhookTrigger</c> nodes and
    /// returns the set of <c>method:path</c> keys they serve. Used by <see cref="Import"/> to
    /// detect route collisions. Disabled workflows are excluded because they don't compete
    /// for an incoming webhook.
    /// </summary>
    private async Task<HashSet<string>> CollectWebhookPathsAsync(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defs = await _db.Workflows.AsNoTracking()
            .Where(w => w.IsEnabled)
            .Select(w => w.DefinitionJson)
            .ToListAsync(ct);
        foreach (var def in defs)
            foreach (var k in ExtractWebhookPaths(def)) set.Add(k);
        return set;
    }

    private static IEnumerable<string> ExtractWebhookPaths(string definitionJson)
    {
        if (!WorkflowDefinitionDocument.TryParse(definitionJson, out var definition) || definition is null)
            yield break;

        foreach (var descriptor in definition.TriggerDescriptors.Where(d => d.ActivityType == "webhookTrigger"))
        {
            var config = descriptor.Config;
            if (config.ValueKind != JsonValueKind.Object) continue;
            var path = config.TryGetProperty("path", out var p) ? p.GetString()?.Trim('/') : null;
            var method = (config.TryGetProperty("method", out var m) ? m.GetString() : "POST")?.ToUpperInvariant() ?? "POST";
            if (!string.IsNullOrEmpty(path)) yield return $"{method}:{path}";
        }
    }

    private static WorkflowExportItem ToExportItem(Workflow w)
    {
        JsonElement definition;
        try
        {
            using var doc = JsonDocument.Parse(w.DefinitionJson);
            // Scrub secrets from the exported copy so a workflow JSON file attached to an
            // email or committed to Git doesn't publish webhook secrets / api keys. The
            // original DefinitionJson in the DB is untouched — owners who need the real
            // secret must rotate & set it again in the target environment.
            definition = RedactSecretsInDefinition(doc.RootElement);
        }
        catch
        {
            // Fallback: wrap corrupt JSON as an error marker so the export doesn't crash.
            using var doc = JsonDocument.Parse("""{"nodes":[],"edges":[],"_importError":"original DefinitionJson was not valid JSON"}""");
            definition = doc.RootElement.Clone();
        }
        return new WorkflowExportItem(w.Name, w.Description, definition, IsEnabled: w.IsEnabled);
    }

    private sealed record ScorchImportAttempt(
        IReadOnlyList<Workflow> Workflows,
        IReadOnlyList<NodePilot.Engine.Scorch.ScorchVariable> Variables,
        string? TriggeredBy,
        Dictionary<string, Guid> CreatedVariableIds);

    private IActionResult ExportEnvelopeResult(WorkflowExportEnvelope envelope, string filename)
    {
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        // Content-Disposition with a strict ASCII filename and RFC 5987 filename* fallback.
        // Filename is already sanitized with Path.GetInvalidFileNameChars on Windows (which
        // includes CR/LF), but we additionally reduce to ASCII [A-Za-z0-9._-] here so the
        // header is safe on any OS and no injection is possible even if callers bypass
        // SanitizeFilename.
        var asciiName = System.Text.RegularExpressions.Regex.Replace(filename, @"[^A-Za-z0-9._-]", "_", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        if (string.IsNullOrEmpty(asciiName)) asciiName = "workflow.json";
        var encoded = Uri.EscapeDataString(filename);
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{asciiName}\"; filename*=UTF-8''{encoded}";
        return Content(json, "application/json");
    }

    private static string UniqueName(string desired, HashSet<string> taken)
    {
        if (!taken.Contains(desired)) return desired;
        for (int n = 2; n < 1000; n++)
        {
            var candidate = $"{desired} (Imported {n})";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{desired} (Imported {Guid.NewGuid():N})";
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var safe = sb.ToString().Trim();
        return string.IsNullOrEmpty(safe) ? "workflow" : safe;
    }
}

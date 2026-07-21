using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NodePilot.Core.Validation;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Api.Dtos;
using NodePilot.Mcp.Mapping;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Workflow authoring + edit-lifecycle tools (lock → edit → publish). Two editing styles:
/// full-definition (publish/update) and granular batch (preview/apply patch). Both protect
/// secrets (the agent only sees redacted definitions, so a naive full write would clobber real
/// secrets with "***") and validate the WHOLE result with the Core structural validator before
/// saving — an invalid intermediate state is never persisted.
/// </summary>
[McpServerToolType]
public sealed class WorkflowEditTools
{
    private static readonly JsonElement EmptyDefinition = JsonDocument.Parse("""{"nodes":[],"edges":[]}""").RootElement;

    private readonly NodePilotApiClient _api;

    public WorkflowEditTools(NodePilotApiClient api) => _api = api;

    // ---- Validation (no save) -----------------------------------------------

    [McpServerTool(Name = "validate_workflow_definition", ReadOnly = true)]
    [Description("Structurally validate a workflow definition (nodes/edges) WITHOUT saving: checks ids, that edge endpoints resolve, and that activityTypes are known. Call this before publish/update. Returns { isValid, error }.")]
    public object ValidateWorkflowDefinition(
        [Description("The workflow definition object: { nodes: [...], edges: [...] }.")] JsonElement definition)
    {
        var result = WorkflowDefinitionStructuralValidator.Validate(definition);
        return new { isValid = result.IsValid, error = result.Error };
    }

    // ---- Edit-lock lifecycle ------------------------------------------------

    [McpServerTool(Name = "lock_workflow", Idempotent = true)]
    [Description("Check out a workflow for editing (SCOrch-style edit lock). Atomically disables it and assigns the lock to you. 409 if someone else holds the lock. Required before update/publish/rollback.")]
    public async Task<object> LockWorkflow(
        [Description("The workflow GUID or exact name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var r = await ApiErrorMapper.Guard(() => _api.LockWorkflowAsync(wf.Id, cancellationToken));
        return new { locked = true, workflowId = r.Id, checkedOutBy = r.CheckedOutByUserName };
    }

    [McpServerTool(Name = "unlock_workflow", Idempotent = true)]
    [Description("Release your edit lock on a workflow. Does NOT re-enable it (use publish_workflow or enable_workflow). 423 if the lock belongs to another user.")]
    public async Task<object> UnlockWorkflow(
        [Description("The workflow GUID or exact name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        await ApiErrorMapper.Guard(() => _api.UnlockWorkflowAsync(wf.Id, cancellationToken));
        return new { unlocked = true, workflowId = wf.Id };
    }

    [McpServerTool(Name = "publish_workflow")]
    [Description("Publish a workflow definition: atomically save + enable + release the lock. Requires you hold the lock. The definition is validated and secrets are restored from the current version (the agent never sees/sets real secret values). name/description default to the current values if omitted.")]
    public async Task<object> PublishWorkflow(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("The full workflow definition: { nodes: [...], edges: [...] }.")] JsonElement definition,
        [Description("Optional new name (defaults to current).")] string? name = null,
        [Description("Optional new description (defaults to current).")] string? description = null,
        CancellationToken cancellationToken = default)
        => await SaveFullAsync(idOrName, definition, name, description, publish: true, cancellationToken);

    [McpServerTool(Name = "update_workflow_definition")]
    [Description("Save a workflow definition as a draft (PUT) WITHOUT enabling/unlocking — requires you hold the lock. Validates + protects secrets like publish. Use publish_workflow when you are ready to go live.")]
    public async Task<object> UpdateWorkflowDefinition(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("The full workflow definition: { nodes: [...], edges: [...] }.")] JsonElement definition,
        [Description("Optional new name (defaults to current).")] string? name = null,
        [Description("Optional new description (defaults to current).")] string? description = null,
        CancellationToken cancellationToken = default)
        => await SaveFullAsync(idOrName, definition, name, description, publish: false, cancellationToken);

    // ---- Granular batch patch -----------------------------------------------

    [McpServerTool(Name = "preview_workflow_patch", ReadOnly = true)]
    [Description("Preview a batch of node/edge edits applied to the CURRENT definition, WITHOUT saving and WITHOUT a lock. Returns the merged (redacted) definition, the structural validation result, and notes (e.g. rejected secrets). operations is an array of { op: upsertNode|deleteNode|upsertEdge|deleteEdge, node?, edge?, id? }.")]
    public async Task<object> PreviewWorkflowPatch(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("Array of operations: { op, node?, edge?, id? }. upsert merges by node/edge id; delete needs id.")] JsonElement operations,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var (mergedElement, notes, validation) = ApplyPatch(wf, operations);
        return new
        {
            workflowId = wf.Id,
            validation = new { isValid = validation.IsValid, error = validation.Error },
            notes,
            definition = DefinitionRedactor.Redact(mergedElement),
        };
    }

    [McpServerTool(Name = "apply_workflow_patch")]
    [Description("Apply a batch of node/edge edits to a workflow atomically (merge-by-id, secrets protected). Requires you hold the lock. Validates the WHOLE result first — nothing is saved if it is invalid. dryRun=true behaves like preview. publish=true saves+enables+unlocks; otherwise it saves a draft.")]
    public async Task<object> ApplyWorkflowPatch(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("Array of operations: { op, node?, edge?, id? }.")] JsonElement operations,
        [Description("When true, do not save — just return the merged definition + validation (like preview).")] bool dryRun = false,
        [Description("When true, publish (save+enable+unlock); otherwise save a draft (requires the lock).")] bool publish = false,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var (mergedElement, notes, validation) = ApplyPatch(wf, operations);

        // Dry-run mirrors preview: return the merged (redacted) definition + validation and
        // notes, even when invalid — so the caller can review/diagnose without anything being saved.
        if (dryRun)
            return new
            {
                workflowId = wf.Id,
                dryRun = true,
                validation = new { isValid = validation.IsValid, error = validation.Error },
                notes,
                definition = DefinitionRedactor.Redact(mergedElement),
            };

        // Real save: never persist an invalid result.
        if (!validation.IsValid)
            throw new McpException($"Patch result is structurally invalid (not saved): {validation.Error}");

        return await PersistAsync(wf, mergedElement.GetRawText(), wf.Name, wf.Description, publish, notes, cancellationToken);
    }

    // ---- Create / manage ----------------------------------------------------

    [McpServerTool(Name = "create_workflow")]
    [Description("Create a new workflow from a definition (validated; any secret values the agent sets are masked — set them manually afterward). The new workflow starts disabled and checked out (locked) to you. Returns its id.")]
    public async Task<object> CreateWorkflow(
        [Description("Workflow name.")] string name,
        [Description("The workflow definition: { nodes: [...], edges: [...] }.")] JsonElement definition,
        [Description("Optional description.")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        RequireFullDefinition(definition);
        // No original → MergeFull against an empty def masks any secrets the agent set on new nodes.
        var merged = WorkflowDefinitionPatcher.MergeFull(EmptyDefinition, definition);
        var mergedElement = JsonSerializer.SerializeToElement(merged.Definition);
        var validation = WorkflowDefinitionStructuralValidator.Validate(mergedElement);
        if (!validation.IsValid)
            throw new McpException($"Definition is structurally invalid (not created): {validation.Error}");

        var created = await ApiErrorMapper.Guard(() =>
            _api.CreateWorkflowAsync(new CreateWorkflowRequest(name, description, mergedElement.GetRawText()), cancellationToken));
        return new { created = true, workflowId = created.Id, name = created.Name, notes = merged.Notes };
    }

    [McpServerTool(Name = "duplicate_workflow")]
    [Description("Duplicate a workflow. The copy starts disabled and locked to you. Returns the new workflow's id.")]
    public async Task<object> DuplicateWorkflow(
        [Description("The workflow GUID or exact name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var copy = await ApiErrorMapper.Guard(() => _api.DuplicateWorkflowAsync(wf.Id, cancellationToken));
        return new { duplicated = true, workflowId = copy.Id, name = copy.Name };
    }

    [McpServerTool(Name = "enable_workflow", Idempotent = true)]
    [Description("Enable a workflow (arms its triggers). Requires you hold the lock if one is set.")]
    public async Task<object> EnableWorkflow(
        [Description("The workflow GUID or exact name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        await ApiErrorMapper.Guard(() => _api.EnableWorkflowAsync(wf.Id, cancellationToken));
        return new { enabled = true, workflowId = wf.Id };
    }

    [McpServerTool(Name = "disable_workflow", Idempotent = true)]
    [Description("Disable a workflow: disarms its triggers (external triggers stop firing within ~5s) and blocks new manual executes. NOT lock-gated, so it works as an incident kill-switch. It does NOT cancel already-running executions — pair with cancel_all_executions for full quarantine.")]
    public async Task<object> DisableWorkflow(
        [Description("The workflow GUID or exact name.")] string idOrName,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        await ApiErrorMapper.Guard(() => _api.DisableWorkflowAsync(wf.Id, cancellationToken));
        return new { disabled = true, workflowId = wf.Id };
    }

    [McpServerTool(Name = "rollback_workflow")]
    [Description("Roll a workflow back to a previous version (requires you hold the lock). Snapshots the current version first. Use list_workflow_versions / get_workflow_version to choose.")]
    public async Task<object> RollbackWorkflow(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("The version number to roll back to.")] int version,
        [Description("Optional reason recorded in the audit trail.")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var r = await ApiErrorMapper.Guard(() => _api.RollbackAsync(wf.Id, version, new RollbackRequest(reason), cancellationToken));
        return new { rolledBack = true, workflowId = r.Id, version = r.Version };
    }

    [McpServerTool(Name = "import_workflow")]
    [Description("Import one or more workflows from a nodepilot-workflow-export/v1 envelope (as produced by export_workflow). Imported workflows are created disabled; name collisions get a suffix.")]
    public async Task<object> ImportWorkflow(
        [Description("The export envelope object (schema nodepilot-workflow-export/v1).")] JsonElement envelope,
        [Description("Target shared folder id; omit for Root. Requires Edit on that folder.")] Guid? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var env = envelope.Deserialize<WorkflowExportEnvelope>(NodePilotApiClient.JsonOptions)
            ?? throw new McpException("envelope is not a valid workflow export.");
        var result = await ApiErrorMapper.Guard(() => _api.ImportAsync(env, folderId, cancellationToken));
        return new { created = result.Created, workflows = result.Workflows, errors = result.Errors };
    }

    [McpServerTool(Name = "import_scorch_workflow")]
    [Description("Import a System Center Orchestrator runbook (XML) as a best-effort-translated NodePilot workflow. Returns created workflows, variables and any warnings.")]
    public async Task<object> ImportScorchWorkflow(
        [Description("The raw Orchestrator export XML.")] string xml,
        [Description("Target shared folder id; omit for Root. Requires Edit on that folder.")] Guid? folderId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ApiErrorMapper.Guard(() => _api.ImportScorchAsync(xml, folderId, cancellationToken));
        return result;
    }

    // ---- Step test ----------------------------------------------------------

    [McpServerTool(Name = "list_step_test_runs", ReadOnly = true)]
    [Description("List recent executions usable as context for testing a single step, flagged with whether that step actually ran.")]
    public async Task<object> ListStepTestRuns(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("The step (node) id.")] string stepId,
        [Description("Max runs to return.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        var runs = await ApiErrorMapper.Guard(() => _api.ListStepTestRunsAsync(wf.Id, stepId, limit, cancellationToken));
        return new { workflowId = wf.Id, stepId, runs };
    }

    [McpServerTool(Name = "get_step_test_context", ReadOnly = true)]
    [Description("Get the upstream variables available to a step (from a chosen past run or the latest), to populate mock inputs for test_step.")]
    public async Task<object> GetStepTestContext(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("The step (node) id.")] string stepId,
        [Description("Optional execution GUID to pull the context from (defaults to the latest).")] string? executionId = null,
        CancellationToken cancellationToken = default)
    {
        var wf = await ResolveAsync(idOrName, cancellationToken);
        Guid? exec = null;
        if (!string.IsNullOrWhiteSpace(executionId))
        {
            if (!Guid.TryParse(executionId, out var g)) throw new McpException("executionId must be a GUID.");
            exec = g;
        }
        var ctx = await ApiErrorMapper.Guard(() => _api.GetStepTestContextAsync(wf.Id, stepId, exec, cancellationToken));
        return ctx;
    }

    // ---- Internals ----------------------------------------------------------

    private async Task<object> SaveFullAsync(
        string idOrName, JsonElement definition, string? name, string? description, bool publish, CancellationToken ct)
    {
        RequireFullDefinition(definition);
        var wf = await ResolveAsync(idOrName, ct);
        var current = ParseDef(wf.DefinitionJson);
        var merged = WorkflowDefinitionPatcher.MergeFull(current, definition);   // restore secrets from current
        var mergedElement = JsonSerializer.SerializeToElement(merged.Definition);
        var validation = WorkflowDefinitionStructuralValidator.Validate(mergedElement);
        if (!validation.IsValid)
            throw new McpException($"Definition is structurally invalid (not saved): {validation.Error}");

        return await PersistAsync(wf, mergedElement.GetRawText(), name ?? wf.Name, description ?? wf.Description, publish, merged.Notes, ct);
    }

    private (JsonElement Merged, IReadOnlyList<string> Notes, WorkflowDefinitionValidationResult Validation) ApplyPatch(
        WorkflowResponse wf, JsonElement operations)
    {
        List<WorkflowDefinitionPatcher.PatchOp> ops;
        try { ops = WorkflowDefinitionPatcher.ParseOps(operations); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }

        var current = ParseDef(wf.DefinitionJson);
        WorkflowDefinitionPatcher.PatchResult result;
        try { result = WorkflowDefinitionPatcher.Apply(current, ops); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }

        var mergedElement = JsonSerializer.SerializeToElement(result.Definition);
        var validation = WorkflowDefinitionStructuralValidator.Validate(mergedElement);
        return (mergedElement, result.Notes, validation);
    }

    private async Task<object> PersistAsync(
        WorkflowResponse wf, string definitionJson, string name, string? description,
        bool publish, IReadOnlyList<string> notes, CancellationToken ct)
    {
        if (publish)
        {
            var r = await ApiErrorMapper.Guard(() => _api.PublishWorkflowAsync(wf.Id, new PublishWorkflowRequest(name, description, definitionJson), ct));
            return new { published = true, workflowId = r.Id, version = r.Version, enabled = r.IsEnabled, notes };
        }
        await ApiErrorMapper.Guard(() => _api.UpdateWorkflowAsync(wf.Id, new UpdateWorkflowRequest(name, description, definitionJson), ct));
        return new { saved = true, mode = "draft", workflowId = wf.Id, notes };
    }

    private static JsonElement ParseDef(string? json)
        => JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone();

    /// <summary>
    /// Guard for the full-definition tools (create/update/publish): the caller must pass an object
    /// with BOTH 'nodes' and 'edges' arrays. Without this, a malformed shape like <c>{}</c> would
    /// merge to <c>{nodes:[],edges:[]}</c> — which the structural validator accepts — and silently
    /// overwrite the workflow with an empty graph. Empty arrays are allowed (intentional empty
    /// workflow); MISSING arrays are rejected.
    /// </summary>
    private static void RequireFullDefinition(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object
            || !definition.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array
            || !definition.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
        {
            throw new McpException(
                "definition must be an object with 'nodes' and 'edges' arrays (both may be empty). "
                + "Refusing to save — a malformed shape would overwrite the workflow with an empty graph. "
                + "For partial edits use apply_workflow_patch instead.");
        }
    }

    private async Task<WorkflowResponse> ResolveAsync(string idOrName, CancellationToken ct)
        => Guid.TryParse(idOrName, out var id)
            ? await ApiErrorMapper.Guard(() => _api.GetWorkflowAsync(id, ct))
            : await ApiErrorMapper.Guard(() => _api.GetWorkflowByNameAsync(idOrName, ct));
}

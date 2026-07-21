using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using NodePilot.Core.Activities;
using NodePilot.Mcp.Analysis;
using NodePilot.Mcp.Api;
using NodePilot.Mcp.Mapping;
using NodePilot.Mcp.Resources;

namespace NodePilot.Mcp.Tools;

/// <summary>
/// Tools for the in-canvas chat assistant. They answer the three questions an author asks in the
/// designer — "is my graph correct?", "what can I reference here?", "why did it break?" — plus
/// review/build helpers. Most run in-process against NodePilot.Core on a supplied definition, so
/// they also work on the UNSAVED canvas state.
/// </summary>
[McpServerToolType]
public sealed class CanvasAssistantTools
{
    private static readonly Regex TemplateRx = new(@"\{\{\s*(.*?)\s*\}\}", RegexOptions.Compiled);
    private static readonly HashSet<string> BinaryOps = new(StringComparer.Ordinal)
        { "==", "!=", "<", ">", "<=", ">=", "contains", "startsWith", "endsWith", "matches" };
    private static readonly HashSet<string> UnaryOps = new(StringComparer.Ordinal)
        { "isEmpty", "isNotEmpty", "isTrue", "isFalse" };

    private readonly NodePilotApiClient _api;

    public CanvasAssistantTools(NodePilotApiClient api) => _api = api;

    // ---- Static analysis ----------------------------------------------------

    [McpServerTool(Name = "analyze_workflow", ReadOnly = true)]
    [Description("Statically analyse a workflow definition (works on the unsaved canvas state): finds nodes that never run (no active path from a trigger), missing/disabled trigger (0 roots → the run Fails), cycles, duplicate edges/output variables, Start-Job-in-runspace scripts, remote activities without a targetMachineId, and unknown activity types. No network call.")]
    public object AnalyzeWorkflow(
        [Description("The workflow definition: { nodes: [...], edges: [...] }.")] JsonElement definition)
        => Wrap(() => WorkflowAnalyzer.Analyze(definition));

    [McpServerTool(Name = "find_unresolved_references", ReadOnly = true)]
    [Description("Scan a definition's config strings for {{…}} references that won't resolve under the contract guarantee (only output/error/success/param.X tails plus globals.*/manual.* resolve). Returns stable codes such as unknown-template-ref and invalid-template-tail. No network call.")]
    public object FindUnresolvedReferences(
        [Description("The workflow definition: { nodes: [...], edges: [...] }.")] JsonElement definition)
        => Wrap(() => new { unresolved = VariableResolver.FindUnresolved(definition) });

    [McpServerTool(Name = "get_available_variables", ReadOnly = true)]
    [Description("List the {{…}} references available AT a given node: upstream step outputs (output/error/success + static + runScript/$var + wmiQuery captureProperties params), run-level manual.* (from manualTrigger parameters) and globals.*. Mostly in-process; globals come from the API.")]
    public async Task<object> GetAvailableVariables(
        [Description("The workflow definition: { nodes: [...], edges: [...] }.")] JsonElement definition,
        [Description("The node id to compute available variables for.")] string nodeId,
        CancellationToken cancellationToken = default)
    {
        VariableResolver.AvailableVariables vars;
        try { vars = VariableResolver.Available(definition, nodeId); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }

        // Globals are run-level; fetch best-effort so the rest still works unauthenticated.
        List<string> globals = [];
        string? globalsNote = null;
        try
        {
            var gs = await _api.ListGlobalsAsync(cancellationToken);
            globals = gs.Select(g => $"{{{{globals.{g.Name}}}}}").ToList();
        }
        catch (Exception ex)
        {
            globalsNote = $"Could not load globals: {ex.Message}";
        }

        return new { nodeId, upstream = vars.Upstream, runLevel = vars.RunLevel, globals, globalsNote };
    }

    [McpServerTool(Name = "check_styleguide", ReadOnly = true)]
    [Description("Lightweight layout/clarity checks against the workflow styleguide: nodes have labels, at least one trigger, no overlapping node positions, branch edges are labelled. See the nodepilot://styleguide resource for the full rules. No network call.")]
    public object CheckStyleguide(
        [Description("The workflow definition: { nodes: [...], edges: [...] }.")] JsonElement definition)
        => Wrap(() => StyleguideCheck(definition));

    // ---- Build helpers ------------------------------------------------------

    [McpServerTool(Name = "validate_edge_condition", ReadOnly = true)]
    [Description("Validate an edge conditionExpression tree (comparison / group(AND|OR) / not). Returns { isValid, errors } so you can author branch conditions correctly. No network call.")]
    public object ValidateEdgeCondition(
        [Description("The conditionExpression object.")] JsonElement conditionExpression)
    {
        var errors = new List<string>();
        ValidateCondition(conditionExpression, "$", errors);
        return new { isValid = errors.Count == 0, errors };
    }

    [McpServerTool(Name = "validate_activity_config", ReadOnly = true)]
    [Description("Check a single node's config against an activity type: confirms the type exists, flags missing required keys and unknown keys (using the curated config reference). No network call.")]
    public object ValidateActivityConfig(
        [Description("The activity type, e.g. 'runScript'.")] string activityType,
        [Description("The node's config object.")] JsonElement config)
    {
        var knownType = ActivityCatalog.ByType.ContainsKey(activityType);
        if (!knownType)
            return new { knownType = false, error = $"Unknown activity type '{activityType}'." };

        var present = config.ValueKind == JsonValueKind.Object
            ? config.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var reference = ConfigReferenceKeys(activityType);
        if (reference is null)
            return new { knownType = true, hasConfigReference = false, note = "No curated config reference for this type yet; only the type was validated.", presentKeys = present };

        var required = reference.Where(k => k.Required).Select(k => k.Key).ToList();
        var allowed = reference.Select(k => k.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingRequired = required.Where(k => !present.Contains(k)).ToList();
        var unknownKeys = present.Where(k => !allowed.Contains(k)).ToList();

        return new
        {
            knownType = true,
            hasConfigReference = true,
            isValid = missingRequired.Count == 0,
            missingRequired,
            unknownKeys,
        };
    }

    [McpServerTool(Name = "preview_template_resolution", ReadOnly = true)]
    [Description("Resolve the {{…}} references in an expression. Provide workflowId+stepId (optionally executionId) to load real upstream values from the step-test context of a past run, and/or mockVariables to supply/override values (keys like 'step.output', 'step.param.x', 'globals.NAME'). Returns the resolved string and any references with no value.")]
    public async Task<object> PreviewTemplateResolution(
        [Description("The expression containing {{…}} references.")] string expression,
        [Description("Flat map of reference (without braces) → value, e.g. { 'checkDisk.param.freeGb': '7' }. Overrides run-context values.")] Dictionary<string, string>? mockVariables = null,
        [Description("Optional workflow GUID or exact name — with stepId, loads real upstream values from a past run.")] string? workflowId = null,
        [Description("Optional step (node) id — required together with workflowId to load run context.")] string? stepId = null,
        [Description("Optional execution GUID to pull the context from (defaults to the latest run).")] string? executionId = null,
        CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? contextNote = null;

        if (!string.IsNullOrWhiteSpace(workflowId) && !string.IsNullOrWhiteSpace(stepId))
        {
            var wf = Guid.TryParse(workflowId, out var id)
                ? id
                : (await ApiErrorMapper.Guard(() => _api.GetWorkflowByNameAsync(workflowId!, cancellationToken))).Id;
            Guid? exec = null;
            if (!string.IsNullOrWhiteSpace(executionId))
            {
                if (!Guid.TryParse(executionId, out var g)) throw new McpException("executionId must be a GUID.");
                exec = g;
            }
            var ctx = await ApiErrorMapper.Guard(() => _api.GetStepTestContextAsync(wf, stepId!, exec, cancellationToken));
            foreach (var v in ctx.Variables)
                if (v.Value is not null) map[v.Key] = v.Value; // Key is the {{…}} inner form, e.g. "check.param.x"
            contextNote = $"Loaded {map.Count} values from {(exec is null ? "the latest run" : $"run {exec}")}.";
        }

        // mockVariables override the run context.
        if (mockVariables is not null)
            foreach (var kv in mockVariables) map[kv.Key] = kv.Value;

        var missing = new List<string>();
        var resolved = TemplateRx.Replace(expression, m =>
        {
            var key = m.Groups[1].Value.Trim();
            if (map.TryGetValue(key, out var v)) return v;
            missing.Add(m.Value);
            return m.Value;
        });
        return new { input = expression, resolved, unresolved = missing.Distinct(), contextNote };
    }

    [McpServerTool(Name = "suggest_layout", ReadOnly = true)]
    [Description("Re-flow node positions into clean left-to-right layers (triggers first), useful after adding nodes. Returns a copy of the definition (secrets redacted) with updated node.position. Does not save. No network call.")]
    public object SuggestLayout(
        [Description("The workflow definition: { nodes: [...], edges: [...] }.")] JsonElement definition)
        => Wrap(() =>
        {
            var reflowed = LayoutEngine.Reflow(definition);
            // Returns the full definition → redact inline secrets before handing it back.
            return new { definition = DefinitionRedactor.Redact(JsonSerializer.SerializeToElement(reflowed)) };
        });

    // ---- Review / debug (API-backed) ----------------------------------------

    [McpServerTool(Name = "diff_workflow_definition", ReadOnly = true)]
    [Description("Diff two workflow definitions by node/edge id: which nodes/edges were added, removed or modified. Use it to review a proposed change (e.g. an apply_workflow_patch result) before applying. No network call.")]
    public object DiffWorkflowDefinition(
        [Description("The current definition.")] JsonElement current,
        [Description("The proposed definition.")] JsonElement proposed)
        => Wrap(() => DefinitionDiff.Diff(current, proposed));

    [McpServerTool(Name = "get_workflow_node", ReadOnly = true)]
    [Description("Get one node's full config from a saved workflow without fetching the whole (possibly huge) definition. Secrets are masked. Use it for the node currently selected in the canvas.")]
    public async Task<object> GetWorkflowNode(
        [Description("The workflow GUID or exact name.")] string idOrName,
        [Description("The node id.")] string nodeId,
        CancellationToken cancellationToken = default)
    {
        var wf = Guid.TryParse(idOrName, out var id)
            ? await ApiErrorMapper.Guard(() => _api.GetWorkflowAsync(id, cancellationToken))
            : await ApiErrorMapper.Guard(() => _api.GetWorkflowByNameAsync(idOrName, cancellationToken));

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(wf.DefinitionJson) ? "{}" : wf.DefinitionJson);
        if (!doc.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            throw new McpException("Workflow has no nodes.");

        foreach (var node in nodes.EnumerateArray())
            if (node.TryGetProperty("id", out var nid) && nid.ValueKind == JsonValueKind.String && nid.GetString() == nodeId)
                return new { workflowId = wf.Id, node = DefinitionRedactor.Redact(node) };

        throw new McpException($"No node '{nodeId}' in workflow {wf.Id}.");
    }

    [McpServerTool(Name = "get_failure_context", ReadOnly = true)]
    [Description("One-call debugging: find the most recent FAILED run of a workflow and return its failing steps with (truncated) error output and the variable snapshot at failure. Answers 'why did it break?'.")]
    public async Task<object> GetFailureContext(
        [Description("The workflow GUID.")] string workflowId,
        CancellationToken cancellationToken = default)
    {
        var wf = ExecutionTools.ParseGuid(workflowId, "workflowId");
        var executions = await ApiErrorMapper.Guard(() => _api.ListExecutionsAsync(wf, activeOnly: false, terminalOnly: true, cancellationToken));
        var failed = executions.FirstOrDefault(e => string.Equals(e.Status, "Failed", StringComparison.OrdinalIgnoreCase));
        if (failed is null)
            return new { workflowId = wf, message = "No failed executions found for this workflow." };

        var steps = await ApiErrorMapper.Guard(() => _api.GetStepsAsync(failed.Id, cancellationToken));
        var failing = steps
            .Where(s => string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            .Select(s => new
            {
                stepId = s.StepId,
                stepName = s.StepName,
                stepType = s.StepType,
                targetMachine = s.TargetMachine,
                error = PayloadShaping.Truncate(s.ErrorOutput),
                output = PayloadShaping.Truncate(s.Output),
                variablesSnapshot = PayloadShaping.Truncate(s.VariablesSnapshot),
            })
            .ToList();

        return new
        {
            workflowId = wf,
            execution = new { failed.Id, failed.Status, failed.StartedAt, failed.CompletedAt, errorMessage = PayloadShaping.Truncate(failed.ErrorMessage) },
            failingSteps = failing,
        };
    }

    // ---- Internals ----------------------------------------------------------

    private static object Wrap(Func<object> compute)
    {
        try { return compute(); }
        catch (JsonException ex) { throw new McpException($"definition is not valid JSON: {ex.Message}"); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    private static void ValidateCondition(JsonElement node, string path, List<string> errors)
    {
        if (node.ValueKind != JsonValueKind.Object) { errors.Add($"{path}: condition must be an object."); return; }
        if (!node.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{path}: missing string 'type' (comparison|group|not).");
            return;
        }

        switch (typeEl.GetString())
        {
            case "comparison":
                if (!node.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String || !(BinaryOps.Contains(op.GetString()!) || UnaryOps.Contains(op.GetString()!)))
                    errors.Add($"{path}.op: must be one of {string.Join(", ", BinaryOps.Concat(UnaryOps))}.");
                break;
            case "group":
                if (!node.TryGetProperty("op", out var gop) || gop.ValueKind != JsonValueKind.String || (gop.GetString() != "AND" && gop.GetString() != "OR"))
                    errors.Add($"{path}.op: group needs op AND or OR.");
                if (!node.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array || children.GetArrayLength() == 0)
                    errors.Add($"{path}.children: group needs a non-empty array.");
                else
                {
                    var i = 0;
                    foreach (var child in children.EnumerateArray()) ValidateCondition(child, $"{path}.children[{i++}]", errors);
                }
                break;
            case "not":
                if (!node.TryGetProperty("child", out var notChild)) errors.Add($"{path}.child: not needs a child.");
                else ValidateCondition(notChild, $"{path}.child", errors);
                break;
            default:
                errors.Add($"{path}.type: '{typeEl.GetString()}' is not comparison|group|not.");
                break;
        }
    }

    private sealed record ConfigKey(string Key, bool Required);

    private static List<ConfigKey>? ConfigReferenceKeys(string activityType)
    {
        var json = EmbeddedResources.Read(EmbeddedResources.ActivityConfigReference);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.GetProperty("activities").TryGetProperty(activityType, out var entry)
            || !entry.TryGetProperty("configKeys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            return null;

        return keys.EnumerateArray()
            .Where(k => k.ValueKind == JsonValueKind.Object && k.TryGetProperty("key", out _))
            .Select(k => new ConfigKey(
                k.GetProperty("key").GetString()!,
                k.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True))
            .ToList();
    }

    private static object StyleguideCheck(JsonElement definition)
    {
        var doc = NodePilot.Core.WorkflowDefinitions.WorkflowDefinitionDocument.FromJsonElement(definition);
        var issues = new List<string>();

        if (doc.RootNodes.Count == 0) issues.Add("No (enabled) trigger node — start a workflow with a trigger.");

        foreach (var n in doc.Nodes)
            if (string.IsNullOrWhiteSpace(n.Data.Label))
                issues.Add($"Node '{n.Id}' has no label — give every node a descriptive label.");

        // Overlapping positions (read raw, since the model drops position).
        if (definition.TryGetProperty("nodes", out var rawNodes) && rawNodes.ValueKind == JsonValueKind.Array)
        {
            var seen = new HashSet<(double, double)>();
            foreach (var n in rawNodes.EnumerateArray())
                if (n.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object
                    && pos.TryGetProperty("x", out var x) && pos.TryGetProperty("y", out var y)
                    && x.ValueKind == JsonValueKind.Number && y.ValueKind == JsonValueKind.Number
                    && !seen.Add((x.GetDouble(), y.GetDouble())))
                    issues.Add("Two or more nodes share the same position — run suggest_layout to de-overlap.");
        }

        return new { ok = issues.Count == 0, issues = issues.Distinct(), reference = "nodepilot://styleguide" };
    }
}

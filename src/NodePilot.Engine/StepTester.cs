using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;
using NodePilot.Engine.Execution;
using NodePilot.Engine.Security;

namespace NodePilot.Engine;

public interface IStepTester
{
    Task<StepTestResult> TestStepAsync(
        Guid workflowId,
        string stepId,
        StepTestAuthorizationSnapshot authorizationSnapshot,
        Dictionary<string, string>? mockVariables,
        JsonElement? configOverride,
        CancellationToken ct);
}

/// <summary>
/// Workflow state observed by the API authorization gate. StepTester revalidates this snapshot
/// before any activity side effects so a concurrent folder move, definition update, or lock
/// replacement cannot change the resource after the caller was authorized.
/// </summary>
public sealed record StepTestAuthorizationSnapshot(
    Guid FolderId,
    int Version,
    Guid? CheckedOutByUserId,
    DateTime? CheckedOutAt)
{
    public static StepTestAuthorizationSnapshot Capture(Workflow workflow) => new(
        workflow.FolderId,
        workflow.Version,
        workflow.CheckedOutByUserId,
        workflow.CheckedOutAt);
}

public record StepTestResult(
    bool Success,
    string? Output,
    string? ErrorOutput,
    Dictionary<string, string> OutputParameters,
    double DurationMs,
    string? ErrorMessage);

public class StepTester : IStepTester
{
    private readonly NodePilotDbContext _db;
    private readonly ActivityRegistry _registry;
    private readonly IGlobalVariableStore _globals;
    private readonly IServiceProvider _serviceProvider;
    private readonly OutputRedactor _redactor;

    public StepTester(NodePilotDbContext db, ActivityRegistry registry, IGlobalVariableStore globals, IServiceProvider serviceProvider, OutputRedactor redactor)
    {
        _db = db;
        _registry = registry;
        _globals = globals;
        _serviceProvider = serviceProvider;
        _redactor = redactor;
    }

    public async Task<StepTestResult> TestStepAsync(
        Guid workflowId, string stepId, StepTestAuthorizationSnapshot authorizationSnapshot,
        Dictionary<string, string>? mockVariables,
        JsonElement? configOverride, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (configOverride.HasValue
            && (authorizationSnapshot.CheckedOutByUserId is null
                || authorizationSnapshot.CheckedOutAt is null))
        {
            return Fail("Config override requires an active edit-lock authorization snapshot", sw);
        }

        var authorizedWorkflow = _db.Workflows.AsNoTracking()
            .Where(w => w.Id == workflowId
                        && w.FolderId == authorizationSnapshot.FolderId
                        && w.Version == authorizationSnapshot.Version);
        if (configOverride.HasValue)
        {
            authorizedWorkflow = authorizedWorkflow.Where(w =>
                w.CheckedOutByUserId == authorizationSnapshot.CheckedOutByUserId
                && w.CheckedOutAt == authorizationSnapshot.CheckedOutAt);
        }

        var workflow = await authorizedWorkflow.FirstOrDefaultAsync(ct);
        if (workflow is null)
            return Fail("Workflow changed after authorization — reload and retry the step test", sw);

        if (!WorkflowDefinitionDocument.TryParse(workflow.DefinitionJson, out var definition) || definition is null)
            return Fail("Invalid workflow definition JSON", sw);

        var node = definition.FindNode(stepId);
        if (node is null)
            return Fail($"Step '{stepId}' not found in workflow", sw);

        if (node.Data.Disabled)
            return Fail("Step is disabled — enable it before testing", sw);

        // Sub-workflow activities' runtime RBAC gate only fires when there is a persisted
        // parent WorkflowExecution row. Step-test synthesises an execution id that is never
        // persisted, so both startWorkflow and forEach would otherwise skip the resolver and
        // could invoke a child workflow outside the caller's folder scope. Until step-test
        // carries an authenticated persisted call context, refuse both activities.
        if (string.Equals(node.Type, "startWorkflow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.Type, "forEach", StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"{node.Type} cannot be step-tested — run the full workflow to validate sub-workflow calls.", sw);
        }

        // Unsaved-config override: when the UI sends the live editor state, swap it onto the
        // node we just parsed so the test reflects what the user is editing right now (not the
        // last-saved DB config). Other fields (targetMachine, credential, outputVariable) keep
        // coming from the persisted definition — those rarely change between test clicks and
        // forcing the user to re-include them in every override would be ergonomic noise.
        if (configOverride is { ValueKind: JsonValueKind.Object } overrideElement)
        {
            node = new WorkflowNode
            {
                Id = node.Id,
                Type = node.Type,
                Data = new WorkflowNodeData
                {
                    Label = node.Data.Label,
                    OutputVariable = node.Data.OutputVariable,
                    TargetMachineRaw = node.Data.TargetMachineRaw,
                    CredentialRaw = node.Data.CredentialRaw,
                    Config = overrideElement.Clone(),
                    Disabled = node.Data.Disabled,
                    Breakpoint = node.Data.Breakpoint,
                    BreakpointCondition = node.Data.BreakpointCondition,
                }
            };
        }

        // Resolve machine + credential from node config (raw GUID or literal hostname)
        ManagedMachine? machine = null;
        if (Guid.TryParse(node.Data.TargetMachineRaw, out var machineId))
            machine = await _db.ManagedMachines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == machineId, ct);
        else if (!string.IsNullOrWhiteSpace(node.Data.TargetMachineRaw))
            machine = new ManagedMachine
            {
                Id = Guid.Empty,
                Name = node.Data.TargetMachineRaw,
                Hostname = node.Data.TargetMachineRaw,
                WinRmPort = 5985,
                UseSsl = false,
            };

        Guid? credentialId = Guid.TryParse(node.Data.CredentialRaw, out var credId) ? credId : null;

        // Build variables: globals first, mock values override
        var globalVars = await _globals.GetAllResolvedAsync(ct);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in globalVars)
            variables[$"globals.{k}"] = v;
        if (mockVariables is not null)
            foreach (var (k, v) in mockVariables)
                variables[k] = v;

        // Convert flat mock dict to ActivityResult dict for JSON config resolution
        var fakeResults = BuildFakeResults(mockVariables ?? []);

        IActivityExecutor executor;
        try { executor = _registry.GetExecutor(node.Type, _serviceProvider); }
        catch (Exception ex) { return Fail($"No executor for activity type '{node.Type}': {ex.Message}", sw); }

        // Resolve config through the same policy as production StepRunner. In particular,
        // sql/databaseTrigger query text must not be template-expanded in step-test mode.
        var configForExecution = StepRunner.ResolveConfigForExecution(
            node.Type, node.Data.Config, fakeResults, definition.OutputVariableToStepId, globalVars);

        var context = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = stepId,
            // Provide a meaningful workflow/step context in the step-test path too, so
            // that user log activities write the same support-log line during a test run
            // as they would in a real run.
            StepLabel = node.Data.Label ?? stepId,
            WorkflowName = workflow.Name,
            TargetMachineId = machine?.Id == Guid.Empty ? null : machine?.Id,
            CredentialId = credentialId,
            Variables = variables,
            ResolvedMachine = machine,
            // Thread the same condition-evaluation context the production StepRunner provides,
            // so a decision step under test resolves upstream/global/manual operands identically
            // instead of silently falling back to empty values.
            PreviousResults = fakeResults,
            OutputVariableToStepId = definition.OutputVariableToStepId,
            GlobalVariables = globalVars,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            var result = await executor.ExecuteAsync(context, configForExecution, cts.Token);
            // Mirror StepRunner.cs: every result that leaves the engine — persist, SignalR,
            // log, OR step-test response — must pass through OutputRedactor first. Otherwise a
            // step-test caller can interpolate a secret global / credential into a runScript
            // body and read the plaintext back, bypassing the always-on redaction contract.
            var sanitized = _redactor.Redact(result);
            return new StepTestResult(
                sanitized.Success, sanitized.Output, sanitized.ErrorOutput,
                sanitized.OutputParameters, sw.Elapsed.TotalMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            return new StepTestResult(false, null, "Step test timed out after 60 seconds.",
                [], sw.Elapsed.TotalMilliseconds, "Timeout");
        }
        catch (Exception ex)
        {
            var redacted = _redactor.Redact(ex.Message);
            return new StepTestResult(false, null, redacted, [], sw.Elapsed.TotalMilliseconds, redacted);
        }
    }

    private static StepTestResult Fail(string msg, System.Diagnostics.Stopwatch sw)
        => new(false, null, msg, [], sw.Elapsed.TotalMilliseconds, msg);

    private static Dictionary<string, ActivityResult> BuildFakeResults(Dictionary<string, string> mockVars)
    {
        var results = new Dictionary<string, ActivityResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in mockVars)
        {
            var parts = key.Split('.', 3);
            if (parts.Length < 2) continue;
            var name = parts[0];
            if (!results.TryGetValue(name, out var ar))
                results[name] = ar = new ActivityResult { Success = true };

            if (parts[1] == "output") ar.Output = value;
            else if (parts[1] == "error") ar.ErrorOutput = value;
            else if (parts[1] == "param" && parts.Length == 3) ar.OutputParameters[parts[2]] = value;
        }
        return results;
    }
}

/// <summary>
/// Variable + sample value the mock-editor pre-fills with. Mirrors the API DTO
/// <c>StepTestContextVariable</c> but lives in the engine so the API can stay free
/// of engine internals like <see cref="WorkflowNode"/> traversal.
/// </summary>
public record StepTestContextEntry(string Key, string Origin, string Source, string? Value);

/// <summary>
/// Returned from <see cref="IStepTestContextProvider.GetContextAsync"/> — describes the
/// concrete execution that was used to build the variable dump (so the UI can show "from
/// run started 14:02 → succeeded") plus the variables themselves. <c>ExecutionId</c> is
/// null when no historical run was found and we fell back to a schema-only suggestion list
/// derived from the static graph.
/// </summary>
public record StepTestContext(
    Guid? ExecutionId,
    DateTime? ExecutedAt,
    string? Status,
    IReadOnlyList<StepTestContextEntry> Variables);

public interface IStepTestContextProvider
{
    /// <summary>
    /// Builds the upstream variable suggestions for the step-test "with last run context"
    /// mode. When <paramref name="executionId"/> is null, the latest non-debug execution of
    /// the workflow is used. Returns a context with empty variables when no run exists yet.
    /// </summary>
    Task<StepTestContext> GetContextAsync(Guid workflowId, string stepId, Guid? executionId, CancellationToken ct);

    /// <summary>List recent executions for the workflow's "Pick a run" dropdown.</summary>
    Task<IReadOnlyList<StepTestContextRunInfoEntry>> ListRunsAsync(Guid workflowId, string stepId, int limit, CancellationToken ct);
}

public record StepTestContextRunInfoEntry(
    Guid ExecutionId,
    DateTime StartedAt,
    string Status,
    string? TriggeredBy,
    bool StepRan);

public sealed class StepTestContextProvider : IStepTestContextProvider
{
    private readonly NodePilotDbContext _db;
    private readonly IGlobalVariableStore _globals;

    public StepTestContextProvider(NodePilotDbContext db, IGlobalVariableStore globals)
    {
        _db = db;
        _globals = globals;
    }

    public async Task<IReadOnlyList<StepTestContextRunInfoEntry>> ListRunsAsync(
        Guid workflowId, string stepId, int limit, CancellationToken ct)
    {
        if (limit <= 0) limit = 10;
        if (limit > 50) limit = 50;

        // Take a few extra so we can backfill after filtering — debug runs often outnumber
        // useful ones and we want at least `limit` real candidates. The 4× headroom is
        // arbitrary but keeps a single round-trip to the DB cheap.
        var raw = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.WorkflowId == workflowId && e.CompletedAt != null)
            .OrderByDescending(e => e.StartedAt)
            .Take(limit * 4)
            .Select(e => new { e.Id, e.StartedAt, e.Status, e.TriggeredBy })
            .ToListAsync(ct);

        if (raw.Count == 0) return Array.Empty<StepTestContextRunInfoEntry>();

        var ids = raw.Select(r => r.Id).ToList();
        var stepRanLookup = await _db.StepExecutions.AsNoTracking()
            .Where(s => ids.Contains(s.WorkflowExecutionId) && s.StepId == stepId)
            .Select(s => s.WorkflowExecutionId)
            .ToListAsync(ct);
        var stepRanSet = new HashSet<Guid>(stepRanLookup);

        return raw
            .Select(r => new StepTestContextRunInfoEntry(
                r.Id, r.StartedAt, r.Status.ToString(), r.TriggeredBy, stepRanSet.Contains(r.Id)))
            .Take(limit)
            .ToList();
    }

    public async Task<StepTestContext> GetContextAsync(Guid workflowId, string stepId, Guid? executionId, CancellationToken ct)
    {
        var workflow = await _db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId, ct);
        if (workflow is null)
            return new StepTestContext(null, null, null, Array.Empty<StepTestContextEntry>());

        if (!WorkflowDefinitionDocument.TryParse(workflow.DefinitionJson, out var definition) || definition is null)
            return new StepTestContext(null, null, null, Array.Empty<StepTestContextEntry>());

        // Disabled edges still propagate context here: the user might be testing exactly
        // that "what if I re-enable this edge" scenario, so include their sources too.
        var ancestors = definition.FindAncestorNodeIds(stepId, includeDisabledEdges: true);

        // Pick the source execution. When the caller passes a specific id, honour it (lets
        // them pin to a known-good run for repeatable testing). Otherwise grab the most
        // recent terminal run of the workflow — null is fine, we then return a schema-only
        // dump derived from the static graph.
        var candidate = executionId is { } eid
            ? await _db.WorkflowExecutions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid && e.WorkflowId == workflowId, ct)
            : await _db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.WorkflowId == workflowId && e.CompletedAt != null)
                .OrderByDescending(e => e.StartedAt)
                .FirstOrDefaultAsync(ct);

        var globals = await _globals.GetAllResolvedAsync(ct);
        var entries = new List<StepTestContextEntry>();

        // globals.* — these are reachable from every step, regardless of upstream wiring.
        // IsSecret globals are masked to "***" before they leave the API, matching the
        // /api/global-variables projection (GlobalVariablesController.Project) so the
        // step-test-context endpoint can't be used as a back-door to exfiltrate secret
        // values that the dedicated globals endpoint already redacts. The engine path
        // (StepTester.TestStepAsync) still uses the plaintext values to actually run
        // the step — only the context-preview surface that the UI displays is masked.
        var globalMeta = await _globals.GetAllAsync(ct);
        var secretNames = new HashSet<string>(
            globalMeta.Where(g => g.IsSecret).Select(g => g.Name),
            StringComparer.Ordinal);
        foreach (var (k, v) in globals.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var display = secretNames.Contains(k) ? "***" : v;
            entries.Add(new StepTestContextEntry($"globals.{k}", "globals", "global", display));
        }

        if (candidate is null)
        {
            // No execution available → dump just the schema (handle names + null values),
            // computed from the static graph. The user still gets autocomplete-style guidance.
            foreach (var ancestorId in ancestors)
            {
                var node = definition.FindNode(ancestorId);
                if (node is null) continue;
                var handle = definition.OutputNameByStepId.TryGetValue(ancestorId, out var alias) ? alias : ancestorId;
                entries.Add(new StepTestContextEntry($"{handle}.output", ancestorId, "output", null));
                entries.Add(new StepTestContextEntry($"{handle}.error", ancestorId, "error", null));
                entries.Add(new StepTestContextEntry($"{handle}.success", ancestorId, "success", null));
            }
            return new StepTestContext(null, null, null, entries);
        }

        var stepRows = await _db.StepExecutions.AsNoTracking()
            .Where(s => s.WorkflowExecutionId == candidate.Id && ancestors.Contains(s.StepId))
            .Select(s => new { s.StepId, s.Output, s.ErrorOutput, s.Status, s.OutputParametersJson })
            .ToListAsync(ct);

        var rowByStepId = stepRows.ToDictionary(r => r.StepId, StringComparer.Ordinal);

        foreach (var ancestorId in ancestors)
        {
            var handle = definition.OutputNameByStepId.TryGetValue(ancestorId, out var alias) ? alias : ancestorId;
            if (!rowByStepId.TryGetValue(ancestorId, out var row))
            {
                // Step never ran in this execution (skipped, or graph branch wasn't taken).
                // Emit schema-only entries so the user can still mock them by hand.
                entries.Add(new StepTestContextEntry($"{handle}.output", ancestorId, "output", null));
                entries.Add(new StepTestContextEntry($"{handle}.error", ancestorId, "error", null));
                entries.Add(new StepTestContextEntry($"{handle}.success", ancestorId, "success", null));
                continue;
            }

            entries.Add(new StepTestContextEntry($"{handle}.output", ancestorId, "output", row.Output));
            entries.Add(new StepTestContextEntry($"{handle}.error", ancestorId, "error", row.ErrorOutput));
            entries.Add(new StepTestContextEntry($"{handle}.success", ancestorId, "success",
                row.Status == ExecutionStatus.Succeeded ? "true" : "false"));

            if (string.IsNullOrEmpty(row.OutputParametersJson)) continue;
            try
            {
                using var doc = JsonDocument.Parse(row.OutputParametersJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var v = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.GetRawText();
                    entries.Add(new StepTestContextEntry(
                        $"{handle}.param.{prop.Name}", ancestorId, "param", v));
                }
            }
            catch (JsonException)
            {
                // Corrupt OutputParametersJson — we don't want to fail the whole context
                // build over one bad row. Emit a marker entry so the user can spot it in
                // the UI and ignore the rest.
                entries.Add(new StepTestContextEntry(
                    $"{handle}.param.<invalid>", ancestorId, "param", null));
            }
        }

        return new StepTestContext(candidate.Id, candidate.StartedAt, candidate.Status.ToString(), entries);
    }

}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Writes a JSON object to <see cref="Core.Models.WorkflowExecution.ReturnData"/>.
/// When the workflow is invoked via a <c>startWorkflow</c> step, each key of
/// that object is surfaced to the caller as <c>{{stepId.param.key}}</c>.
///
/// Config:
///   data: { key1: "literal or {{template}}", key2: "...", ... }
///
/// Concurrency: multiple returnData steps on parallel branches can write the same row.
/// The write goes through ExecuteUpdate, which bypasses tracked-entity state, so there
/// is no stale-entity exception across scopes. Which branch wins is not defined — the
/// semantic is last-write-wins on the whole JSON, not per key, so a workflow should use
/// a single terminal returnData step.
/// </summary>
public class ReturnDataActivity : IActivityExecutor
{
    private readonly NodePilotDbContext _db;
    private readonly OutputRedactor? _redactor;

    // Cap the serialized ReturnData so a single misbehaving workflow (or a caller trying
    // to stuff secrets) can't blow the column / audit trail.
    private const int MaxReturnDataChars = 32 * 1024;

    // Per-value cap. Truncating the serialized JSON would shred string-escape sequences
    // and trailing braces — the parent's JsonDocument.Parse in StartWorkflowActivity then
    // silently catches the exception and the child's returnData becomes empty. Capping
    // each value before serialization keeps the envelope syntactically valid.
    private const int MaxPerValueChars = 8 * 1024;
    private const string PerValueTruncationMarker = "…(truncated)";

    public ReturnDataActivity(NodePilotDbContext db, OutputRedactor? redactor = null)
    {
        _db = db;
        _redactor = redactor;
    }

    public string ActivityType => "returnData";

    public async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        if (!config.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = "returnData: 'data' must be a JSON object of key/value pairs",
            };
        }

        // The engine resolves variables in values before invoking this executor.
        // (non-runScript activities go through ResolveVariables on the config), so dataEl is plain.
        // Per-value cap is applied here so each value stays small enough that the envelope as a
        // whole almost always fits inside MaxReturnDataChars.
        var outputParams = new Dictionary<string, string>();
        foreach (var prop in dataEl.EnumerateObject())
        {
            var raw = PowerShellOperation.JsonElementToScalarString(prop.Value);
            outputParams[prop.Name] = raw.Length > MaxPerValueChars
                ? raw[..MaxPerValueChars] + PerValueTruncationMarker
                : raw;
        }

        var json = JsonSerializer.Serialize(outputParams);

        // Run ReturnData through the redactor — a careless workflow that echoes a secret
        // into returnData would otherwise persist it unmasked to the WorkflowExecution.ReturnData
        // column and flow it up to any startWorkflow parent.
        var persistJson = _redactor?.Redact(json) ?? json;

        // Hard envelope cap: even with per-value capping, a workflow with thousands of keys
        // can still exceed the column budget. Failing with a clear error beats silently
        // string-cutting the JSON (which would shred escapes and make the parent's
        // JsonDocument.Parse fall back to "no returnData").
        if (persistJson.Length > MaxReturnDataChars)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"returnData payload is {persistJson.Length} chars after per-value capping "
                    + $"(limit {MaxReturnDataChars}). Reduce the number of keys or shrink individual values.",
            };
        }

        // Atomic update — avoids fetching a tracked entity from this scope's DbContext
        // while another scope's context might also be tracking the same row.
        await _db.WorkflowExecutions
            .Where(e => e.Id == context.WorkflowExecutionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.ReturnData, persistJson), ct);

        return new ActivityResult
        {
            Success = true,
            Output = json,
            OutputParameters = outputParams,
        };
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
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
/// Concurrency (audit M10): multiple returnData steps on parallel branches used to race —
/// each fetched the execution row via its scope-local DbContext, set ReturnData, saved, and
/// EF's default last-writer-wins gave nondeterministic results. The fix is two-fold:
///   1. per-execution SemaphoreSlim serializes writes within a single process.
///   2. ExecuteUpdate bypasses tracked-entity state entirely — no stale-entity concurrency
///      exception between the fetch and the update from a different scope.
///
/// Designers are still encouraged to place a single terminal returnData step; this fix just
/// makes the "last-write" semantic deterministic rather than racy.
/// </summary>
public class ReturnDataActivity : IActivityExecutor
{
    private readonly NodePilotDbContext _db;
    private readonly OutputRedactor? _redactor;

    // Process-wide: one semaphore per WorkflowExecutionId. Slots are released by the finally
    // block below. We don't aggressively evict completed executions — the dictionary grows
    // linearly with lifetime executions which is bounded (existing executions live forever
    // in the DB but fewer than a handful of returnData writes per execution) and the steady
    // state is small.
    private static readonly object _locksGate = new();
    private static readonly ConcurrentDictionary<Guid, ExecutionLock> _perExecutionLocks = new();

    internal static int ActiveLockCount => _perExecutionLocks.Count;

    // Cap the serialized ReturnData so a single misbehaving workflow (or a caller trying
    // to stuff secrets) can't blow the column / audit trail.
    private const int MaxReturnDataChars = 32 * 1024;

    // Per-value cap. Truncating the *serialized* JSON would shred string-escape sequences
    // and trailing braces — the parent's JsonDocument.Parse in StartWorkflowActivity then
    // silently catches the exception and the child's returnData becomes empty. Capping
    // each value BEFORE serialisation keeps the envelope syntactically valid.
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

        // Variables in values are already resolved by the engine before this executor is called
        // (non-runScript activities go through ResolveVariables on the config), so dataEl is plain.
        // Per-value cap is applied here so each value stays small enough that the envelope as a
        // whole almost always fits inside MaxReturnDataChars.
        var outputParams = new Dictionary<string, string>();
        foreach (var prop in dataEl.EnumerateObject())
        {
            var raw = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => prop.Value.GetRawText(),
            };
            outputParams[prop.Name] = raw.Length > MaxPerValueChars
                ? raw[..MaxPerValueChars] + PerValueTruncationMarker
                : raw;
        }

        var json = JsonSerializer.Serialize(outputParams);

        // H-9: run ReturnData through the redactor — a careless workflow that echoes a secret
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

        var executionLock = AcquireExecutionLock(context.WorkflowExecutionId);
        await executionLock.Gate.WaitAsync(ct);
        try
        {
            // Atomic update — avoids fetching a tracked entity from this scope's DbContext
            // while another scope's context might also be tracking the same row.
            await _db.WorkflowExecutions
                .Where(e => e.Id == context.WorkflowExecutionId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.ReturnData, persistJson), ct);
        }
        finally
        {
            executionLock.Gate.Release();
            ReleaseExecutionLock(context.WorkflowExecutionId, executionLock);
        }

        return new ActivityResult
        {
            Success = true,
            Output = json,
            OutputParameters = outputParams,
        };
    }

    private static ExecutionLock AcquireExecutionLock(Guid executionId)
    {
        lock (_locksGate)
        {
            if (!_perExecutionLocks.TryGetValue(executionId, out var executionLock))
            {
                executionLock = new ExecutionLock();
                _perExecutionLocks[executionId] = executionLock;
            }

            executionLock.RefCount++;
            return executionLock;
        }
    }

    private static void ReleaseExecutionLock(Guid executionId, ExecutionLock executionLock)
    {
        lock (_locksGate)
        {
            executionLock.RefCount--;
            if (executionLock.RefCount == 0)
            {
                _perExecutionLocks.TryRemove(KeyValuePair.Create(executionId, executionLock));
                executionLock.Gate.Dispose();
            }
        }
    }

    private sealed class ExecutionLock
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int RefCount { get; set; }
    }
}

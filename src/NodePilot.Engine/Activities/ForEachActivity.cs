using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Iterates over a collection and invokes a child workflow once per item —
/// NodePilot's equivalent of a for-each / foreach-parallel loop. Each iteration
/// gets its own full <see cref="WorkflowExecution"/> row, so per-item progress
/// and return-data are visible in the UI / execution list / SignalR stream.
///
/// Config:
///   items                : string, required. Template-resolved to either a JSON array
///                          or a newline-separated list. <c>itemsFormat</c> controls parsing.
///   itemsFormat          : "auto" | "json" | "lines". Default "auto" — tries JSON first,
///                          falls back to line-split (trimmed, empty lines dropped).
///   childWorkflowNameOrId: string, required. GUID or unique workflow name.
///   itemParameterName    : string. Default "item". Name of the per-iteration param handed
///                          to the child (available in child as {{manual.item}} etc.
///                          via manual-trigger input parameters).
///   indexParameterName   : string. Default "index". 0-based iteration index.
///   parameters           : object, optional. Additional static params forwarded to every child
///                          invocation (same shape as startWorkflow).
///   maxParallelism       : int, default 1. Concurrency. &lt;=0 means "no per-foreach limit"
///                          (still bounded by the engine-wide <see cref="ISubWorkflowGate"/>,
///                          which is shared with <see cref="StartWorkflowActivity"/> so an
///                          accidental thousand-item collection can't saturate the process).
///   continueOnError      : bool, default false. If true, iteration continues after a child
///                          failure; step succeeds iff every item succeeded.
///   timeoutSecondsPerItem: int, default 3600. Per-child timeout.
///
/// Outputs (OutputParameters):
///   total, succeeded, failed — counts
///   firstError               — error message from the first failing item, if any
///   results                  — JSON array of {index, item, status, executionId, error}
///
/// Guards:
///   - self-invocation (child == current workflow) rejected.
///   - call-depth tracked via __callDepth (shared with startWorkflow).
///   - reserved "__"-prefix rejected for user-supplied parameter keys (incl. the custom
///     itemParameterName/indexParameterName).
/// </summary>
public class ForEachActivity : IActivityExecutor
{
    // Soft upper bound on parallelism within a single forEach. Prevents a typo
    // (maxParallelism = 100000) from flooding the engine-wide gate queue. The
    // engine-wide cap still applies on top — a single forEach can spin up at most
    // 64 in-flight children, but cross-forEach + startWorkflow contention is
    // bounded by ISubWorkflowGate.Capacity.
    private const int MaxParallelismHardCap = 64;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NodePilotDbContext _db;
    private readonly ISubWorkflowGate _gate;
    private readonly ISubWorkflowAuthorizationResolver? _subWorkflowAuthz;

    public ForEachActivity(IServiceScopeFactory scopeFactory, NodePilotDbContext db, ISubWorkflowGate gate)
        : this(scopeFactory, db, gate, null)
    {
    }

    public ForEachActivity(
        IServiceScopeFactory scopeFactory,
        NodePilotDbContext db,
        ISubWorkflowGate gate,
        ISubWorkflowAuthorizationResolver? subWorkflowAuthz)
    {
        _scopeFactory = scopeFactory;
        _db = db;
        _gate = gate;
        _subWorkflowAuthz = subWorkflowAuthz;
    }

    public string ActivityType => "forEach";

    public async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var parsed = ParseConfig(config);
        if (parsed.Error is not null) return Fail(parsed.Error, sw);

        var items = ParseItemsOrError(parsed.ItemsRaw!, parsed.ItemsFormat);
        if (items.Error is not null) return Fail(items.Error, sw);
        if (items.List!.Count == 0) return EmptyCollectionResult(sw);

        var resolved = await ResolveChildWorkflowAsync(parsed.ChildWorkflowNameOrId!, ct);
        if (resolved.Error is not null) return Fail(resolved.Error, sw);

        var contextCheck = await ValidateCallContextAsync(context, resolved.Workflow!, ct);
        if (contextCheck.Error is not null) return Fail(contextCheck.Error, sw);

        var staticParams = CollectStaticParams(config, out var staticParamError);
        if (staticParamError is not null) return Fail(staticParamError, sw);

        var runCtx = new RunContext(
            Items: items.List,
            ChildWorkflow: resolved.Workflow!,
            StaticParams: staticParams,
            ItemParamName: parsed.ItemParamName,
            IndexParamName: parsed.IndexParamName,
            TimeoutPerItem: parsed.TimeoutPerItem,
            ContinueOnError: parsed.ContinueOnError,
            CurrentDepth: contextCheck.CurrentDepth,
            EffectiveParallelism: parsed.MaxParallelism <= 0
                ? MaxParallelismHardCap
                : Math.Min(parsed.MaxParallelism, MaxParallelismHardCap),
            StepId: context.StepId);

        var results = await RunIterationsAsync(runCtx, ct);
        return BuildAggregateResult(runCtx, results, sw);
    }

    private static ActivityResult Fail(string error, Stopwatch sw) =>
        new() { Success = false, ErrorOutput = error, Duration = sw.Elapsed };

    private static ActivityResult EmptyCollectionResult(Stopwatch sw) => new()
    {
        Success = true,
        Output = "forEach: empty collection — no iterations.",
        OutputParameters = new Dictionary<string, string>
        {
            ["total"] = "0",
            ["succeeded"] = "0",
            ["failed"] = "0",
            ["results"] = "[]",
        },
        Duration = sw.Elapsed,
    };

    private static ParsedConfig ParseConfig(JsonElement config)
    {
        var childWorkflowNameOrId = config.GetStringOrNull("childWorkflowNameOrId");
        if (string.IsNullOrWhiteSpace(childWorkflowNameOrId))
            return new ParsedConfig(Error: "forEach: 'childWorkflowNameOrId' is required");

        var itemsRaw = config.GetStringOrNull("items");
        if (itemsRaw is null)
            return new ParsedConfig(Error: "forEach: 'items' is required");

        var itemParamName = config.GetString("itemParameterName", "item");
        var indexParamName = config.GetString("indexParameterName", "index");

        // Reserved-prefix guard for the per-iteration parameter names. The __-prefix is the
        // engine's bookkeeping namespace (see __callDepth); letting a user steer __callDepth
        // via itemParameterName would bypass the recursion guard.
        if (itemParamName.StartsWith("__", StringComparison.OrdinalIgnoreCase)
            || indexParamName.StartsWith("__", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedConfig(Error: "forEach: itemParameterName / indexParameterName cannot start with '__' (reserved).");
        }

        return new ParsedConfig(
            ChildWorkflowNameOrId: childWorkflowNameOrId,
            ItemsRaw: itemsRaw,
            ItemsFormat: config.GetStringOrNull("itemsFormat")?.ToLowerInvariant() ?? "auto",
            ItemParamName: itemParamName,
            IndexParamName: indexParamName,
            // D8: clamp to a positive value before it reaches CancellationTokenSource(TimeSpan).
            // A user-supplied 0 produces TimeSpan.Zero which cancels the CTS immediately, and
            // a negative value throws ArgumentOutOfRangeException — neither is a useful per-item
            // budget. Treat <=0 as "use default" (3600s), same as we do for MaxParallelism.
            TimeoutPerItem: config.TryGetProperty("timeoutSecondsPerItem", out var t) && t.TryGetInt32(out var ts) && ts > 0 ? ts : 3600,
            ContinueOnError: config.GetBool("continueOnError", false),
            MaxParallelism: config.TryGetProperty("maxParallelism", out var mp) && mp.TryGetInt32(out var mpv) ? mpv : 1);
    }

    private static (List<string>? List, string? Error) ParseItemsOrError(string raw, string format)
    {
        List<string> list;
        try
        {
            list = ParseItems(raw, format);
        }
        catch (Exception ex)
        {
            return (null, $"forEach: failed to parse items ({format}): {ex.Message}");
        }

        // M-9: hard cap on items. A misconfigured upstream step (e.g. `Get-ADUser -Filter *`)
        // can produce a million-element array that detonates the engine — thousands of
        // WorkflowExecution rows, unbounded DbContext growth, blocked sub-workflow gate.
        // Caller is expected to filter / page upstream; we fail fast with a clear message.
        const int MaxItemCount = 10_000;
        if (list.Count > MaxItemCount)
        {
            return (null, $"forEach: items count {list.Count} exceeds limit of {MaxItemCount}. " +
                          "Pre-filter upstream (chunk / page / Where-Object) before the forEach.");
        }

        return (list, null);
    }

    private async Task<(Workflow? Workflow, string? Error)> ResolveChildWorkflowAsync(string nameOrId, CancellationToken ct)
    {
        Workflow? workflow;
        if (Guid.TryParse(nameOrId, out var id))
        {
            workflow = await _db.Workflows.FirstOrDefaultAsync(wf => wf.Id == id, ct);
        }
        else
        {
            // Exact-case wins, then case-insensitive; ambiguous names fail the step.
            var resolved = await WorkflowNameResolver.ResolveByNameAsync(_db.Workflows, nameOrId, ct);
            if (resolved.Outcome == WorkflowNameResolver.Outcome.Ambiguous)
                return (null, $"forEach: multiple workflows named '{nameOrId}' — disambiguate with the GUID");
            workflow = resolved.Workflow;
        }

        if (workflow is null)
            return (null, $"forEach: child workflow '{nameOrId}' not found");
        if (!workflow.IsEnabled)
            return (null, $"forEach: child workflow '{workflow.Name}' is disabled");
        return (workflow, null);
    }

    private async Task<(int CurrentDepth, string? Error)> ValidateCallContextAsync(StepExecutionContext context, Workflow childWorkflow, CancellationToken ct)
    {
        // Self-invocation guard — identical to startWorkflow.
        var parentExec = await _db.WorkflowExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == context.WorkflowExecutionId, ct);
        if (parentExec is not null && parentExec.WorkflowId == childWorkflow.Id)
            return (0, "forEach: self-invocation is not allowed (direct recursion)");

        // RBAC sub-workflow runtime check — identical model to StartWorkflowActivity.
        // Without this, an operator who lost folder access between Publish and Run could
        // still iterate a forEach over a child workflow they no longer have permission to
        // start. Defense in depth: PrePublishChecklist enforces the same check at save
        // time, but folder permissions can be revoked or rotated mid-flight.
        if (_subWorkflowAuthz is not null && parentExec is not null)
        {
            var blocked = await _subWorkflowAuthz.IsBlockedAsync(parentExec, childWorkflow, ct);
            if (blocked is not null)
                return (0, $"forEach: {blocked}");
        }

        // Call-depth guard.
        var currentDepth = 0;
        if (context.Variables.TryGetValue($"manual.{WorkflowRecursion.CallDepthKey}", out var depthStr)
            && int.TryParse(depthStr, out var parsed))
        {
            currentDepth = parsed;
        }
        if (currentDepth >= WorkflowRecursion.MaxCallDepth)
            return (currentDepth, $"forEach: call depth limit ({WorkflowRecursion.MaxCallDepth}) exceeded");

        return (currentDepth, null);
    }

    private static Dictionary<string, string> CollectStaticParams(JsonElement config, out string? error)
    {
        error = null;
        var staticParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!config.TryGetProperty("parameters", out var paramsEl) || paramsEl.ValueKind != JsonValueKind.Object)
            return staticParams;

        foreach (var prop in paramsEl.EnumerateObject())
        {
            if (prop.Name.StartsWith("__", StringComparison.OrdinalIgnoreCase))
            {
                error = $"forEach: parameter name '{prop.Name}' is reserved ('__'-prefix)";
                return staticParams;
            }
            staticParams[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => prop.Value.GetRawText(),
            };
        }
        return staticParams;
    }

    private async Task<ItemResult[]> RunIterationsAsync(RunContext rctx, CancellationToken ct)
    {
        var results = new ItemResult?[rctx.Items.Count];
        var cancelRequested = 0;
        var nextIndex = -1;
        using var itemsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var workers = Enumerable.Range(0, Math.Min(rctx.EffectiveParallelism, rctx.Items.Count))
            .Select(_ => RunWorkerAsync(rctx, results, itemsCts, () => Interlocked.Increment(ref nextIndex), b =>
            {
                if (b && Interlocked.Exchange(ref cancelRequested, 1) == 0)
                    return itemsCts.CancelAsync();
                return Task.CompletedTask;
            }))
            .ToArray();
        await Task.WhenAll(workers);

        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= new ItemResult(i, rctx.Items[i], "Skipped", null, "cancelled before start");
        }
        return results.Select(r => r!).ToArray();
    }

    private async Task RunWorkerAsync(
        RunContext rctx,
        ItemResult?[] results,
        CancellationTokenSource itemsCts,
        Func<int> nextIndexFn,
        Func<bool, Task> requestCancelIfFailed)
    {
        while (!itemsCts.IsCancellationRequested)
        {
            var index = nextIndexFn();
            if (index >= rctx.Items.Count) return;

            var item = rctx.Items[index];

            // Engine-wide back-pressure: also bounded by ISubWorkflowGate so cross-forEach
            // + startWorkflow contention can't blow past the engine cap. The fixed worker
            // count is the local concurrency budget, so only this worker set can wait on
            // the global gate instead of all items racing it simultaneously.
            var globalAcquired = false;
            try
            {
                try
                {
                    await _gate.WaitAsync(itemsCts.Token);
                    globalAcquired = true;
                }
                catch (OperationCanceledException)
                {
                    results[index] = new ItemResult(index, item, "Skipped", null, "cancelled before start");
                    return;
                }

                var (childExec, errorMsg) = await ExecuteOneAsync(rctx, index, item, itemsCts.Token);

                var status = childExec?.Status.ToString() ?? "Failed";
                var succeeded = childExec?.Status == ExecutionStatus.Succeeded;
                if (!succeeded && errorMsg is null)
                {
                    errorMsg = childExec?.ErrorMessage ?? "child workflow did not succeed";
                }

                results[index] = new ItemResult(index, item, status, childExec?.Id, succeeded ? null : errorMsg);

                // Fail-fast: if an item fails and we don't continueOnError, cancel remaining.
                if (!succeeded && !rctx.ContinueOnError)
                    await requestCancelIfFailed(true);
            }
            finally
            {
                if (globalAcquired) _gate.Release();
            }
        }
    }

    private async Task<(WorkflowExecution? ChildExec, string? Error)> ExecuteOneAsync(
        RunContext rctx, int index, string item, CancellationToken parentCt)
    {
        // Merge per-iteration params on top of static. itemParamName/indexParamName
        // are seeded LAST so they can't be shadowed by user-supplied static entries
        // (the static loop already rejected __-prefix, so the only collision path is
        // a user naming a static param "item" / "index" — in that case the iteration
        // value wins, documented behavior).
        var childParams = new Dictionary<string, string>(rctx.StaticParams, StringComparer.OrdinalIgnoreCase)
        {
            [rctx.ItemParamName] = item,
            [rctx.IndexParamName] = index.ToString(),
            [WorkflowRecursion.CallDepthKey] = (rctx.CurrentDepth + 1).ToString(),
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(rctx.TimeoutPerItem));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt, timeoutCts.Token);

        try
        {
            var childExec = await engine.ExecuteAsync(rctx.ChildWorkflow, $"forEach:{rctx.StepId}[{index}]", linkedCts.Token, childParams);
            return (childExec, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return (null, $"timed out after {rctx.TimeoutPerItem}s");
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static ActivityResult BuildAggregateResult(RunContext rctx, ItemResult[] results, Stopwatch sw)
    {
        var succeededCount = results.Count(r => r.Status == "Succeeded");
        var failedCount = results.Count(r => r.Status != "Succeeded" && r.Status != "Skipped");
        var skippedCount = results.Count(r => r.Status == "Skipped");
        var firstError = results.FirstOrDefault(r => !string.IsNullOrEmpty(r.Error))?.Error;

        // Serialize per-item results as JSON for downstream consumption via {{step.param.results}}.
        var resultsJson = JsonSerializer.Serialize(results.Select(r => new
        {
            index = r.Index,
            item = r.Item,
            status = r.Status,
            executionId = r.ExecutionId?.ToString(),
            error = r.Error,
        }));

        var allSucceeded = failedCount == 0 && skippedCount == 0;
        // In continueOnError mode the step can still succeed overall even with some failures —
        // downstream can branch on {{step.param.failed}} > 0 to react. Without continueOnError,
        // any failure bubbles up.
        var stepSuccess = allSucceeded || (rctx.ContinueOnError && succeededCount > 0 && skippedCount == 0);

        var outputParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["total"] = rctx.Items.Count.ToString(),
            ["succeeded"] = succeededCount.ToString(),
            ["failed"] = failedCount.ToString(),
            ["skipped"] = skippedCount.ToString(),
            ["results"] = resultsJson,
        };
        if (!string.IsNullOrEmpty(firstError))
            outputParams["firstError"] = firstError;

        return new ActivityResult
        {
            Success = stepSuccess,
            Output = $"forEach '{rctx.ChildWorkflow.Name}': {succeededCount}/{rctx.Items.Count} succeeded"
                     + (failedCount > 0 ? $", {failedCount} failed" : "")
                     + (skippedCount > 0 ? $", {skippedCount} skipped" : ""),
            ErrorOutput = stepSuccess ? null : firstError,
            OutputParameters = outputParams,
            Duration = sw.Elapsed,
        };
    }

    private sealed record ParsedConfig(
        string? ChildWorkflowNameOrId = null,
        string? ItemsRaw = null,
        string ItemsFormat = "auto",
        string ItemParamName = "item",
        string IndexParamName = "index",
        int TimeoutPerItem = 3600,
        bool ContinueOnError = false,
        int MaxParallelism = 1,
        string? Error = null);

    private sealed record RunContext(
        List<string> Items,
        Workflow ChildWorkflow,
        Dictionary<string, string> StaticParams,
        string ItemParamName,
        string IndexParamName,
        int TimeoutPerItem,
        bool ContinueOnError,
        int CurrentDepth,
        int EffectiveParallelism,
        string StepId);

    private static List<string> ParseItems(string raw, string format)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return new List<string>();

        var tryJson = format == "json"
            || (format == "auto" && (raw.StartsWith('[') || raw.StartsWith('{')));

        if (tryJson)
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    list.Add(el.ValueKind switch
                    {
                        JsonValueKind.String => el.GetString() ?? string.Empty,
                        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                        _ => el.GetRawText(),
                    });
                }
                return list;
            }
            if (format == "json")
                throw new InvalidOperationException("expected JSON array, got " + doc.RootElement.ValueKind);
            // auto-mode + non-array JSON: fall through to line-split
        }

        return raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim('\r', ' ', '\t'))
            .Where(s => s.Length > 0)
            .ToList();
    }

    private sealed record ItemResult(int Index, string Item, string Status, Guid? ExecutionId, string? Error);
}

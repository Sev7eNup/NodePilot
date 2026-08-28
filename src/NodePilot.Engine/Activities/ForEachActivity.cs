using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Execution;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Iterates over a collection and invokes a child workflow once per item — a for-each /
/// foreach-parallel loop. Each iteration runs as its own <see cref="WorkflowExecution"/>, so
/// per-item progress and return data are visible in the UI, execution list, and SignalR stream.
/// Item parsing supports the <c>"auto"</c>, <c>"json"</c>, and <c>"lines"</c> formats.
/// Supports configurable parallelism (capped by <see cref="ISubWorkflowGate"/>), per-item
/// timeouts, and a continue-on-error mode that lets the loop finish after individual failures.
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
    // Per-workflow cap on the child, on top of maxParallelism: that one bounds this loop, this
    // one bounds the child across every caller. Required on both constructors so no path runs
    // unlimited.
    private readonly IWorkflowConcurrencyGate _workflowConcurrency;
    private readonly ISubWorkflowAuthorizationResolver? _subWorkflowAuthz;

    public ForEachActivity(
        IServiceScopeFactory scopeFactory,
        NodePilotDbContext db,
        ISubWorkflowGate gate,
        IWorkflowConcurrencyGate workflowConcurrency)
        : this(scopeFactory, db, gate, workflowConcurrency, null)
    {
    }

    public ForEachActivity(
        IServiceScopeFactory scopeFactory,
        NodePilotDbContext db,
        ISubWorkflowGate gate,
        IWorkflowConcurrencyGate workflowConcurrency,
        ISubWorkflowAuthorizationResolver? subWorkflowAuthz)
    {
        _scopeFactory = scopeFactory;
        _db = db;
        _gate = gate;
        _workflowConcurrency = workflowConcurrency;
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

        // Releases the global step-gate slot for the iterations, like StartWorkflowActivity does
        // for a synchronous child: both wait on children drawing from the same gate, so holding
        // the slot while waiting could deadlock a parent against the children it depends on.
        var results = await WorkflowScheduler.RunWithCurrentStepGateReleasedAsync(
            () => RunIterationsAsync(runCtx, ct), ct);
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
            // Clamps to a positive value before it reaches CancellationTokenSource(TimeSpan): a
            // user-supplied 0 produces TimeSpan.Zero (cancels immediately), and a negative value
            // throws ArgumentOutOfRangeException. Non-positive values fall back to the 3600s
            // default.
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

        // Hard cap on items: a misconfigured upstream step (e.g. Get-ADUser -Filter *) could
        // otherwise produce a huge array, overwhelming the engine with WorkflowExecution rows
        // and unbounded DbContext growth. Callers are expected to filter or page upstream.
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
        // Exact-case wins, then case-insensitive; ambiguous names fail the step.
        var (outcome, workflow) = await SubWorkflowInvocation.ResolveChildWorkflowAsync(_db, nameOrId, ct);
        return outcome switch
        {
            SubWorkflowInvocation.ChildOutcome.Ambiguous =>
                (null, $"forEach: multiple workflows named '{nameOrId}' — disambiguate with the GUID"),
            SubWorkflowInvocation.ChildOutcome.NotFound =>
                (null, $"forEach: child workflow '{nameOrId}' not found"),
            SubWorkflowInvocation.ChildOutcome.Disabled =>
                (null, $"forEach: child workflow '{workflow!.Name}' is disabled"),
            _ => (workflow, null),
        };
    }

    private async Task<(int CurrentDepth, string? Error)> ValidateCallContextAsync(StepExecutionContext context, Workflow childWorkflow, CancellationToken ct)
    {
        // Self-invocation guard — identical to startWorkflow.
        var parentExec = await SubWorkflowInvocation.LoadParentExecutionAsync(_db, context.WorkflowExecutionId, ct);
        if (SubWorkflowInvocation.IsSelfInvocation(parentExec, childWorkflow))
            return (0, "forEach: self-invocation is not allowed (direct recursion)");

        // RBAC check for the sub-workflow, matching StartWorkflowActivity. Folder permissions
        // can change after a workflow is published, so PrePublishChecklist's save-time check
        // is not enough on its own — this runtime check keeps a lost grant from being exploited.
        var blocked = await SubWorkflowInvocation.GetAuthorizationBlockAsync(
            _subWorkflowAuthz, parentExec, childWorkflow, ct);
        if (blocked is not null)
            return (0, $"forEach: {blocked}");

        // Call-depth guard.
        var currentDepth = SubWorkflowInvocation.CurrentCallDepth(context);
        if (currentDepth >= WorkflowRecursion.MaxCallDepth)
            return (currentDepth, $"forEach: call depth limit ({WorkflowRecursion.MaxCallDepth}) exceeded");

        return (currentDepth, null);
    }

    private static Dictionary<string, string> CollectStaticParams(JsonElement config, out string? error)
    {
        var staticParams = SubWorkflowInvocation.CollectParameters(config, out var reservedKey);
        error = reservedKey is null
            ? null
            : $"forEach: parameter name '{reservedKey}' is reserved ('__'-prefix)";
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

            // Bounded by ISubWorkflowGate too, so cross-forEach and startWorkflow contention
            // cannot exceed the engine-wide cap. The fixed worker count is the local budget,
            // so only these workers wait on the global gate instead of every item racing it.
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
        // Merges per-iteration params on top of the static ones. itemParamName/indexParamName
        // are set last so they cannot be shadowed by a user-supplied static entry with the same
        // name — in that collision case the iteration value wins, by design.
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

        var concurrencySlotHeld = false;
        try
        {
            // The child was resolved once for the whole loop, so its limit may have changed
            // since. Re-read per item — one indexed lookup beside starting a whole execution —
            // otherwise a limit raised mid-loop would not take effect until the loop ends.
            // Falls back to the loop's snapshot when the scope has no context, which is the
            // behaviour this loop had before the re-read existed.
            var scopedDb = scope.ServiceProvider.GetService<NodePilotDbContext>();
            var limit = scopedDb is null
                ? rctx.ChildWorkflow.MaxConcurrentExecutions
                : await scopedDb.Workflows
                    .AsNoTracking()
                    .Where(w => w.Id == rctx.ChildWorkflow.Id)
                    .Select(w => w.MaxConcurrentExecutions)
                    .FirstOrDefaultAsync(linkedCts.Token);

            // Taken after ISubWorkflowGate, which the caller still holds — the same order both
            // sub-workflow activities use. The per-item timeout covers this wait.
            await _workflowConcurrency.AcquireAsync(rctx.ChildWorkflow.Id, limit, linkedCts.Token);
            concurrencySlotHeld = true;

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
        finally
        {
            if (concurrencySlotHeld) _workflowConcurrency.Release(rctx.ChildWorkflow.Id);
        }
    }

    private static ActivityResult BuildAggregateResult(RunContext rctx, ItemResult[] results, Stopwatch sw)
    {
        var succeededCount = results.Count(r => r.Status == "Succeeded");
        var failedCount = results.Count(r => r.Status != "Succeeded" && r.Status != "Skipped");
        var skippedCount = results.Count(r => r.Status == "Skipped");
        var firstError = results.FirstOrDefault(r => !string.IsNullOrEmpty(r.Error))?.Error;

        // Serializes per-item results as JSON, exposed downstream as {{step.param.results}}.
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
                    list.Add(PowerShellOperation.JsonElementToScalarString(el));
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

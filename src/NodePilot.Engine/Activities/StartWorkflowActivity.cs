using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Execution;
using NodePilot.Core.Telemetry;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Invokes another workflow (by id or name) from inside the current workflow -
/// the NodePilot equivalent of SCOrch's "Invoke Runbook". Runs synchronously:
/// the parent step waits for the child to complete and then surfaces the
/// child's <c>returnData</c> as this step's OutputParameters so downstream
/// nodes can consume it via <c>{{stepId.param.key}}</c>.
///
/// Config:
///   workflowNameOrId : string, required. GUID or unique workflow name.
///   parameters       : object, optional. Forwarded to the child as manualTrigger inputs.
///   timeoutSeconds   : int, optional (default 3600).
///   waitForCompletion: bool, optional (default true). When false, fire-and-forget -
///                       output contains only the child executionId.
///
/// Guards:
///   - self-call (child workflow == current workflow) is rejected.
///   - call-depth is tracked via the reserved <c>__callDepth</c> input parameter;
///     MAX_CALL_DEPTH (10) prevents runaway recursion.
/// </summary>
public class StartWorkflowActivity : IActivityExecutor
{
    // C3: per the catalog docs / CLAUDE.md, default child timeout is 3600s. The code
    // previously fell through to "no CancelAfter" if the field was missing, letting a
    // stuck child workflow pin the parent step indefinitely.
    internal const int DefaultChildTimeoutSeconds = 3600;

    private static readonly System.Diagnostics.Metrics.Counter<long> _subWorkflowInvocations =
        EngineMetrics.Meter.CreateCounter<long>(
            "nodepilot.subworkflow.invocations", unit: "1",
            description: "Sub-workflow invocations via startWorkflow, tagged by wait_mode and depth_bucket.");
    private static readonly System.Diagnostics.Metrics.Counter<long> _subWorkflowDepthExceeded =
        EngineMetrics.Meter.CreateCounter<long>(
            "nodepilot.subworkflow.depth_exceeded", unit: "1",
            description: "startWorkflow attempts that hit the MaxCallDepth limit.");

    // The engine-wide sub-workflow concurrency cap is owned by ISubWorkflowGate so
    // ForEachActivity participates in the same back-pressure pool. See
    // InMemorySubWorkflowGate for the rationale on the 128 default.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NodePilotDbContext _db;
    private readonly ISubWorkflowGate _gate;
    private readonly IExecutionDispatchQueue? _dispatchQueue;
    private readonly ISubWorkflowAuthorizationResolver? _subWorkflowAuthz;
    private readonly ILogger<StartWorkflowActivity>? _logger;

    public StartWorkflowActivity(IServiceScopeFactory scopeFactory, NodePilotDbContext db, ISubWorkflowGate gate)
        : this(scopeFactory, db, gate, null, null)
    {
    }

    public StartWorkflowActivity(
        IServiceScopeFactory scopeFactory,
        NodePilotDbContext db,
        ISubWorkflowGate gate,
        IExecutionDispatchQueue? dispatchQueue)
        : this(scopeFactory, db, gate, dispatchQueue, null)
    {
    }

    public StartWorkflowActivity(
        IServiceScopeFactory scopeFactory,
        NodePilotDbContext db,
        ISubWorkflowGate gate,
        IExecutionDispatchQueue? dispatchQueue,
        ISubWorkflowAuthorizationResolver? subWorkflowAuthz,
        ILogger<StartWorkflowActivity>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _db = db;
        _gate = gate;
        _dispatchQueue = dispatchQueue;
        _subWorkflowAuthz = subWorkflowAuthz;
        _logger = logger;
    }

    public string ActivityType => "startWorkflow";

    public async Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var span = WorkflowEngine.ActivitiesSource.StartActivity(
            "startWorkflow.invoke", System.Diagnostics.ActivityKind.Internal);

        var workflowNameOrId = config.GetStringOrNull("workflowNameOrId");
        if (string.IsNullOrWhiteSpace(workflowNameOrId))
        {
            return new ActivityResult { Success = false, ErrorOutput = "startWorkflow: 'workflowNameOrId' is required" };
        }

        // Raw, node-set value (null when the author didn't specify one). The SYNCHRONOUS path
        // applies a 3600s default (C3) so a slow child can't pin the parent step forever. The
        // FIRE-AND-FORGET path intentionally has NO default: a detached child blocks nothing, so
        // we impose no wall-clock ceiling on legitimately long work — it self-completes via the
        // step-always-resolves + guaranteed-finalization contract. A timeout there is opt-in per
        // node (and, unlike before, is now actually honored on the detached path).
        var explicitTimeoutSeconds = config.GetOptionalPositiveInt("timeoutSeconds");
        var timeoutSeconds = explicitTimeoutSeconds ?? DefaultChildTimeoutSeconds;
        var waitForCompletion = config.GetBool("waitForCompletion", true);

        // Locate child workflow — GUID first; by name exact-case wins, then case-insensitive.
        Workflow? childWorkflow;
        if (Guid.TryParse(workflowNameOrId, out var id))
        {
            childWorkflow = await _db.Workflows.FirstOrDefaultAsync(wf => wf.Id == id, ct);
        }
        else
        {
            var resolved = await WorkflowNameResolver.ResolveByNameAsync(_db.Workflows, workflowNameOrId, ct);
            if (resolved.Outcome == WorkflowNameResolver.Outcome.Ambiguous)
            {
                return new ActivityResult
                {
                    Success = false,
                    ErrorOutput = $"startWorkflow: multiple workflows named '{workflowNameOrId}' — disambiguate with the GUID",
                    Duration = sw.Elapsed,
                };
            }
            childWorkflow = resolved.Workflow;
        }

        if (childWorkflow is null)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: workflow '{workflowNameOrId}' not found",
                Duration = sw.Elapsed,
            };
        }
        if (!childWorkflow.IsEnabled)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: workflow '{childWorkflow.Name}' is disabled",
                Duration = sw.Elapsed,
            };
        }

        // Self-call guard - requires the parent workflow id. It is not on the context directly,
        // so we derive it from the current execution row.
        var parentExec = await _db.WorkflowExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == context.WorkflowExecutionId, ct);
        if (parentExec is not null && parentExec.WorkflowId == childWorkflow.Id)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = "startWorkflow: self-invocation is not allowed (direct recursion)",
                Duration = sw.Elapsed,
            };
        }

        // RBAC sub-workflow runtime check (Defense-in-Depth — the publish-time check in
        // PrePublishChecklist already rejects unauthorized startWorkflow refs at save time,
        // but folder permissions can be revoked between Publish and Run, and trigger-driven
        // runs need a re-check anyway because the publishing user may differ from the
        // effective principal at fire time). Effective principal:
        //   - manual run: parentExec.StartedByUserId
        //   - trigger-driven run: parent workflow's LastModifiedByUserId (best proxy V1)
        // If neither resolves, the run lacks a principal — refuse the cross-folder call.
        if (_subWorkflowAuthz is not null && parentExec is not null)
        {
            var blocked = await _subWorkflowAuthz.IsBlockedAsync(parentExec, childWorkflow, ct);
            if (blocked is not null)
            {
                return new ActivityResult
                {
                    Success = false,
                    ErrorOutput = $"startWorkflow: {blocked}",
                    Duration = sw.Elapsed,
                };
            }
        }

        // Call-depth guard - read from the reserved variable the engine places into context.Variables
        // ("manual.__callDepth" when passed via inputParameters).
        var currentDepth = 0;
        if (context.Variables.TryGetValue($"manual.{WorkflowRecursion.CallDepthKey}", out var depthStr)
            && int.TryParse(depthStr, out var parsed))
        {
            currentDepth = parsed;
        }
        if (currentDepth >= WorkflowRecursion.MaxCallDepth)
        {
            _subWorkflowDepthExceeded.Add(1);
            span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "max call depth exceeded");
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: call depth limit ({WorkflowRecursion.MaxCallDepth}) exceeded",
                Duration = sw.Elapsed,
            };
        }

        var childCallDepth = currentDepth + 1;
        var waitModeTag = waitForCompletion ? "wait" : "fire_and_forget";
        var depthBucket = childCallDepth <= 1 ? "1"
            : childCallDepth <= 3 ? "2-3"
            : childCallDepth <= 5 ? "4-5"
            : "6+";
        span?.SetTag(NodePilot.Core.Telemetry.TelemetryConstants.Attributes.SubWorkflowChildId, childWorkflow.Id.ToString());
        span?.SetTag(NodePilot.Core.Telemetry.TelemetryConstants.Attributes.SubWorkflowWaitMode, waitModeTag);
        span?.SetTag(NodePilot.Core.Telemetry.TelemetryConstants.Attributes.WorkflowCallDepth, childCallDepth);
        _subWorkflowInvocations.Add(1,
            new KeyValuePair<string, object?>("wait_mode", waitModeTag),
            new KeyValuePair<string, object?>("depth_bucket", depthBucket));

        // Collect child input parameters. Dictionary is OrdinalIgnoreCase so `Foo` and `foo`
        // collide - PowerShell and the template resolver are case-insensitive too. Critical
        // consequence: the __callDepth key we set below must be protected against an attacker
        // who supplies "__callDepth", "__CALLDEPTH", "__CallDepth", etc. as a user parameter
        // and resets the counter. Any key starting with "__" is reserved for engine bookkeeping
        // and rejected on ingest, case-insensitively.
        var childParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.TryGetProperty("parameters", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in paramsEl.EnumerateObject())
            {
                // Reserved-prefix guard (H5): case-insensitive. Previously the reserved key was
                // seeded first and then overwritten by user params because the user-loop came
                // second. Reject ALL "__"-prefixed keys from user input - they're ours.
                if (prop.Name.StartsWith("__", StringComparison.OrdinalIgnoreCase))
                {
                    return new ActivityResult
                    {
                        Success = false,
                        ErrorOutput = $"startWorkflow: parameter name '{prop.Name}' is reserved (keys starting with '__' are used by the engine). Rename the parameter.",
                        Duration = sw.Elapsed,
                    };
                }

                childParams[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    _ => prop.Value.GetRawText(),
                };
            }
        }
        // Seed the reserved depth counter AFTER the user loop so even if the reject above were
        // bypassed, the engine's value always wins. Belt-and-suspenders on H5.
        childParams[WorkflowRecursion.CallDepthKey] = childCallDepth.ToString();

        using var timeoutCts = new CancellationTokenSource();
        // timeoutSeconds is now non-null (C3 default applied above) — always arm CancelAfter.
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (!waitForCompletion)
        {
            // Acquire a slot before scheduling the fire-and-forget; if the system is saturated,
            // we'd rather reject the start than queue unbounded background tasks.
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(5), ct))
            {
                return new ActivityResult
                {
                    Success = false,
                    ErrorOutput = $"startWorkflow: engine is at sub-workflow concurrency limit ({_gate.Capacity}); try again later",
                    Duration = sw.Elapsed,
                };
            }

            // H-15: fire-and-forget inherits the PARENT's cancellation token. Previously the
            // child ran with CancellationToken.None, which meant a `POST /cancel` on the parent
            // didn't reach it - a detached child kept running on the engine until natural
            // termination. Linking to the parent makes `cancel-all` actually quarantine
            // everything a disabled workflow fans out.
            var parentCancellation = CancellationToken.None;
            if (parentExec is not null
                && WorkflowEngine.TryGetExecutionCancellation(parentExec.Id, out var parentCt))
            {
                parentCancellation = parentCt;
            }

            // Release the slot if enqueue/scheduling itself throws before the detached path owns it.
            try
            {
                if (_dispatchQueue is not null)
                {
                    using var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(ct, parentCancellation);
                    await _dispatchQueue.EnqueueAsync(
                        workerCt => ExecuteDetachedChildAsync(
                            childWorkflow,
                            context.WorkflowExecutionId,
                            context.StepId,
                            childParams,
                            childCallDepth,
                            explicitTimeoutSeconds,
                            parentCancellation,
                            workerCt),
                        dispatchCts.Token);
                }
                else
                {
                    _ = ExecuteDetachedChildAsync(
                        childWorkflow,
                        context.WorkflowExecutionId,
                        context.StepId,
                        childParams,
                        childCallDepth,
                        explicitTimeoutSeconds,
                        parentCancellation,
                        ct);
                }
            }
            catch
            {
                _gate.Release();
                throw;
            }

            return new ActivityResult
            {
                Success = true,
                Output = $"Fire-and-forget invoked '{childWorkflow.Name}' (id={childWorkflow.Id})",
                OutputParameters = new Dictionary<string, string>
                {
                    ["workflowId"] = childWorkflow.Id.ToString(),
                    ["workflowName"] = childWorkflow.Name,
                    ["waited"] = "false",
                },
                Duration = sw.Elapsed,
            };
        }

        // Same back-pressure for the synchronous path, but release the parent step slot
        // while waiting for sub-workflow capacity and child completion.
        async Task<ActivityResult> ExecuteSynchronousChildAsync()
        {
            var gateAcquired = false;
            try
            {
                await _gate.WaitAsync(linkedCts.Token);
                gateAcquired = true;

            // Use the execution-level CTS (not the step-level `ct`) so child lifetime is
            // decoupled from step cancellation. When a waitAny junction fires, it cancels
            // the losing branch's step-level CTS — without this decoupling that signal
            // propagates into the child engine and marks the child as Cancelled even though
            // the parent execution continues normally.
            WorkflowEngine.TryGetExecutionCancellation(context.WorkflowExecutionId, out var execCancellation);
            using var childExecCts = CancellationTokenSource.CreateLinkedTokenSource(execCancellation, timeoutCts.Token);

            // Run child in a FRESH DI scope so it has its own DbContext - avoids
            // EF-Core threading races against the parent's _db.
            await using var scope = _scopeFactory.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
            var childExec = await engine.ExecuteAsync(
                childWorkflow,
                $"startWorkflow:{context.StepId}",
                childExecCts.Token,
                childParams,
                parentExecutionId: context.WorkflowExecutionId,
                callDepth: childCallDepth);
            span?.SetTag(NodePilot.Core.Telemetry.TelemetryConstants.Attributes.SubWorkflowChildExecutionId, childExec.Id.ToString());

            // Re-fetch via a fresh lookup so we get the final ReturnData without relying on the engine's
            // tracking state (it used its own DbContext inside the scope above).
            var childRow = await scope.ServiceProvider.GetRequiredService<NodePilotDbContext>()
                .WorkflowExecutions
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == childExec.Id, CancellationToken.None);

            var returnDataJson = childRow?.ReturnData;
            var returned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(returnDataJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(returnDataJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            returned[prop.Name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                                _ => prop.Value.GetRawText(),
                            };
                        }
                    }
                }
                catch
                {
                    // ReturnData was non-JSON - ignore, leave `returned` empty.
                }
            }

            var childSucceeded = childExec.Status == ExecutionStatus.Succeeded;

            // Always expose metadata params alongside returned data
            returned["__executionId"] = childExec.Id.ToString();
            returned["__status"] = childExec.Status.ToString();
            returned["__workflowId"] = childWorkflow.Id.ToString();
            returned["__workflowName"] = childWorkflow.Name;

            return new ActivityResult
            {
                Success = childSucceeded,
                Output = returnDataJson ?? $"Child execution {childExec.Id} completed with status {childExec.Status}",
                ErrorOutput = childSucceeded ? null : (childExec.ErrorMessage ?? "child workflow did not succeed"),
                OutputParameters = returned,
                Duration = sw.Elapsed,
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: child '{childWorkflow.Name}' timed out after {timeoutSeconds}s",
                Duration = sw.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: {ex.GetType().Name}: {ex.Message}",
                Duration = sw.Elapsed,
            };
        }
        finally
        {
            if (gateAcquired) _gate.Release();
        }
        }

        return await WorkflowScheduler.RunWithCurrentStepGateReleasedAsync(
            ExecuteSynchronousChildAsync,
            ct);
    }

    private async Task ExecuteDetachedChildAsync(
        Workflow childWorkflow,
        Guid parentExecutionId,
        string parentStepId,
        Dictionary<string, string> childParams,
        int childCallDepth,
        int? timeoutSeconds,
        CancellationToken parentCancellation,
        CancellationToken dispatchCancellation)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                parentCancellation,
                dispatchCancellation);
            // Honor a node-set timeout on the detached path too (previously ignored) so an
            // author who wants a wall-clock ceiling actually gets one; null = no ceiling, since
            // a fire-and-forget child blocks nothing and long-running work is legitimate. The
            // run finishes regardless via the step-always-resolves + guaranteed-finalization
            // contract — the timeout is only a backstop for a genuinely wedged step.
            await engine.ExecuteAsync(
                childWorkflow,
                $"startWorkflow:{parentStepId}",
                linkedCts.Token,
                childParams,
                timeoutSeconds: timeoutSeconds,
                parentExecutionId: parentExecutionId,
                callDepth: childCallDepth);
        }
        catch (Exception ex)
        {
            // The child finalizes its own terminal state (Failed/Cancelled) inside the engine.
            // Reaching here means engine.ExecuteAsync threw OUTSIDE that guaranteed finalization
            // (e.g. scope/host teardown). Surface it — never swallow silently — so a genuine
            // engine fault is diagnosable instead of a mystery Running row.
            _logger?.LogError(ex,
                "Detached sub-workflow '{ChildName}' ({ChildId}) from parent execution {ParentExecutionId} threw outside finalization",
                childWorkflow.Name, childWorkflow.Id, parentExecutionId);
        }
        finally
        {
            _gate.Release();
        }
    }
}

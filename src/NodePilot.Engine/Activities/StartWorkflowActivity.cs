using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Enums;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Execution;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Invokes another workflow by id or name from inside the current workflow. By default the
/// parent step waits for the child and exposes the child's <c>returnData</c> as this step's
/// OutputParameters, so downstream nodes can read it via <c>{{stepId.param.key}}</c>.
///
/// Config: <c>workflowNameOrId</c> (required, GUID or unique workflow name),
/// <c>parameters</c> (forwarded to the child as manualTrigger inputs), <c>timeoutSeconds</c>
/// (default 3600) and <c>waitForCompletion</c> (default true; when false the step is
/// fire-and-forget and returns only the child executionId). Self-invocation is rejected, and
/// call depth is tracked through the reserved <c>__callDepth</c> input parameter and capped by
/// MAX_CALL_DEPTH to stop runaway recursion.
/// </summary>
public class StartWorkflowActivity : IActivityExecutor
{
    // Timeout applied to a synchronous child when the node sets none, so a stuck child
    // cannot pin the parent step indefinitely.
    internal const int DefaultChildTimeoutSeconds = 3600;

    private static readonly System.Diagnostics.Metrics.Counter<long> _subWorkflowInvocations =
        EngineMetrics.Meter.CreateCounter<long>(
            "nodepilot.subworkflow.invocations", unit: "1",
            description: "Sub-workflow invocations via startWorkflow, tagged by wait_mode and depth_bucket.");
    private static readonly System.Diagnostics.Metrics.Counter<long> _subWorkflowDepthExceeded =
        EngineMetrics.Meter.CreateCounter<long>(
            "nodepilot.subworkflow.depth_exceeded", unit: "1",
            description: "startWorkflow attempts that hit the MaxCallDepth limit.");

    // ISubWorkflowGate owns the engine-wide sub-workflow concurrency cap, so ForEachActivity
    // draws from the same back-pressure pool.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NodePilotDbContext _db;
    private readonly ISubWorkflowGate _gate;
    private readonly IWorkflowExecutionDispatcher? _executionDispatcher;
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
        IWorkflowExecutionDispatcher? executionDispatcher)
        : this(scopeFactory, db, gate, executionDispatcher, null)
    {
    }

    public StartWorkflowActivity(
        IServiceScopeFactory scopeFactory,
        NodePilotDbContext db,
        ISubWorkflowGate gate,
        IWorkflowExecutionDispatcher? executionDispatcher,
        ISubWorkflowAuthorizationResolver? subWorkflowAuthz,
        ILogger<StartWorkflowActivity>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _db = db;
        _gate = gate;
        _executionDispatcher = executionDispatcher;
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

        // Raw node value, null when the author set none. The synchronous path falls back to a
        // default so a slow child cannot pin the parent step forever. The fire-and-forget path
        // has no default: a detached child blocks nothing and finishes through the guaranteed
        // finalization contract, so a wall-clock ceiling there is opt-in per node.
        var explicitTimeoutSeconds = config.GetOptionalPositiveInt("timeoutSeconds");
        var timeoutSeconds = explicitTimeoutSeconds ?? DefaultChildTimeoutSeconds;
        var waitForCompletion = config.GetBool("waitForCompletion", true);

        // Locate the child workflow: GUID first, then by name (exact case wins, then
        // case-insensitive).
        var (outcome, resolvedWorkflow) = await SubWorkflowInvocation.ResolveChildWorkflowAsync(_db, workflowNameOrId, ct);
        if (outcome == SubWorkflowInvocation.ChildOutcome.Ambiguous)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: multiple workflows named '{workflowNameOrId}' — disambiguate with the GUID",
                Duration = sw.Elapsed,
            };
        }
        if (outcome == SubWorkflowInvocation.ChildOutcome.NotFound)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: workflow '{workflowNameOrId}' not found",
                Duration = sw.Elapsed,
            };
        }
        var childWorkflow = resolvedWorkflow!;
        if (outcome == SubWorkflowInvocation.ChildOutcome.Disabled)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: workflow '{childWorkflow.Name}' is disabled",
                Duration = sw.Elapsed,
            };
        }

        // Self-call guard: it needs the parent workflow id, which is not on the step context,
        // so it comes from the current execution row.
        var parentExec = await SubWorkflowInvocation.LoadParentExecutionAsync(_db, context.WorkflowExecutionId, ct);
        if (SubWorkflowInvocation.IsSelfInvocation(parentExec, childWorkflow))
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = "startWorkflow: self-invocation is not allowed (direct recursion)",
                Duration = sw.Elapsed,
            };
        }

        // Runtime RBAC check on top of the publish-time check in PrePublishChecklist: folder
        // permissions can be revoked between publish and run, and a trigger-driven run may
        // execute under a different principal than the publishing user. The effective principal
        // is parentExec.StartedByUserId for a manual run, otherwise the parent workflow's
        // LastModifiedByUserId. Without a principal the cross-folder call is refused.
        var blocked = await SubWorkflowInvocation.GetAuthorizationBlockAsync(
            _subWorkflowAuthz, parentExec, childWorkflow, ct);
        if (blocked is not null)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: {blocked}",
                Duration = sw.Elapsed,
            };
        }

        // Call-depth guard: read from the reserved variable the engine places into
        // context.Variables ("manual.__callDepth" when passed via inputParameters).
        var currentDepth = SubWorkflowInvocation.CurrentCallDepth(context);
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

        // Collect child input parameters. The dictionary is OrdinalIgnoreCase, matching
        // PowerShell and the template resolver, so "Foo" and "foo" collide. Keys starting with
        // "__" are reserved for engine bookkeeping such as the call-depth counter and are
        // rejected here case-insensitively, so a user parameter cannot reset that counter.
        var childParams = SubWorkflowInvocation.CollectParameters(config, out var reservedKey);
        if (reservedKey is not null)
        {
            return new ActivityResult
            {
                Success = false,
                ErrorOutput = $"startWorkflow: parameter name '{reservedKey}' is reserved (keys starting with '__' are used by the engine). Rename the parameter.",
                Duration = sw.Elapsed,
            };
        }
        // Seed the reserved depth counter after the user parameters so the engine's value wins
        // even if the rejection above were bypassed.
        childParams[WorkflowRecursion.CallDepthKey] = childCallDepth.ToString();

        using var timeoutCts = new CancellationTokenSource();
        // timeoutSeconds is never null here, so CancelAfter is always armed.
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (!waitForCompletion)
        {
            if (_executionDispatcher is null)
            {
                return new ActivityResult
                {
                    Success = false,
                    ErrorOutput = "startWorkflow: durable execution dispatcher is unavailable",
                    Duration = sw.Elapsed,
                };
            }

            var childExecution = await _executionDispatcher.DispatchAsync(
                new WorkflowDispatchIntent(
                    childWorkflow.Id,
                    $"startWorkflow:{context.StepId}",
                    childParams,
                    explicitTimeoutSeconds,
                    StartedByUserId: parentExec?.StartedByUserId,
                    RequireWorkflowEnabled: true,
                    MissingWorkflowMessage: "Queued sub-workflow was not dispatched because it no longer exists or is disabled.",
                    PreOwnershipFailurePrefix: "Queued sub-workflow failed before the engine could take ownership",
                    RequireMaintenanceWindowCheck: false,
                    ParentExecutionId: context.WorkflowExecutionId,
                    CallDepth: childCallDepth),
                ct);

            return new ActivityResult
            {
                Success = true,
                Output = $"Fire-and-forget invoked '{childWorkflow.Name}' (id={childWorkflow.Id})",
                OutputParameters = new Dictionary<string, string>
                {
                    ["workflowId"] = childWorkflow.Id.ToString(),
                    ["workflowName"] = childWorkflow.Name,
                    ["executionId"] = childExecution.Id.ToString(),
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

            // Use the execution-level CTS instead of the step-level `ct` so the child's lifetime
            // is decoupled from step cancellation. A waitAny junction cancels the losing branch's
            // step-level CTS, and that signal must not mark the child as Cancelled while the
            // parent execution continues.
            WorkflowEngine.TryGetExecutionCancellation(context.WorkflowExecutionId, out var execCancellation);
            using var childExecCts = CancellationTokenSource.CreateLinkedTokenSource(execCancellation, timeoutCts.Token);

            // Run the child in a fresh DI scope so it gets its own DbContext and cannot race
            // the parent's _db on EF Core.
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

            // Re-read the row so the final ReturnData comes from the database instead of the
            // engine's tracking state, which lives in its own DbContext.
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
                            returned[prop.Name] = PowerShellOperation.JsonElementToScalarString(prop.Value);
                        }
                    }
                }
                catch
                {
                    // ReturnData was not JSON; leave `returned` empty.
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

}

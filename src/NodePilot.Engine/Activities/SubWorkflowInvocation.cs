using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Guards that <see cref="StartWorkflowActivity"/> and <see cref="ForEachActivity"/> run before
/// handing work to the engine: child resolution, self-invocation, runtime RBAC, call depth and
/// user-parameter ingest. Only the mechanics live here. Each activity formats its own error
/// strings, because their wording is part of the step's observable output, and keeps its own
/// instrumentation.
/// </summary>
internal static class SubWorkflowInvocation
{
    internal enum ChildOutcome
    {
        Found,
        Ambiguous,
        NotFound,
        Disabled,
    }

    /// <summary>
    /// Locates the child workflow: GUID first, then by name (exact case wins, then
    /// case-insensitive). Ambiguous, missing and disabled workflows come back as outcomes so
    /// the caller can word its own error message.
    /// </summary>
    public static async Task<(ChildOutcome Outcome, Workflow? Workflow)> ResolveChildWorkflowAsync(
        NodePilotDbContext db,
        string nameOrId,
        CancellationToken ct)
    {
        Workflow? workflow;
        if (Guid.TryParse(nameOrId, out var id))
        {
            workflow = await db.Workflows.FirstOrDefaultAsync(wf => wf.Id == id, ct);
        }
        else
        {
            var resolved = await WorkflowNameResolver.ResolveByNameAsync(db.Workflows, nameOrId, ct);
            if (resolved.Outcome == WorkflowNameResolver.Outcome.Ambiguous)
                return (ChildOutcome.Ambiguous, null);
            workflow = resolved.Workflow;
        }

        if (workflow is null) return (ChildOutcome.NotFound, null);
        if (!workflow.IsEnabled) return (ChildOutcome.Disabled, workflow);
        return (ChildOutcome.Found, workflow);
    }

    /// <summary>
    /// Loads the parent execution row behind the current step. The self-invocation guard and the
    /// RBAC re-check need the parent workflow id, which is not on the step context.
    /// </summary>
    public static Task<WorkflowExecution?> LoadParentExecutionAsync(
        NodePilotDbContext db,
        Guid workflowExecutionId,
        CancellationToken ct)
        => db.WorkflowExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == workflowExecutionId, ct);

    /// <summary>True when the step starts the workflow it runs in (direct recursion).</summary>
    public static bool IsSelfInvocation(WorkflowExecution? parentExec, Workflow childWorkflow)
        => parentExec is not null && parentExec.WorkflowId == childWorkflow.Id;

    /// <summary>
    /// Runtime RBAC re-check, because folder permissions can be revoked between publish and run.
    /// Returns the block reason without an activity prefix, or null when the call is allowed.
    /// </summary>
    public static async Task<string?> GetAuthorizationBlockAsync(
        ISubWorkflowAuthorizationResolver? subWorkflowAuthz,
        WorkflowExecution? parentExec,
        Workflow childWorkflow,
        CancellationToken ct)
    {
        if (subWorkflowAuthz is null || parentExec is null) return null;
        return await subWorkflowAuthz.IsBlockedAsync(parentExec, childWorkflow, ct);
    }

    /// <summary>
    /// Current call depth, read from the reserved variable the engine places into
    /// <c>context.Variables</c> ("manual.__callDepth" when passed via inputParameters). A missing
    /// or unparsable value counts as depth 0.
    /// </summary>
    public static int CurrentCallDepth(StepExecutionContext context)
        => context.Variables.TryGetValue($"manual.{WorkflowRecursion.CallDepthKey}", out var depthStr)
           && int.TryParse(depthStr, out var parsed)
            ? parsed
            : 0;

    /// <summary>
    /// Reads the optional <c>parameters</c> object into a case-insensitive dictionary, the same
    /// comparer the template resolver and PowerShell use, so <c>Foo</c> and <c>foo</c> collide.
    /// Stops at the first "__"-prefixed key and reports it via <paramref name="reservedKey"/>:
    /// that namespace belongs to engine bookkeeping such as <c>__callDepth</c>, and user control
    /// over it would bypass the recursion guard.
    /// </summary>
    public static Dictionary<string, string> CollectParameters(JsonElement config, out string? reservedKey)
    {
        reservedKey = null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!config.TryGetProperty("parameters", out var paramsEl) || paramsEl.ValueKind != JsonValueKind.Object)
            return parameters;

        foreach (var prop in paramsEl.EnumerateObject())
        {
            if (WorkflowRecursion.IsReservedParameterName(prop.Name))
            {
                reservedKey = prop.Name;
                return parameters;
            }
            parameters[prop.Name] = PowerShellOperation.JsonElementToScalarString(prop.Value);
        }
        return parameters;
    }
}

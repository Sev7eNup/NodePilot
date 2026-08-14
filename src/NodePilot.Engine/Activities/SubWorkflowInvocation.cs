using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Activities;

/// <summary>
/// The guards every child-workflow-spawning activity runs before it hands work to the engine —
/// shared by <see cref="StartWorkflowActivity"/> and <see cref="ForEachActivity"/>: child
/// resolution, self-invocation, runtime RBAC, call depth and the user-parameter ingest.
///
/// Only the mechanics live here. Each activity formats its OWN error strings (their wording is
/// part of the step's observable output) and keeps its own instrumentation — startWorkflow, for
/// instance, counts and tags a depth violation before returning.
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
    /// Locates the child workflow: GUID first, then by name (exact-case wins, then
    /// case-insensitive). Ambiguous names, a missing workflow and a disabled workflow are
    /// reported as outcomes — the caller turns them into its own error message.
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
    /// The parent execution row behind the current step. Needed for the self-invocation guard and
    /// the RBAC re-check; the parent workflow id is not on the step context directly.
    /// </summary>
    public static Task<WorkflowExecution?> LoadParentExecutionAsync(
        NodePilotDbContext db,
        Guid workflowExecutionId,
        CancellationToken ct)
        => db.WorkflowExecutions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == workflowExecutionId, ct);

    /// <summary>True when the step would start the workflow it is running in (direct recursion).</summary>
    public static bool IsSelfInvocation(WorkflowExecution? parentExec, Workflow childWorkflow)
        => parentExec is not null && parentExec.WorkflowId == childWorkflow.Id;

    /// <summary>
    /// Runtime RBAC re-check (Defense-in-Depth — folder permissions can be revoked between Publish
    /// and Run). Returns the block reason WITHOUT an activity prefix, or null when allowed.
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
    /// <c>context.Variables</c> ("manual.__callDepth" when passed via inputParameters). Missing or
    /// unparsable means depth 0.
    /// </summary>
    public static int CurrentCallDepth(StepExecutionContext context)
        => context.Variables.TryGetValue($"manual.{WorkflowRecursion.CallDepthKey}", out var depthStr)
           && int.TryParse(depthStr, out var parsed)
            ? parsed
            : 0;

    /// <summary>
    /// Reads the optional <c>parameters</c> object into a case-insensitive dictionary — the same
    /// comparer the template resolver and PowerShell use, so <c>Foo</c> and <c>foo</c> collide.
    /// Stops at the first "__"-prefixed key and reports it via <paramref name="reservedKey"/>:
    /// that namespace belongs to engine bookkeeping (see <c>__callDepth</c>) and letting a user
    /// steer it would bypass the recursion guard.
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

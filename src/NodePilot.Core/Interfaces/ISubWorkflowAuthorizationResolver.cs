using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Engine-side authorization gate for <c>startWorkflow</c> sub-workflow calls. Separate from
/// <see cref="IResourceAuthorizationService"/> because the engine has no ClaimsPrincipal at
/// runtime: it starts from the parent execution row and resolves the effective principal (the
/// user who started the run, or the publishing user for trigger-driven runs) itself before
/// checking folder permissions.
/// <para>
/// Returns <c>null</c> when the call is allowed, otherwise a single-line error message.
/// Implementations live in NodePilot.Api to reuse the folder-permission logic; the interface
/// lives in Core so NodePilot.Engine can inject it without a project reference.
/// </para>
/// </summary>
public interface ISubWorkflowAuthorizationResolver
{
    Task<string?> IsBlockedAsync(WorkflowExecution parentExecution, Workflow childWorkflow, CancellationToken ct);
}

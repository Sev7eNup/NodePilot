using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

/// <summary>
/// Engine-side authorization gate for <c>startWorkflow</c> sub-workflow calls. Lives
/// outside <see cref="IResourceAuthorizationService"/> because the engine has no
/// ClaimsPrincipal at runtime — it has the parent execution row and must resolve the
/// effective principal (manual-run starter or trigger-driven publishing user) internally
/// before checking folder permissions.
/// <para>
/// Returns <c>null</c> when the call is allowed; returns a single-line human-readable
/// error message when blocked. Implementations live in NodePilot.Api so they can reuse
/// the folder-permission logic; the interface lives in Core so NodePilot.Engine can
/// inject it without cross-project references.
/// </para>
/// </summary>
public interface ISubWorkflowAuthorizationResolver
{
    Task<string?> IsBlockedAsync(WorkflowExecution parentExecution, Workflow childWorkflow, CancellationToken ct);
}

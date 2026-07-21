namespace NodePilot.Core.Audit;

/// <summary>
/// Actor attribution for audit entries — used by <see cref="IAuditStager"/> callers that
/// build entries outside an HTTP request (background services, scheduler, engine-side
/// code). The HTTP-flow <see cref="IAuditWriter"/> resolves the actor from the current
/// principal + remote IP automatically; non-HTTP callers supply their own.
/// </summary>
/// <param name="UserId">Authenticated user id, or <c>null</c> when the action has no
/// user attribution (scheduler-fired trigger, system bootstrap, background retention).</param>
/// <param name="Username">Username at the time of the event. Frozen-at-write so the row
/// remains interpretable after the user is renamed or deleted.</param>
/// <param name="IpAddress">Source IP if the action originated from an HTTP request.
/// <c>null</c> for background work.</param>
public sealed record AuditActor(Guid? UserId, string? Username, string? IpAddress)
{
    /// <summary>
    /// No user, no IP — e.g. a scheduler-fired trigger or a system migration. Use this
    /// rather than <c>new AuditActor(null, null, null)</c> so the intent reads clearly.
    /// </summary>
    public static readonly AuditActor System = new(null, null, null);
}

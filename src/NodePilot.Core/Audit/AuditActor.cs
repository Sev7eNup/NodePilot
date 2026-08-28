namespace NodePilot.Core.Audit;

/// <summary>
/// Actor attribution for audit entries. Used by <see cref="IAuditStager"/> callers that build
/// entries outside an HTTP request, such as background services, the scheduler and engine-side
/// code. The HTTP-flow <see cref="IAuditWriter"/> resolves the actor from the current principal
/// and remote IP; non-HTTP callers supply their own.
/// </summary>
/// <param name="UserId">Authenticated user id, or <c>null</c> when the action has no
/// user attribution (scheduler-fired trigger, system bootstrap, background retention).</param>
/// <param name="Username">Username recorded at write time so the row stays interpretable
/// after the user is renamed or deleted.</param>
/// <param name="IpAddress">Source IP if the action originated from an HTTP request.
/// <c>null</c> for background work.</param>
public sealed record AuditActor(Guid? UserId, string? Username, string? IpAddress)
{
    /// <summary>
    /// No user and no IP, for example a scheduler-fired trigger or a system migration.
    /// Preferred over <c>new AuditActor(null, null, null)</c> because it names the intent.
    /// </summary>
    public static readonly AuditActor System = new(null, null, null);
}

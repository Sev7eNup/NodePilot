using NodePilot.Core.Audit;
using NodePilot.Core.Models;

namespace NodePilot.Api.Tests.TestSupport;

/// <summary>
/// <see cref="IAuditStager"/> that throws on every <c>Build</c> — used by the identity-provider
/// tests (LDAP/OIDC/SCIM) to prove that an audit-staging failure never corrupts the actual user
/// mutation. By default it throws <see cref="InvalidOperationException"/>; pass a factory when a
/// test needs to observe its own marker exception type bubbling out.
/// </summary>
public sealed class ThrowingAuditStager(Func<Exception>? exceptionFactory = null) : IAuditStager
{
    public AuditLogEntry Build(
        string action,
        AuditActor actor,
        string? resourceType = null,
        Guid? resourceId = null,
        string? details = null) =>
        throw (exceptionFactory?.Invoke() ?? new InvalidOperationException("audit staging failed"));
}

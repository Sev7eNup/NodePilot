using NodePilot.Api.Audit;
using NodePilot.Core.Audit;

namespace NodePilot.Api.Tests;

// Discard-all audit writer for tests that don't assert on audit side-effects. Keeps the
// test arrange blocks short — ctor calls don't need to construct a real writer + its
// HttpContextAccessor + its DbContext dependency. Use `NoopAuditWriter.Instance` rather
// than `new NoopAuditWriter()` — the type is stateless, one instance is enough.
public sealed class NoopAuditWriter : IAuditWriter
{
    public static readonly NoopAuditWriter Instance = new();

    public Task LogAsync(string action, string? resourceType = null, Guid? resourceId = null,
        string? details = null, CancellationToken ct = default) => Task.CompletedTask;
}

// In-memory audit writer for controller tests — captures calls so individual tests can
// assert on them, but doesn't require an HttpContextAccessor or a second DbContext.
public sealed class CapturingAuditWriter : IAuditWriter
{
    public List<(string Action, string? ResourceType, Guid? ResourceId, string? Details)> Calls { get; } = new();

    public Task LogAsync(string action, string? resourceType = null, Guid? resourceId = null,
        string? details = null, CancellationToken ct = default)
    {
        Calls.Add((action, resourceType, resourceId, details));
        return Task.CompletedTask;
    }
}

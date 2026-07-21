using NodePilot.Core.Audit;

namespace NodePilot.TestCommons;

/// <summary>
/// Deterministic <see cref="IAuditDetailsRedactor"/> stub: masks exactly the sentinel value
/// <c>hunter2</c> to <c>***</c>. Tests seed that sentinel and assert on the masking, without
/// depending on the real regex patterns used by the Engine's <c>OutputRedactor</c>.
/// </summary>
public sealed class StubAuditDetailsRedactor : IAuditDetailsRedactor
{
    public string? Redact(string? input) => input?.Replace("hunter2", "***");
}

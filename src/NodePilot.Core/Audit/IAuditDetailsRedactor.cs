namespace NodePilot.Core.Audit;

/// <summary>
/// Single-method abstraction over the regex-based secret-scrubber that lives in
/// <c>NodePilot.Engine.Security.OutputRedactor</c>. Defined in Core so the audit
/// stager (which lives in Core to be consumed by Data + Scheduler + Api alike) can
/// apply redaction without pulling Engine into Core's dependency graph. The Engine
/// implementation registers itself against this interface in DI.
/// </summary>
public interface IAuditDetailsRedactor
{
    /// <summary>
    /// Returns <paramref name="input"/> with known secret shapes (key=value pairs,
    /// JSON properties, PEM bodies, AWS/GitHub/Stripe/Slack/GitLab token shapes) replaced
    /// by <c>***</c>. Null/empty input is returned unchanged.
    /// </summary>
    string? Redact(string? input);
}

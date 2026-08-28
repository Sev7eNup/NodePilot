namespace NodePilot.Core.Audit;

/// <summary>
/// Single-method abstraction over the regex-based secret scrubber in
/// <c>NodePilot.Engine.Security.OutputRedactor</c>. Defined in Core so the audit stager, which
/// Data, Scheduler and Api all consume, can redact without pulling Engine into Core's
/// dependency graph. The Engine implementation registers itself against this interface in DI.
/// </summary>
public interface IAuditDetailsRedactor
{
    /// <summary>
    /// Returns <paramref name="input"/> with known secret shapes replaced by <c>***</c>:
    /// key=value pairs, JSON properties, PEM bodies and AWS/GitHub/Stripe/Slack/GitLab
    /// tokens. Null or empty input is returned unchanged.
    /// </summary>
    string? Redact(string? input);
}

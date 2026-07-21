using NodePilot.Core.Enums;

namespace NodePilot.Core.Models;

public class StepExecution
{
    public Guid Id { get; set; }
    public Guid WorkflowExecutionId { get; set; }
    public string StepId { get; set; } = string.Empty;
    public string? StepName { get; set; }
    public string StepType { get; set; } = string.Empty;
    public string? TargetMachine { get; set; }
    public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Output { get; set; }
    public string? ErrorOutput { get; set; }

    /// <summary>
    /// Number of attempts the activity actually ran before reaching its final status.
    /// 1 = normal single-shot execution; values &gt; 1 indicate retry-policy engagement.
    /// Surfaced so incident review can tell "flaky — retried 3× and eventually succeeded"
    /// from "first-shot success" without diffing the log.
    /// </summary>
    public int AttemptCount { get; set; } = 1;

    /// <summary>
    /// Timestamp at which the step paused at a breakpoint. Non-null means Status==Paused was
    /// active at some point (the value is cleared back to null on resume, so finished/terminal
    /// rows don't carry leftover debug metadata). Only relevant for active debug sessions.
    /// </summary>
    public DateTime? PausedAt { get; set; }

    /// <summary>
    /// JSON snapshot of the resolved variables dictionary at the moment the step paused
    /// (globals.*, manual.*, step.param.*, step.output, etc.). Only set while the step is
    /// paused; cleared back to null on resume so finished rows don't carry a large JSON blob.
    /// Compliance note: any secret variables in here are run through OutputRedactor before
    /// being persisted, so the debugger inspector always shows redacted values.
    /// </summary>
    public string? VariablesSnapshot { get; set; }

    /// <summary>
    /// Verbose execution log captured during the step (PowerShell Start-Transcript output for
    /// RunScript with <c>config.transcript: true</c>). Null when tracing was disabled or the
    /// activity does not produce a transcript. Truncated to <c>Logging:StepDetail:MaxOutputChars</c>
    /// like the regular Output, and run through OutputRedactor before persist so secrets in
    /// command echoes / Write-Host calls get masked.
    /// </summary>
    public string? TraceOutput { get; set; }

    /// <summary>
    /// JSON map of the OutputParameters (key → string). Persisted after <c>OutputRedactor.Redact</c>,
    /// meaning secret patterns are already masked. Lets Step-Test replay <c>{{step.param.x}}</c>
    /// with real run context, and backs the coverage aggregations that answer "was this output
    /// ever set?". Null when the step produced no parameters (most commonly <c>delay</c>/<c>log</c>)
    /// — an empty dict is also persisted as null to save storage.
    /// </summary>
    public string? OutputParametersJson { get; set; }

    /// <summary>
    /// Reproducibility snapshot for custom-activity steps: the definition key, the version that ran,
    /// and a hash of the script template + normalized options. Non-null only when
    /// <see cref="StepType"/> is a <c>custom:&lt;key&gt;</c> type. Because latest-wins lets the live
    /// definition change (and rollback rewinds it), these fields are the only record of which script
    /// a past run actually executed. Set by <c>CustomActivityExecutor</c> via
    /// <c>ActivityResult.CustomActivity</c>.
    /// </summary>
    public string? CustomActivityKey { get; set; }
    public int? CustomActivityVersion { get; set; }
    public string? CustomActivityHash { get; set; }

    public WorkflowExecution WorkflowExecution { get; set; } = null!;
}

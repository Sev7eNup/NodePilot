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
    /// Number of attempts the activity made before reaching its final status.
    /// 1 means no retry; a higher value shows the retry policy engaged.
    /// </summary>
    public int AttemptCount { get; set; } = 1;

    /// <summary>
    /// Timestamp when the step paused at a breakpoint. Set only during an active debug
    /// pause; cleared back to null on resume so finished rows carry no debug metadata.
    /// </summary>
    public DateTime? PausedAt { get; set; }

    /// <summary>
    /// JSON snapshot of resolved variables (globals.*, manual.*, step.param.*, step.output, etc.)
    /// at the moment the step paused. Cleared back to null on resume. Secret values are run
    /// through OutputRedactor before being persisted.
    /// </summary>
    public string? VariablesSnapshot { get; set; }

    /// <summary>
    /// Verbose execution log captured during the step (PowerShell Start-Transcript output for
    /// RunScript with <c>config.transcript: true</c>). Null when tracing is disabled or the
    /// activity produces no transcript. Truncated like Output and redacted before persist.
    /// </summary>
    public string? TraceOutput { get; set; }

    /// <summary>
    /// JSON map of OutputParameters (key to string), redacted via <c>OutputRedactor.Redact</c>
    /// before persist. Lets Step-Test replay <c>{{step.param.x}}</c> with real run context.
    /// Null when the step produced no parameters.
    /// </summary>
    public string? OutputParametersJson { get; set; }

    /// <summary>
    /// Reproducibility snapshot for custom-activity steps: definition key, version that ran,
    /// and a hash of the script template plus normalized options. Non-null only when
    /// <see cref="StepType"/> is a <c>custom:&lt;key&gt;</c> type, since the live definition
    /// can change or roll back after the run.
    /// </summary>
    public string? CustomActivityKey { get; set; }
    public int? CustomActivityVersion { get; set; }
    public string? CustomActivityHash { get; set; }

    public WorkflowExecution WorkflowExecution { get; set; } = null!;
}

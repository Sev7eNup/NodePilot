using System.Text.Json;
using NodePilot.Core.Models;

namespace NodePilot.Core.Interfaces;

public class ActivityResult
{
    public bool Success { get; set; }
    public string? Output { get; set; }
    public string? ErrorOutput { get; set; }
    public TimeSpan Duration { get; set; }
    /// <summary>
    /// Named output parameters for fine-grained access via {{varName.param.paramName}}.
    /// Used by ManualTrigger to expose individual input parameters to downstream steps.
    /// </summary>
    public Dictionary<string, string> OutputParameters { get; set; } = [];

    /// <summary>
    /// Optional verbose execution log (PowerShell <c>Start-Transcript</c> capture, future
    /// equivalents) emitted alongside <see cref="Output"/>. Populated by activities that
    /// opt into per-step tracing — RunScript with <c>config.transcript: true</c> currently.
    /// Persisted to <c>StepExecution.TraceOutput</c> and surfaced in the UI as a separate
    /// "Transcript" tab so the regular Output stays clean for variable resolution.
    /// </summary>
    public string? TraceOutput { get; set; }

    /// <summary>
    /// Set by <c>CustomActivityExecutor</c> only — identifies the exact custom-activity definition
    /// version that ran, so the step stays reproducible after later edits/rollbacks. Persisted onto
    /// <c>StepExecution</c>. NB: <c>OutputRedactor.Redact</c> rebuilds the result, so it must copy
    /// this field through.
    /// </summary>
    public CustomActivityProvenance? CustomActivity { get; set; }
}

public class StepExecutionContext
{
    public Guid WorkflowExecutionId { get; set; }
    public string StepId { get; set; } = string.Empty;
    /// <summary>
    /// Human-readable step label from <c>WorkflowNode.Data.Label</c>, falling back to
    /// <see cref="StepId"/> when no label is set. Activities that emit user-visible log
    /// lines (support log) use this instead of the opaque GUID step id.
    /// </summary>
    public string StepLabel { get; set; } = string.Empty;
    /// <summary>
    /// Workflow name from <c>Workflow.Name</c>. Passed through by the engine to every
    /// activity so user-facing log lines can be attributed without a DB lookup.
    /// </summary>
    public string WorkflowName { get; set; } = string.Empty;
    public Guid? TargetMachineId { get; set; }
    public Guid? CredentialId { get; set; }
    public Dictionary<string, string> Variables { get; set; } = [];
    /// <summary>
    /// Pre-resolved target machine. May be a registered machine (from DB) or an
    /// ad-hoc machine built from a resolved hostname expression.
    /// </summary>
    public ManagedMachine? ResolvedMachine { get; set; }

    /// <summary>
    /// Completed upstream step results. Populated by the engine for activities that
    /// need to evaluate condition expressions against prior step outputs (decision /
    /// switch). Null for the StepTester / ad-hoc invocation paths — activities that
    /// rely on this must guard for null.
    /// </summary>
    public IReadOnlyDictionary<string, ActivityResult>? PreviousResults { get; set; }

    /// <summary>
    /// Maps the user-facing <c>OutputVariable</c> alias of a node to its raw step id, so
    /// condition expressions can reference either form (mirrors the map built for edge
    /// condition evaluation). Null when no aliases are in use.
    /// </summary>
    public IReadOnlyDictionary<string, string>? OutputVariableToStepId { get; set; }

    /// <summary>
    /// Raw global variables (bare names, no <c>globals.</c> prefix). Populated by the engine so
    /// condition-evaluating activities (decision) can resolve <c>source:"global"</c> operands and
    /// <c>{{globals.X}}</c> literals identically to the edge-condition path. Null on ad-hoc paths.
    /// </summary>
    public IReadOnlyDictionary<string, string>? GlobalVariables { get; set; }

    /// <summary>
    /// Raw trigger/manual input parameters (bare names, no <c>manual.</c> prefix). Populated by the
    /// engine so condition-evaluating activities can resolve <c>source:"manual"</c> operands and
    /// <c>{{manual.X}}</c> literals identically to the edge-condition path. Null on ad-hoc paths.
    /// </summary>
    public IReadOnlyDictionary<string, string>? InputParameters { get; set; }
}

public interface IActivityExecutor
{
    string ActivityType { get; }
    Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct);
}

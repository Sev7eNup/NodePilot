namespace NodePilot.Api.Dtos;

/// <summary>
/// Ad-hoc execute body. <c>TimeoutSeconds</c> caps the whole run (not per-step); when set,
/// the engine cancels the workflow if it hasn't reached a terminal status by then. Default
/// null = no ceiling (the workflow's own step timeouts still apply).
/// </summary>
public record ExecuteWorkflowRequest(
    Dictionary<string, string>? Parameters = null,
    int? TimeoutSeconds = null,
    bool Debug = false);

public record ExecutionResponse(
    Guid Id, Guid WorkflowId, string Status, DateTime StartedAt,
    DateTime? CompletedAt, string? TriggeredBy, string? ErrorMessage,
    string? TraceId = null, string? SpanId = null, string? ReturnData = null,
    string? InputParametersJson = null,
    // Triage columns for the history/list view. Only GET /api/executions (the list endpoint)
    // populates these; single-resource endpoints (Execute/Retry/GetById) leave the defaults
    // (null/0) in place because a freshly created Pending row has no steps or parent info yet.
    // What each field means per row:
    //   StartedByUsername — the username behind StartedByUserId; null for trigger-initiated runs.
    //   ParentExecutionId / ParentWorkflowName — marks this as a sub-workflow run; null for top-level runs.
    //   StepsTotal — count of all StepExecution rows for this run.
    //   StepsCompleted — StepsTotal minus Skipped (a skipped step is a control-flow branch that
    //     never actually ran, so it shouldn't count as "completed").
    //   FailedSteps — every failed step of the run, in the order it started. Multiple parallel
    //     branches can fail at the same time; the grid joins their names with commas.
    string? StartedByUsername = null,
    Guid? ParentExecutionId = null,
    string? ParentWorkflowName = null,
    int StepsTotal = 0,
    int StepsCompleted = 0,
    IReadOnlyList<FailedStepRef>? FailedSteps = null);

/// <summary>
/// A lightweight reference to a failed step, shown in the history grid. <c>StepName</c> can be
/// null (the step has no explicit label set) — renderers then fall back to showing <c>StepId</c>.
/// </summary>
public record FailedStepRef(string StepId, string? StepName);

public record StepExecutionResponse(
    Guid Id, string StepId, string? StepName, string StepType, string? TargetMachine,
    string Status, DateTime? StartedAt, DateTime? CompletedAt,
    string? Output, string? ErrorOutput,
    int AttemptCount,
    DateTime? PausedAt,
    string? VariablesSnapshot,
    string? TraceOutput,
    // OutputParametersJson: JSON-serialized map of OutputParameters captured at terminal time.
    // Already redacted by OutputRedactor; null when the step produced no params.
    string? OutputParametersJson = null,
    // OutputVariable: `data.outputVariable` of the producing node at run time (resolved from
    // the workflow definition at API time, not persisted on the row). Lets the UI rebuild
    // alias-keyed databus entries (`{alias}.output`, `{alias}.param.*`) after a refresh so
    // the live and post-refresh views match the engine's BuildStepVariables dual-lookup.
    string? OutputVariable = null,
    // Custom-activity reproducibility snapshot: which definition key/version/hash actually ran.
    // Non-null only for custom:<key> steps. Lets the UI show "ran v3 of MyNode" even after the
    // live definition was edited (latest-wins) or rolled back.
    string? CustomActivityKey = null,
    int? CustomActivityVersion = null,
    string? CustomActivityHash = null);

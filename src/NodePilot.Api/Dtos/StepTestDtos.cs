using System.Text.Json;

namespace NodePilot.Api.Dtos;

/// <summary>
/// Request body for <c>POST /api/workflows/{id}/steps/{stepId}/test</c>.
///
/// <para><b>MockVariables</b>: flat map of <c>stepName.field</c> -> value (e.g.
/// <c>"checkDisk.output": "7"</c>, <c>"checkDisk.param.freeGb": "7"</c>). Populates
/// upstream <c>{{step.output}}</c> / <c>{{step.param.x}}</c> references.</para>
///
/// <para><b>ConfigOverride</b> replaces only <c>data.config</c>, retaining persisted node metadata.
/// <b>StepOverride</b> replaces the complete <c>node.data</c> object, including activity type,
/// config, target and credential. Omitted target/credential fields clear their saved values.
/// The overrides are mutually exclusive. Either requires Folder Edit permission and the
/// caller's active edit-lock; testing the persisted step requires Run permission.</para>
/// </summary>
public record StepTestRequest(
    Dictionary<string, string>? MockVariables = null,
    JsonElement? ConfigOverride = null,
    JsonElement? StepOverride = null);

/// <summary>
/// Variable name + sample value the UI shows in the mock-editor when the user picks
/// "with last run context". <c>Source</c> is one of <c>output</c>, <c>error</c>, <c>success</c>,
/// or <c>param</c>; <c>Origin</c> is the step ID that produced it. <c>Value</c> is already
/// redacted (it came out of <c>StepExecution</c>, which persists redacted text).
/// </summary>
public record StepTestContextVariable(
    string Key,
    string Origin,
    string Source,
    string? Value);

/// <summary>
/// Per-execution snapshot returned from <c>GET /api/workflows/{id}/steps/{stepId}/test-context</c>.
/// One entry per upstream step that this step transitively depends on, plus one entry for every
/// global variable that's reachable from the workflow.
/// </summary>
public record StepTestContextResponse(
    Guid? ExecutionId,
    DateTime? ExecutedAt,
    string? Status,
    IReadOnlyList<StepTestContextVariable> Variables);

/// <summary>
/// Lightweight execution summary the UI uses to populate the "Pick a run" dropdown.
/// </summary>
public record StepTestContextRunInfo(
    Guid ExecutionId,
    DateTime StartedAt,
    string Status,
    string? TriggeredBy,
    bool StepRan);

public record StepTestResponse(
    bool Success,
    string? Output,
    string? ErrorOutput,
    Dictionary<string, string> OutputParameters,
    double DurationMs,
    string? ErrorMessage);

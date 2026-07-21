namespace NodePilot.Ai;

/// <summary>
/// A single upstream variable available to the step currently being edited. The frontend already
/// has this for display in the editor; the AI endpoint forwards it as-is into the user prompt so
/// the LLM knows which <c>{{var}}</c> references are valid to use in the script. Deliberately
/// carries NO values (privacy + prompt-injection surface).
/// </summary>
/// <param name="StepId">ID of the source step.</param>
/// <param name="Label">Display label ("Collect Info → $hostname").</param>
/// <param name="Variable">Variable path ("collectInfo.param.hostname").</param>
/// <param name="Expression">Fully-qualified template form ("{{collectInfo.param.hostname}}").</param>
/// <param name="Type">Type hint ("string"/"number"/"boolean"/"object"/"array"/"unknown").</param>
public sealed record UpstreamVariableDto(
    string StepId,
    string Label,
    string Variable,
    string Expression,
    string Type);

/// <summary>
/// Request body for <c>POST /api/ai/generate-script</c>. The frontend caps the list to
/// <see cref="LlmOptions.MaxUpstreamVariables"/> in BFS order (direct predecessors first); the
/// server re-trims it for safety as well.
/// <paramref name="CurrentScript"/> is the current editor content — it gives requests like
/// "refactor this script" / "fix the error" something to work from (otherwise the LLM would have
/// to hallucinate from the variable list alone). It is forwarded as-is inside an untrusted context
/// block; since the user is editing their own script, it is not redacted.
/// </summary>
public sealed record GenerateScriptRequest(
    string Prompt,
    Guid? WorkflowId,
    string? StepId,
    IReadOnlyList<UpstreamVariableDto> UpstreamVariables,
    string? CurrentScript);

/// <summary>
/// Request body for <c>POST /api/ai/generate-workflow</c>. Just a free-text prompt — all activity
/// schemas and layout rules live in the server-side system prompt.
/// </summary>
public sealed record GenerateWorkflowRequest(string Prompt);

/// <summary>
/// Response from <c>POST /api/ai/generate-workflow</c>. <c>DefinitionJson</c> has already been
/// validated (top-level <c>nodes[]</c> + <c>edges[]</c>, every <c>activityType</c> value exists in
/// the catalog, every edge source/target references a real node ID) and can be posted directly as
/// <c>CreateWorkflowRequest.DefinitionJson</c>.
/// </summary>
public sealed record GenerateWorkflowResponse(
    string DefinitionJson,
    string SuggestedName,
    string? SuggestedDescription,
    int NodeCount,
    int EdgeCount,
    bool Retried,
    int DurationMs,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null);

/// <summary>
/// A prior chat message in the workflow assistant conversation. <c>Role</c> is <c>"user"</c> or
/// <c>"assistant"</c> (normalized/validated server-side).
/// </summary>
public sealed record AiChatTurnDto(string Role, string Content);

/// <summary>
/// Request body for <c>POST /api/ai/chat</c>. Multi-turn assistant for the workflow currently open
/// in the editor. <c>WorkflowJson</c> is the current canvas definition (already cleaned up by the
/// frontend's <c>stripRuntimeDefinition</c>); it gets secret-redacted server-side <b>before</b> the
/// LLM call. <c>BaseDefinitionHash</c> hashes that same canvas snapshot and is echoed back in the
/// response — the frontend refuses to apply a proposal if the canvas has changed in the meantime
/// (stale-proposal protection).
/// </summary>
public sealed record WorkflowChatRequest(
    string Question,
    string WorkflowJson,
    Guid? WorkflowId,
    string BaseDefinitionHash,
    IReadOnlyList<AiChatTurnDto> History);

/// <summary>
/// A workflow change proposed by the assistant (sent in the SSE <c>proposal</c> event).
/// <c>DefinitionJson</c> has already been merged back onto the original server-side (preserving
/// layout, secrets, and other fields) and structurally validated — the frontend shows it as a diff
/// and can apply it onto the canvas.
/// </summary>
public sealed record WorkflowChatProposalDto(
    string DefinitionJson,
    string Summary,
    int NodeCount,
    int EdgeCount,
    string BaseDefinitionHash);

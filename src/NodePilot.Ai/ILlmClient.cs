using System.Text.Json;

namespace NodePilot.Ai;

/// <summary>
/// Abstraction over a single chat-completion round trip. One method, one one-shot response — no
/// streaming in v1. Errors are thrown as a classified <see cref="LlmException"/> with an
/// <see cref="LlmErrorKind"/>, never as a generic exception.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);

    /// <summary>
    /// Streams a chat completion token by token. Yields <see cref="LlmStreamEvent"/>s: any number
    /// carrying <see cref="LlmStreamEvent.ContentDelta"/>, followed by exactly one with
    /// <see cref="LlmStreamEvent.Done"/>=true (carrying the model name plus optional token usage).
    /// Errors are thrown as a classified <see cref="LlmException"/> (as a normal exception before
    /// the first event; afterwards it propagates through <c>MoveNextAsync</c>).
    /// </summary>
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>
/// An event in a streaming response. Either a text delta (<see cref="ContentDelta"/> set,
/// <see cref="Done"/>=false) or the closing event (<see cref="Done"/>=true, carrying the model
/// name plus optional token counts; <see cref="ContentDelta"/> is null). Token usage is only
/// available for streaming responses when the request set <c>stream_options.include_usage</c> —
/// otherwise those fields stay null.
/// </summary>
public sealed record LlmStreamEvent(
    string? ContentDelta,
    bool Done = false,
    string? Model = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    // Tool-calling (opt-in): the Done event carries FinishReason plus (when it's "tool_calls") the accumulated calls.
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? FinishReason = null);

/// <summary>
/// An OpenAI-compatible tool definition (function calling). Offered to the LLM via
/// <see cref="LlmRequest.Tools"/>; the model itself decides (under <c>tool_choice: "auto"</c>)
/// whether to call one.
/// </summary>
/// <param name="Name">Unique tool name (snake_case, matching the MCP tool naming).</param>
/// <param name="Description">What the tool does and when it's useful — this text drives the model's choice.</param>
/// <param name="Parameters">JSON schema (object) of the parameters; an empty <c>{ "type":"object","properties":{} }</c> when there are none.</param>
public sealed record LlmToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,
    bool Strict = false);

/// <summary>
/// A tool call requested by the model, taken from <c>choices[].message.tool_calls</c>.
/// </summary>
/// <param name="Id">Call ID assigned by the model — must be echoed back as <c>tool_call_id</c> in the tool-result turn.</param>
/// <param name="Name">Function name.</param>
/// <param name="ArgumentsJson">Arguments as a JSON string, exactly as the model provided them (can be empty/`{}`).</param>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// A single chat message in a multi-turn conversation (chat assistant).
/// </summary>
/// <param name="Role">OpenAI role: <c>"user"</c> or <c>"assistant"</c> (the system message is set separately via <see cref="LlmRequest.SystemPrompt"/>).</param>
/// <param name="Content">Plain message text.</param>
/// <param name="ToolCallId">Only for role <c>"tool"</c>: the <see cref="LlmToolCall.Id"/> this result answers.</param>
/// <param name="ToolCalls">Only for an assistant turn that requested tools (played back to the API in this shape).</param>
public sealed record LlmMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null);

/// <summary>
/// A chat-completion call. The default case is two messages (system + user) — enough for script
/// generation and workflow generation. For the multi-turn chat assistant, an optional
/// <see cref="Conversation"/> can be supplied; the client then emits
/// <c>[system, ...Conversation]</c> instead of <c>[system, user]</c> and <see cref="UserPrompt"/>
/// is ignored.
/// </summary>
/// <param name="SystemPrompt">Long, static instructions — the system role.</param>
/// <param name="UserPrompt">User input plus dynamic context — the user role. Ignored when <see cref="Conversation"/> is set.</param>
/// <param name="JsonMode">When true: send <c>response_format: {"type":"json_object"}</c>. OpenAI Cloud honors it; local endpoints usually just ignore it silently — that's fine, the caller-side parsing is tolerant enough either way.</param>
/// <param name="Conversation">Optional multi-turn history (user/assistant). When set and non-empty, it replaces the single user turn.</param>
/// <param name="Tools">Optional tool definitions (function calling). When set, the client sends <c>tools</c> plus <see cref="ToolChoice"/>.</param>
/// <param name="ToolChoice"><c>"auto"</c> (default behavior), <c>"none"</c>, or <c>"required"</c>. Only relevant when <see cref="Tools"/> is set.</param>
public sealed record LlmRequest(
    string SystemPrompt,
    string UserPrompt,
    bool JsonMode = false,
    IReadOnlyList<LlmMessage>? Conversation = null,
    IReadOnlyList<LlmToolDefinition>? Tools = null,
    string? ToolChoice = null);

/// <summary>
/// Result of a chat-completion call.
/// </summary>
/// <param name="Content">The text from <c>choices[0].message.content</c>.</param>
/// <param name="Model">Echo of the model name returned by the server (can differ from <see cref="LlmOptions.Model"/> if the server resolves aliases).</param>
/// <param name="PromptTokens">Prompt token count from <c>usage.prompt_tokens</c>; null when the server doesn't send that block.</param>
/// <param name="CompletionTokens">Completion token count from <c>usage.completion_tokens</c>; null when not provided.</param>
/// <param name="TotalTokens">Total from <c>usage.total_tokens</c>; null when not provided.</param>
/// <param name="ToolCalls">When <c>finish_reason: "tool_calls"</c>: the calls the model requested; otherwise null.</param>
/// <param name="FinishReason">Echo of <c>choices[0].finish_reason</c> (<c>"stop"</c>/<c>"tool_calls"</c>/<c>"length"</c>…).</param>
public sealed record LlmResponse(
    string Content,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? FinishReason = null);

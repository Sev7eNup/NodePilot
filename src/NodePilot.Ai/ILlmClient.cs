using System.Text.Json;

namespace NodePilot.Ai;

/// <summary>
/// Abstraction over a chat-completion call, one-shot or streaming. Errors are thrown as a
/// classified <see cref="LlmException"/> with an <see cref="LlmErrorKind"/>, never as a generic
/// exception.
/// </summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);

    /// <summary>
    /// Streams a chat completion token by token: any number of <see cref="LlmStreamEvent"/>s
    /// carrying <see cref="LlmStreamEvent.ContentDelta"/>, then exactly one with
    /// <see cref="LlmStreamEvent.Done"/>=true (model name plus optional token usage). Errors are
    /// thrown as a classified <see cref="LlmException"/>, once streaming has started through
    /// <c>MoveNextAsync</c>.
    /// </summary>
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>
/// An event in a streaming response: either a text delta (<see cref="ContentDelta"/> set,
/// <see cref="Done"/>=false) or the closing event (<see cref="Done"/>=true, carrying the model
/// name plus optional token counts; <see cref="ContentDelta"/> is null). Token counts are only
/// filled in when the request set <c>stream_options.include_usage</c>.
/// </summary>
/// <param name="GenerationMs">
/// On the Done event: the time the server spent emitting output, measured from the first chunk
/// carrying content or tool-call deltas to the end of the stream. Connect and prompt prefill are
/// excluded, so <c>CompletionTokens / GenerationMs</c> is a decode throughput. Null when the
/// stream produced no output.
/// </param>
public sealed record LlmStreamEvent(
    string? ContentDelta,
    bool Done = false,
    string? Model = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    // Opt-in tool calling: the Done event carries FinishReason and, when "tool_calls", the calls.
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? FinishReason = null,
    int? GenerationMs = null);

/// <summary>
/// An OpenAI-compatible tool definition (function calling). Offered to the model via
/// <see cref="LlmRequest.Tools"/>; under <c>tool_choice: "auto"</c> the model decides whether to
/// call one.
/// </summary>
/// <param name="Name">Unique tool name (snake_case, matching the MCP tool naming).</param>
/// <param name="Description">What the tool does; this text drives the model's choice.</param>
/// <param name="Parameters">
/// JSON schema object for the parameters; <c>{ "type":"object","properties":{} }</c> when there
/// are none.
/// </param>
public sealed record LlmToolDefinition(
    string Name,
    string Description,
    JsonElement Parameters,
    bool Strict = false);

/// <summary>
/// A tool call requested by the model, taken from <c>choices[].message.tool_calls</c>.
/// </summary>
/// <param name="Id">
/// Call ID assigned by the model; echoed back as <c>tool_call_id</c> in the tool-result turn.
/// </param>
/// <param name="Name">Function name.</param>
/// <param name="ArgumentsJson">Arguments as a JSON string, exactly as the model sent them.</param>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// A single chat message in a multi-turn conversation (chat assistant).
/// </summary>
/// <param name="Role">
/// OpenAI role: <c>"user"</c> or <c>"assistant"</c>. The system message is set separately via
/// <see cref="LlmRequest.SystemPrompt"/>.
/// </param>
/// <param name="Content">Plain message text.</param>
/// <param name="ToolCallId">
/// Only for role <c>"tool"</c>: the <see cref="LlmToolCall.Id"/> this result answers.
/// </param>
/// <param name="ToolCalls">
/// Only for an assistant turn that requested tools; played back to the API in this shape.
/// </param>
public sealed record LlmMessage(
    string Role,
    string Content,
    string? ToolCallId = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null);

/// <summary>
/// A chat-completion call. The default case is two messages (system + user), which covers script
/// and workflow generation. For the multi-turn chat assistant, an optional
/// <see cref="Conversation"/> can be supplied; the client then emits
/// <c>[system, ...Conversation]</c> instead of <c>[system, user]</c> and <see cref="UserPrompt"/>
/// is ignored.
/// </summary>
/// <param name="SystemPrompt">Static instructions sent as the system role.</param>
/// <param name="UserPrompt">
/// User input plus dynamic context, sent as the user role. Ignored when
/// <see cref="Conversation"/> is set.
/// </param>
/// <param name="JsonMode">
/// When true, send <c>response_format: {"type":"json_object"}</c>. Local endpoints often ignore
/// it silently; caller-side parsing tolerates both.
/// </param>
/// <param name="Conversation">
/// Optional multi-turn history (user/assistant). When set and non-empty, it replaces the single
/// user turn.
/// </param>
/// <param name="Tools">
/// Optional tool definitions (function calling). When set, the client sends <c>tools</c> plus
/// <see cref="ToolChoice"/>.
/// </param>
/// <param name="ToolChoice">
/// <c>"auto"</c> (default behavior), <c>"none"</c>, or <c>"required"</c>. Only relevant when
/// <see cref="Tools"/> is set.
/// </param>
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
/// <param name="Model">
/// Model name returned by the server; can differ from <see cref="LlmOptions.Model"/> when the
/// server resolves aliases.
/// </param>
/// <param name="PromptTokens">
/// Prompt token count from <c>usage.prompt_tokens</c>; null when the server omits that block.
/// </param>
/// <param name="CompletionTokens">
/// Completion token count from <c>usage.completion_tokens</c>; null when not provided.
/// </param>
/// <param name="TotalTokens">Total from <c>usage.total_tokens</c>; null when not provided.</param>
/// <param name="ToolCalls">
/// The calls the model requested when <c>finish_reason: "tool_calls"</c>; otherwise null.
/// </param>
/// <param name="FinishReason">
/// Value of <c>choices[0].finish_reason</c>, such as <c>"stop"</c> or <c>"tool_calls"</c>.
/// </param>
public sealed record LlmResponse(
    string Content,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? TotalTokens = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? FinishReason = null);

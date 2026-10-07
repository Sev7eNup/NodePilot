using System.Runtime.CompilerServices;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NodePilot.Core.Agents;

namespace NodePilot.Ai.Agents;

/// <summary>Keeps the existing guarded transports behind the framework chat contract.</summary>
public sealed class LlmChatClientAdapter(
    ILlmClient client, AgentBudget budget, AgentOptions limits,
    Func<AgentProgress, CancellationToken, Task> progress, string memberId,
    Action<LlmException>? failRun = null, ILlmClient? summaryClient = null,
    Func<string, string>? sanitize = null, Func<int>? evidenceCount = null, TeamBoard? board = null) : IChatClient
{
    private readonly AgentContextManager _context = new();
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        var system = string.Join("\n", history.Where(m => m.Role == ChatRole.System || m.Role.Value == "developer")
            .Select(m => m.Text).Append(options?.Instructions ?? ""));
        var conversation = new List<LlmMessage>();
        foreach (var message in history.Where(m => m.Role != ChatRole.System && m.Role.Value != "developer"))
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToArray();
            if (results.Length > 0)
            {
                conversation.AddRange(results.Select(r => new LlmMessage("tool",
                    r.Result is string text ? text : JsonSerializer.Serialize(r.Result), r.CallId)));
                continue;
            }
            var calls = message.Contents.OfType<FunctionCallContent>()
                .Select(c => new LlmToolCall(c.CallId, c.Name, JsonSerializer.Serialize(c.Arguments))).ToArray();
            conversation.Add(new LlmMessage(message.Role.Value, message.Text, ToolCalls: calls.Length == 0 ? null : calls));
        }
        var baseSystem = system;
        string BudgetStatus() => $"\nHost budget before this call (shared across the team): {budget.RemainingModelCalls} model calls, {budget.MaxToolCalls - budget.ToolCalls} tool calls, {budget.MaxDelegations - budget.Delegations} delegations remaining. The host separately reserves one final-report call during investigation. Use available calls for concrete checks that could change the answer; do not defer such a check merely to finish early. Reserve calls for required reviews. Stop when the requested outcome is supported or further permitted checks cannot distinguish the alternatives; never repeat reads just to spend budget. A budget limit is not evidence that an unresolved finding is proved.";
        system += BudgetStatus();
        var tools = options?.ToolMode == ChatToolMode.None || budget.LastModelCall || budget.ToolCalls >= budget.MaxToolCalls ? null
            : options?.Tools?.OfType<AIFunctionDeclaration>()
                .Select(t => new LlmToolDefinition(t.Name, t.Description, t.JsonSchema)).ToArray();
        var schemaCharacters = tools?.Sum(t => (long)t.Parameters.GetRawText().Length + t.Description.Length + t.Name.Length + 32) ?? 0;
        var delta = tools is { Length: > 0 } && conversation.LastOrDefault()?.Role == "tool"
            ? board?.TakeDelta(memberId, Math.Clamp(limits.MaxContextCharacters / 25, 256, 4000)) : null;
        conversation = await _context.PrepareAsync(conversation, schemaCharacters + system.Length + (delta?.Content.Length ?? 0), limits.MaxContextCharacters,
            (request, token) => CompleteWorkingAsync(request, "context_summary", token), progress, memberId, cancellationToken, evidenceCount?.Invoke() ?? 0);
        // Compaction consumes the same budget and may leave only the final answer call.
        if (budget.LastModelCall) tools = null;
        if (delta is not null && tools is { Length: > 0 })
        {
            conversation[^1] = conversation[^1] with { Content = conversation[^1].Content + delta.Content };
            await progress(new AgentProgress("team_board", JsonSerializer.Serialize(new { ids = delta.Ids, omitted = delta.Omitted }), memberId), cancellationToken);
        }
        system = baseSystem + BudgetStatus();
        var request = new LlmRequest(system, "",
            JsonMode: options?.ResponseFormat is ChatResponseFormatJson,
            Conversation: conversation, Tools: tools is { Length: > 0 } ? tools : null);
        var response = await CompleteAsync(request, client, "agent", cancellationToken);
        var contents = new List<AIContent>();
        if (response.Content.Length > 0) contents.Add(new TextContent(response.Content));
        foreach (var call in response.ToolCalls ?? [])
        {
            if (tools is null || !tools.Any(t => t.Name == call.Name))
                throw new InvalidOperationException($"Model requested an unavailable tool: {call.Name}.");
            if (call.ArgumentsJson.Length > 64_000) throw new ArgumentException("Tool arguments exceed the size limit.");
            var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(call.ArgumentsJson)
                ?? throw new ArgumentException("Tool arguments must be an object.");
            contents.Add(new FunctionCallContent(call.Id, call.Name, arguments));
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = response.Model,
            FinishReason = response.ToolCalls is { Count: > 0 } ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = response.PromptTokens, OutputTokenCount = response.CompletionTokens, TotalTokenCount = response.TotalTokens }
        };
    }

    internal async Task<LlmResponse> CompleteWorkingAsync(LlmRequest request, string purpose, CancellationToken ct)
    {
        if (budget.LastModelCall) throw new AgentBudgetExceededException("No model budget remains for working notes and a final answer.", AgentBudgetLimit.ModelCalls);
        if (request.SystemPrompt.Length + request.UserPrompt.Length > limits.MaxContextCharacters)
            throw new AgentBudgetExceededException("Working analysis input exceeds the context limit.");
        return await CompleteAsync(request, summaryClient ?? client, purpose, ct);
    }

    private async Task<LlmResponse> CompleteAsync(LlmRequest request, ILlmClient transport, string purpose, CancellationToken cancellationToken)
    {
        var callNumber = budget.TakeModelCall();
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await progress(new AgentProgress("model_started", $"Model call {callNumber} ({purpose}; timeout: {limits.ModelCallTimeoutSeconds}s)", memberId), cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(limits.ModelCallTimeoutSeconds));
            LlmResponse response;
            try
            {
                response = await transport.CompleteAsync(request, timeout.Token);
                budget.AddUsage(response.PromptTokens, response.CompletionTokens);
                timeout.Token.ThrowIfCancellationRequested();
                if (response.FinishReason == "length" && purpose != "context_summary")
                    throw new LlmException(LlmErrorKind.MalformedResponse,
                        $"Agent member '{memberId}' exhausted the model output limit. The incomplete response was rejected; no tools were executed from it.");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                (ex is OperationCanceledException && timeout.IsCancellationRequested || ex is LlmException))
            {
                var failure = ex is OperationCanceledException || ex is LlmException { Kind: LlmErrorKind.Timeout }
                    ? new LlmException(LlmErrorKind.Timeout,
                        $"Agent member '{memberId}' model call {callNumber} timed out before a complete response "
                        + $"(agent limit: {limits.ModelCallTimeoutSeconds}s; a shorter profile timeout also applies).", inner: ex)
                    : new LlmException(((LlmException)ex).Kind, $"Agent member '{memberId}' model call {callNumber} failed: {ex.Message}",
                        httpStatus: ((LlmException)ex).HttpStatus, inner: ex);
                var transient = IsTransient(failure);
                // Retry only the transport request; existing tool results and the session stay unchanged.
                var reserveAnswer = purpose != "agent" || request.Tools is { Count: > 0 };
                if (attempt == 0 && transient && budget.RemainingModelCalls > (reserveAnswer ? 1 : 0))
                {
                    await progress(new AgentProgress("model_retrying",
                        $"Model call {callNumber} ({purpose}) failed with {failure.Kind}; retrying the same request once after 1s within the shared budget. No tool is replayed.", memberId), cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    if (budget.TryTakeModelRetry(reserveAnswer, out var retryCall))
                    {
                        callNumber = retryCall;
                        continue;
                    }
                }
                await progress(new AgentProgress("model_failed", failure.Message, memberId), cancellationToken);
                // Cancel the shared run so nested delegation cannot turn this into another model call.
                failRun?.Invoke(failure);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(failure.Message, failure, cancellationToken);
                throw failure;
            }
            await progress(new AgentProgress(response.FinishReason == "length" ? "model_output_truncated" : "model_completed",
                $"Model call {callNumber} ({purpose}) " + (response.FinishReason == "length" ? "returned an incomplete working summary; its content is discarded." : "completed"), memberId), cancellationToken);
            return response with { Content = sanitize?.Invoke(response.Content) ?? response.Content };
        }
    }

    private static bool IsTransient(LlmException failure)
    {
        if (failure.Kind is LlmErrorKind.Timeout or LlmErrorKind.RateLimited
            || failure.Kind == LlmErrorKind.UpstreamError && failure.HttpStatus is 408 or 500 or 502 or 503 or 504)
            return true;
        if (failure.Kind != LlmErrorKind.Unreachable) return false;
        var interrupted = false;
        for (Exception? cause = failure; cause is not null; cause = cause.InnerException)
        {
            // TLS/authentication failures stay terminal even when their inner error is a reset.
            if (cause is AuthenticationException
                || cause is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
                return false;
            if (cause is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset }
                || cause is HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded })
                interrupted = true;
        }
        return interrupted;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

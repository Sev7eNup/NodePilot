using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static NodePilot.Ai.LlmJson;

namespace NodePilot.Ai;

/// <summary>
/// HTTP client for an OpenAI-compatible <b>chat-completions</b> endpoint. Works against OpenAI
/// Cloud, Ollama, LM Studio, vLLM, LocalAI, and llama.cpp servers — they all implement the same
/// wire format. The URL to POST to comes from <see cref="LlmEndpointTarget.PostUrl"/>; OpenAI's
/// separate <c>/responses</c> dialect is served by <see cref="OpenAiResponsesLlmClient"/> instead.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private static readonly ConcurrentDictionary<string, byte> MaxCompletionTokenEndpoints =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly LlmClientConfig _config;
    private readonly ILogger<OpenAiCompatibleLlmClient> _logger;
    private readonly LlmHttpTransport _transport;

    public OpenAiCompatibleLlmClient(
        IHttpClientFactory httpClientFactory,
        LlmClientConfig config,
        ILogger<OpenAiCompatibleLlmClient> logger)
    {
        _config = config;
        _logger = logger;
        _transport = new LlmHttpTransport(httpClientFactory, config, logger);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        // Outer fallback: newer OpenAI models (o-series / GPT-5 era) reject `max_tokens` with
        // HTTP 400 and require `max_completion_tokens` instead. We detect exactly this quirk (the
        // response body mentions `max_completion_tokens`) and retry once with the new key. Local
        // and older endpoints keep receiving `max_tokens` as before. Same fallback idiom as the
        // response_format/stream_options fallbacks below.
        var effectiveRequest = request;
        var useMaxCompletionTokens = PrefersMaxCompletionTokens();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await CompleteWithJsonFallbackAsync(
                    effectiveRequest, useMaxCompletionTokens, ct);
            }
            catch (LlmException ex) when (!useMaxCompletionTokens && IsMaxTokensUnsupported(ex))
            {
                RememberMaxCompletionTokens();
                useMaxCompletionTokens = true;
                _logger.LogWarning(
                    "LLM upstream rejected max_tokens with HTTP 400 — retrying with max_completion_tokens. Body: {BodyExcerpt}",
                    ex.BodyExcerpt);
            }
            catch (LlmException ex) when (HasStrictTools(effectiveRequest) && IsStrictToolsUnsupported(ex))
            {
                effectiveRequest = WithoutStrictTools(effectiveRequest);
                _logger.LogWarning(
                    "LLM upstream rejected strict function schemas — retrying with best-effort tool calling. Body: {BodyExcerpt}",
                    ex.BodyExcerpt);
            }

            if (attempt >= 2) throw new InvalidOperationException("LLM compatibility fallback limit exceeded.");
        }
    }

    private async Task<LlmResponse> CompleteWithJsonFallbackAsync(
        LlmRequest request, bool useMaxCompletionTokens, CancellationToken ct)
    {
        // First attempt: with (or without, depending on the request) `response_format:
        // json_object`. If the upstream rejects that with HTTP 400 — typical for local models
        // without JSON-mode support (e.g. LM Studio running gemma) — we fall back exactly once to
        // a call without that field. The existing "reply with ONLY JSON" hint in the
        // workflow-generation prompt, plus the caller-side JSON-parse retry, are tolerant enough
        // to parse the result cleanly either way. The max_tokens quirk is deliberately NOT caught
        // here, so the outer CompleteAsync catch handles it instead.
        try
        {
            return await SendOnceAsync(request, includeJsonResponseFormat: request.JsonMode, useMaxCompletionTokens, ct);
        }
        catch (LlmException ex) when (request.JsonMode
            && ex.Kind == LlmErrorKind.UpstreamError
            && ex.HttpStatus == (int)HttpStatusCode.BadRequest
            && !IsMaxTokensUnsupported(ex)
            && !IsStrictToolsUnsupported(ex))
        {
            _logger.LogWarning(
                "LLM upstream rejected response_format=json_object with HTTP 400 — retrying without it. Body: {BodyExcerpt}",
                ex.BodyExcerpt);
            return await SendOnceAsync(request, includeJsonResponseFormat: false, useMaxCompletionTokens, ct);
        }
    }

    /// <summary>
    /// Detects the OpenAI quirk "<c>max_tokens</c> is not supported with this model. Use
    /// <c>max_completion_tokens</c> instead." (HTTP 400, code <c>unsupported_parameter</c>).
    /// Discriminated by the body substring <c>max_completion_tokens</c> — that string never
    /// appears as a substring of <c>max_tokens</c>, so there's no false-positive risk.
    /// </summary>
    private static bool IsMaxTokensUnsupported(LlmException ex) =>
        ex.Kind == LlmErrorKind.UpstreamError
        && ex.HttpStatus == (int)HttpStatusCode.BadRequest
        && ex.BodyExcerpt is { } body
        && body.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase);

    private static bool IsStrictToolsUnsupported(LlmException ex) =>
        ex.Kind == LlmErrorKind.UpstreamError
        && ex.HttpStatus == (int)HttpStatusCode.BadRequest
        && ex.BodyExcerpt is { } body
        && (body.Contains("strict", StringComparison.OrdinalIgnoreCase)
            || body.Contains("additionalProperties", StringComparison.OrdinalIgnoreCase));

    private bool PrefersMaxCompletionTokens() =>
        MaxCompletionTokenEndpoints.ContainsKey(CompatibilityKey);

    private void RememberMaxCompletionTokens() =>
        MaxCompletionTokenEndpoints.TryAdd(CompatibilityKey, 0);

    private string CompatibilityKey => $"{_config.Endpoint.PostUrl}|{_config.Model}";

    private static bool HasStrictTools(LlmRequest request) =>
        request.Tools?.Any(t => t.Strict) == true;

    private static LlmRequest WithoutStrictTools(LlmRequest request) =>
        request with
        {
            Tools = request.Tools?
                .Select(t => t.Strict ? t with { Strict = false } : t)
                .ToArray(),
        };

    private async Task<LlmResponse> SendOnceAsync(
        LlmRequest request, bool includeJsonResponseFormat, bool useMaxCompletionTokens, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _config.Model,
            [useMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = _config.MaxTokens,
            ["messages"] = BuildMessages(request),
        };
        if (_config.Temperature is double temperature)
            body["temperature"] = temperature;
        if (includeJsonResponseFormat)
        {
            body["response_format"] = new { type = "json_object" };
        }
        AppendTools(body, request);

        using var timeoutCts = _transport.CreateTimeoutScope(ct);
        using var resp = await _transport.SendAsync(
            body, HttpCompletionOption.ResponseContentRead, timeoutCts.Token, ct);
        using var doc = await LlmHttpTransport.ReadJsonAsync(resp, ct);

        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "LLM-Antwort enthielt kein 'choices'-Array.");
        }
        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "LLM-Antwort enthielt kein 'choices[0].message'-Objekt.");
        }

        // Tool-call responses often have content: null plus tool_calls — accept both cases.
        var contentStr = ReadString(message, "content") ?? string.Empty;
        var toolCalls = ParseToolCalls(message);
        var finishReason = ReadString(first, "finish_reason");
        if (contentStr.Length == 0 && (toolCalls is null || toolCalls.Count == 0))
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "LLM-Antwort enthielt weder 'content' (string) noch 'tool_calls'.");
        }

        var modelEcho = ReadString(doc.RootElement, "model") ?? _config.Model;

        int? promptTokens = null, completionTokens = null, totalTokens = null;
        if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            promptTokens = ReadInt(usage, "prompt_tokens");
            completionTokens = ReadInt(usage, "completion_tokens");
            totalTokens = ReadInt(usage, "total_tokens");
        }

        return new LlmResponse(contentStr, modelEcho,
            promptTokens, completionTokens, totalTokens, toolCalls, finishReason);
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var timeoutCts = _transport.CreateTimeoutScope(ct);
        var token = timeoutCts.Token;

        // Sends the request, including the stream_options and max_completion_tokens fallbacks.
        // yield isn't allowed inside try/catch, hence these are separate methods. The outer catch
        // here handles the max_tokens quirk (see CompleteAsync).
        HttpResponseMessage resp;
        var effectiveRequest = request;
        var useMaxCompletionTokens = PrefersMaxCompletionTokens();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                resp = await SendStreamingWithStreamOptionsFallbackAsync(
                    effectiveRequest, useMaxCompletionTokens, token, ct);
                break;
            }
            catch (LlmException ex) when (!useMaxCompletionTokens && IsMaxTokensUnsupported(ex))
            {
                RememberMaxCompletionTokens();
                useMaxCompletionTokens = true;
                _logger.LogWarning(
                    "LLM upstream rejected max_tokens with HTTP 400 — retrying with max_completion_tokens. Body: {BodyExcerpt}",
                    ex.BodyExcerpt);
            }
            catch (LlmException ex) when (HasStrictTools(effectiveRequest) && IsStrictToolsUnsupported(ex))
            {
                effectiveRequest = WithoutStrictTools(effectiveRequest);
                _logger.LogWarning(
                    "LLM upstream rejected strict function schemas — retrying with best-effort tool calling. Body: {BodyExcerpt}",
                    ex.BodyExcerpt);
            }

            if (attempt >= 2) throw new InvalidOperationException("LLM compatibility fallback limit exceeded.");
        }

        using (resp)
        {
            string? model = null;
            string? finishReason = null;
            int? promptTokens = null, completionTokens = null;
            var toolAcc = new Dictionary<int, ToolCallAccumulator>();
            var toolAutoIndex = 0; // slot counter for the index-less streaming path (see AccumulateToolCallDeltas)
            // Marks when the server started emitting output, so the Done event can report a decode
            // throughput instead of a wall-clock rate. Everything before this stamp — connect, and
            // above all prompt prefill — is not generation: a 3k-token prompt can take seconds while
            // the answer itself decodes in milliseconds. Strictly the window spans n-1 decode
            // intervals for n tokens, so very short answers read slightly high; irrelevant at
            // realistic answer lengths.
            long? firstOutputTs = null;

            await foreach (var data in _transport.ReadSseDataAsync(resp, token, ct))
            {
                // Parsing happens outside a yield block (yield is not allowed inside try/catch).
                string? delta = null;
                var sawToolDelta = false;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    model = ReadString(root, "model") ?? model;
                    if (root.TryGetProperty("choices", out var choices)
                        && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var choice0 = choices[0];
                        finishReason = ReadString(choice0, "finish_reason") ?? finishReason;
                        if (choice0.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object)
                        {
                            delta = ReadString(d, "content");
                            if (d.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                            {
                                AccumulateToolCallDeltas(tcs, toolAcc, ref toolAutoIndex);
                                sawToolDelta = tcs.GetArrayLength() > 0;
                            }
                        }
                    }
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        promptTokens = ReadInt(usage, "prompt_tokens") ?? promptTokens;
                        completionTokens = ReadInt(usage, "completion_tokens") ?? completionTokens;
                    }
                }
                catch (JsonException)
                {
                    delta = null; // skip this one malformed chunk and keep going
                }

                // An empty content string doesn't count: OpenAI opens every stream with a role-only
                // chunk carrying `content: ""`, which would start the clock before the first token.
                if (!string.IsNullOrEmpty(delta) || sawToolDelta)
                    firstOutputTs ??= Stopwatch.GetTimestamp();

                if (!string.IsNullOrEmpty(delta))
                    yield return new LlmStreamEvent(delta, Model: model);
            }

            // Attach the accumulated tool calls (if the model requested any) to the Done event.
            var finalToolCalls = ToolCallAccumulator.Materialize(toolAcc);

            var generationMs = firstOutputTs is long outputStart
                ? (int)Stopwatch.GetElapsedTime(outputStart).TotalMilliseconds
                : (int?)null;

            yield return new LlmStreamEvent(null, Done: true, Model: model ?? _config.Model,
                PromptTokens: promptTokens, CompletionTokens: completionTokens,
                ToolCalls: finalToolCalls, FinishReason: finishReason, GenerationMs: generationMs);
        }
    }

    /// <summary>
    /// Sends the streaming request and, on a <c>stream_options</c>-related HTTP 400, retries
    /// exactly once without that field (some local servers don't know it — the response then
    /// simply has no token usage). The max_tokens quirk is deliberately NOT caught here, so the
    /// outer StreamAsync catch handles it instead.
    /// </summary>
    private async Task<HttpResponseMessage> SendStreamingWithStreamOptionsFallbackAsync(
        LlmRequest request, bool useMaxCompletionTokens, CancellationToken token, CancellationToken ct)
    {
        try
        {
            return await SendStreamingAsync(request, includeStreamOptions: true, useMaxCompletionTokens, token, ct);
        }
        catch (LlmException ex) when (ex.Kind == LlmErrorKind.UpstreamError
            && ex.HttpStatus == (int)HttpStatusCode.BadRequest
            && !IsMaxTokensUnsupported(ex)
            && !IsStrictToolsUnsupported(ex))
        {
            _logger.LogWarning("LLM upstream rejected stream_options with HTTP 400 — retrying without it.");
            return await SendStreamingAsync(request, includeStreamOptions: false, useMaxCompletionTokens, token, ct);
        }
    }

    private async Task<HttpResponseMessage> SendStreamingAsync(
        LlmRequest request, bool includeStreamOptions, bool useMaxCompletionTokens,
        CancellationToken token, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _config.Model,
            [useMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = _config.MaxTokens,
            ["stream"] = true,
            ["messages"] = BuildMessages(request),
        };
        if (_config.Temperature is double temperature)
            body["temperature"] = temperature;
        if (includeStreamOptions)
            body["stream_options"] = new { include_usage = true };
        AppendTools(body, request);

        return await _transport.SendAsync(body, HttpCompletionOption.ResponseHeadersRead, token, ct);
    }

    /// <summary>Builds the OpenAI `messages` array: [system, ...Conversation] or [system, user]. Also
    /// serializes tool-call assistant turns (with <c>tool_calls</c>) and tool-result turns (Role <c>"tool"</c>).</summary>
    private static List<object> BuildMessages(LlmRequest request)
    {
        var messages = new List<object>(1 + (request.Conversation?.Count ?? 1))
        {
            new { role = "system", content = request.SystemPrompt },
        };
        if (request.Conversation is { Count: > 0 } turns)
            foreach (var turn in turns)
                messages.Add(MessageToWire(turn));
        else
            messages.Add(new { role = "user", content = request.UserPrompt });
        return messages;
    }

    /// <summary>Maps an <see cref="LlmMessage"/> to the OpenAI wire form (incl. tool role + tool_calls).</summary>
    private static object MessageToWire(LlmMessage turn)
    {
        if (string.Equals(turn.Role, "tool", StringComparison.Ordinal))
            return new { role = "tool", tool_call_id = turn.ToolCallId, content = turn.Content };
        if (turn.ToolCalls is { Count: > 0 } calls)
            return new
            {
                role = turn.Role,
                content = string.IsNullOrEmpty(turn.Content) ? null : turn.Content,
                tool_calls = calls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.Name, arguments = tc.ArgumentsJson },
                }).ToArray(),
            };
        return new { role = turn.Role, content = turn.Content };
    }

    /// <summary>Appends `tools` + `tool_choice` to the body — but only when the request supplies tools.</summary>
    private static void AppendTools(Dictionary<string, object?> body, LlmRequest request)
    {
        if (request.Tools is not { Count: > 0 } tools) return;
        body["tools"] = tools.Select(t =>
        {
            var function = new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters,
            };
            if (t.Strict) function["strict"] = true;
            return new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = function,
            };
        }).ToArray();
        body["tool_choice"] = request.ToolChoice ?? "auto";
    }

    /// <summary>Parses `choices[0].message.tool_calls` (non-streaming) into <see cref="LlmToolCall"/>s; null if there are none.</summary>
    private static IReadOnlyList<LlmToolCall>? ParseToolCalls(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var tcs)
            || tcs.ValueKind != JsonValueKind.Array || tcs.GetArrayLength() == 0)
            return null;
        var list = new List<LlmToolCall>();
        foreach (var tc in tcs.EnumerateArray())
        {
            var id = ReadString(tc, "id") ?? "";
            if (!tc.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
            var name = ReadString(fn, "name") ?? "";
            var args = ReadString(fn, "arguments") ?? "";
            if (name.Length > 0) list.Add(new LlmToolCall(id, name, args));
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Akkumuliert die inkrementellen <c>delta.tool_calls</c> eines Streaming-Chunks (id/name einmal,
    /// arguments konkateniert). Schlüssel ist bevorzugt das OpenAI-<c>index</c>-Feld. Fehlt es (manche
    /// lokale Runtimes wie LM Studio senden keins), wird ein <b>neuer</b> Slot angelegt, sobald ein
    /// Fragment eine nicht-leere <c>id</c> ODER <c>function.name</c> trägt (= Beginn eines neuen Calls);
    /// reine Argument-Fortsetzungen hängen an den zuletzt geöffneten Slot. Damit kollabieren mehrere
    /// index-lose parallele Tool-Calls nicht mehr in einen einzigen (überschriebene id/name, konkatenierte
    /// Argumente). <paramref name="autoIndex"/> ist der Zähler für den index-losen Pfad und muss über die
    /// Chunks eines Streams hinweg gehalten werden.
    /// </summary>
    private static void AccumulateToolCallDeltas(JsonElement toolCallsArray, Dictionary<int, ToolCallAccumulator> acc, ref int autoIndex)
    {
        foreach (var tc in toolCallsArray.EnumerateArray())
        {
            var id = ReadString(tc, "id");
            var hasId = !string.IsNullOrEmpty(id);
            var hasFn = tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object;
            var fnName = hasFn ? ReadString(fn, "name") : null;
            var startsNewCall = hasId || !string.IsNullOrEmpty(fnName);

            int index;
            if (ReadInt(tc, "index") is { } wireIndex)
                index = wireIndex;                   // canonical OpenAI incremental stream
            else if (startsNewCall || acc.Count == 0)
                index = autoIndex++;                 // index-less runtime: a new call opens a fresh slot
            else
                index = Math.Max(0, autoIndex - 1);  // index-less arguments continuation → current slot

            var slot = ToolCallAccumulator.Slot(acc, index);
            if (hasId)
                slot.Id = id!;
            if (hasFn)
            {
                if (!string.IsNullOrEmpty(fnName))
                    slot.Name = fnName;
                if (ReadString(fn, "arguments") is { } arguments)
                    slot.Arguments.Append(arguments);
            }
        }
    }

}

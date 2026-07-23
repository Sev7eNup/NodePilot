using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NodePilot.Ai;

/// <summary>
/// HTTP client for an OpenAI-compatible chat-completions endpoint. Works against OpenAI Cloud,
/// Ollama, LM Studio, vLLM, LocalAI, and llama.cpp servers — they all implement the same wire
/// format under <c>{BaseUrl}/chat/completions</c>.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private const int BodyExcerptMaxChars = 500;

    // L-4 (security audit 2026-05-15): cap upstream response bodies before parsing them so
    // a hostile or runaway LLM endpoint cannot exhaust memory by streaming gigabytes into
    // JsonDocument.ParseAsync. 16 MiB is well above any realistic chat-completion payload
    // (typical Workflow-Gen responses are <100 KiB) and safe to allocate in one shot.
    private const long MaxResponseBytes = 16L * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, byte> MaxCompletionTokenEndpoints =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LlmClientConfig _config;
    private readonly ILogger<OpenAiCompatibleLlmClient> _logger;

    public OpenAiCompatibleLlmClient(
        IHttpClientFactory httpClientFactory,
        LlmClientConfig config,
        ILogger<OpenAiCompatibleLlmClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
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

    private string CompatibilityKey => $"{_config.BaseUrl.TrimEnd('/')}|{_config.Model}";

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
        var http = _httpClientFactory.CreateClient(LlmHttpClient.Name);

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

        // BaseUrl robustness: the operator may configure it with or without a trailing slash,
        // with or without /v1. We simply append "chat/completions" and normalize the trailing slash.
        var baseUrl = _config.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/chat/completions";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

        // Timeout via a linked CTS, so the caller can cancel at any time without
        // HttpClient.Timeout getting in the way globally.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException(LlmErrorKind.Timeout,
                $"LLM-Endpoint hat innerhalb von {_config.TimeoutSeconds}s nicht geantwortet ({_config.BaseUrl}).");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(LlmErrorKind.Unreachable,
                $"LLM-Endpoint nicht erreichbar ({_config.BaseUrl}): {ex.Message}", inner: ex);
        }

        // L-4: pre-flight ContentLength check rejects oversized responses without ever
        // touching the body. A hostile upstream omitting Content-Length still goes through
        // the LimitedStream wrapper below, so this is the cheap-path optimization only.
        if (resp.Content.Headers.ContentLength is long cl && cl > MaxResponseBytes)
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                $"LLM-Antwort überschreitet das Body-Limit ({cl} > {MaxResponseBytes} bytes).",
                httpStatus: (int)resp.StatusCode);
        }
        await using var rawStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var stream = new LengthLimitedStream(rawStream, MaxResponseBytes);
        if (!resp.IsSuccessStatusCode)
        {
            var bodyText = await ReadBodyExcerptAsync(stream, ct);
            var kind = resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => LlmErrorKind.Unauthorized,
                HttpStatusCode.Forbidden => LlmErrorKind.Unauthorized,
                HttpStatusCode.TooManyRequests => LlmErrorKind.RateLimited,
                _ => LlmErrorKind.UpstreamError,
            };
            _logger.LogWarning("LLM upstream returned {Status} for model {Model}: {BodyExcerpt}",
                (int)resp.StatusCode, _config.Model, bodyText);
            throw new LlmException(kind,
                $"LLM-Endpoint antwortete mit HTTP {(int)resp.StatusCode}.",
                httpStatus: (int)resp.StatusCode, bodyExcerpt: bodyText);
        }

        try
        {
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
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
            var contentStr = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : string.Empty;
            var toolCalls = ParseToolCalls(message);
            var finishReason = first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                ? fr.GetString() : null;
            if (contentStr.Length == 0 && (toolCalls is null || toolCalls.Count == 0))
            {
                throw new LlmException(LlmErrorKind.MalformedResponse,
                    "LLM-Antwort enthielt weder 'content' (string) noch 'tool_calls'.");
            }

            var modelEcho = doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? _config.Model
                : _config.Model;

            int? promptTokens = null, completionTokens = null, totalTokens = null;
            if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number
                    ? pt.GetInt32() : null;
                // Deliberately not named `ct` locally — that would shadow the outer CancellationToken parameter (CS0136).
                completionTokens = usage.TryGetProperty("completion_tokens", out var ctTok) && ctTok.ValueKind == JsonValueKind.Number
                    ? ctTok.GetInt32() : null;
                totalTokens = usage.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number
                    ? tt.GetInt32() : null;
            }

            return new LlmResponse(contentStr, modelEcho,
                promptTokens, completionTokens, totalTokens, toolCalls, finishReason);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Body-Limit", StringComparison.Ordinal))
        {
            // L-4: LengthLimitedStream tripped — upstream sent more than MaxResponseBytes.
            throw new LlmException(LlmErrorKind.MalformedResponse,
                ex.Message, inner: ex);
        }
        catch (JsonException ex)
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "LLM-Antwort war kein valides JSON.", inner: ex);
        }
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.TimeoutSeconds));
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
                    effectiveRequest, useMaxCompletionTokens, token);
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException(LlmErrorKind.Timeout,
                    $"LLM-Endpoint hat innerhalb von {_config.TimeoutSeconds}s nicht geantwortet ({_config.BaseUrl}).");
            }
            catch (HttpRequestException ex)
            {
                throw new LlmException(LlmErrorKind.Unreachable,
                    $"LLM-Endpoint nicht erreichbar ({_config.BaseUrl}): {ex.Message}", inner: ex);
            }

            if (attempt >= 2) throw new InvalidOperationException("LLM compatibility fallback limit exceeded.");
        }

        using (resp)
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream);

            long totalBytes = 0;
            string? model = null;
            string? finishReason = null;
            int? promptTokens = null, completionTokens = null;
            var toolAcc = new Dictionary<int, ToolCallAccumulator>();
            var toolAutoIndex = 0; // slot counter for the index-less streaming path (see AccumulateToolCallDeltas)

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new LlmException(LlmErrorKind.Timeout,
                        $"LLM-Stream lieferte innerhalb von {_config.TimeoutSeconds}s nicht weiter ({_config.BaseUrl}).");
                }
                if (line is null) break;

                // L-4: byte cap applies in the streaming path too (LengthLimitedStream doesn't cover this).
                totalBytes += line.Length;
                if (totalBytes > MaxResponseBytes)
                    throw new LlmException(LlmErrorKind.MalformedResponse,
                        $"LLM-Stream überschreitet das Body-Limit ({MaxResponseBytes} bytes).");

                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;

                // Parsing happens outside a yield block (yield is not allowed inside try/catch).
                string? delta = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                        model = m.GetString();
                    if (root.TryGetProperty("choices", out var choices)
                        && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var choice0 = choices[0];
                        if (choice0.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                            finishReason = frEl.GetString();
                        if (choice0.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.Object)
                        {
                            if (d.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                                delta = c.GetString();
                            if (d.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                                AccumulateToolCallDeltas(tcs, toolAcc, ref toolAutoIndex);
                        }
                    }
                    if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                            promptTokens = pt.GetInt32();
                        if (usage.TryGetProperty("completion_tokens", out var cpt) && cpt.ValueKind == JsonValueKind.Number)
                            completionTokens = cpt.GetInt32();
                    }
                }
                catch (JsonException)
                {
                    delta = null; // skip this one malformed chunk and keep going
                }

                if (!string.IsNullOrEmpty(delta))
                    yield return new LlmStreamEvent(delta, Model: model);
            }

            // Attach the accumulated tool calls (if the model requested any) to the Done event.
            IReadOnlyList<LlmToolCall>? finalToolCalls = toolAcc.Count > 0
                ? toolAcc.OrderBy(kv => kv.Key)
                    .Select(kv => new LlmToolCall(kv.Value.Id, kv.Value.Name, kv.Value.Arguments.ToString()))
                    .Where(t => t.Name.Length > 0).ToList()
                : null;
            if (finalToolCalls is { Count: 0 }) finalToolCalls = null;

            yield return new LlmStreamEvent(null, Done: true, Model: model ?? _config.Model,
                PromptTokens: promptTokens, CompletionTokens: completionTokens,
                ToolCalls: finalToolCalls, FinishReason: finishReason);
        }
    }

    /// <summary>
    /// Sends the streaming request and, on a <c>stream_options</c>-related HTTP 400, retries
    /// exactly once without that field (some local servers don't know it — the response then
    /// simply has no token usage). The max_tokens quirk is deliberately NOT caught here, so the
    /// outer StreamAsync catch handles it instead.
    /// </summary>
    private async Task<HttpResponseMessage> SendStreamingWithStreamOptionsFallbackAsync(
        LlmRequest request, bool useMaxCompletionTokens, CancellationToken token)
    {
        try
        {
            return await SendStreamingAsync(request, includeStreamOptions: true, useMaxCompletionTokens, token);
        }
        catch (LlmException ex) when (ex.Kind == LlmErrorKind.UpstreamError
            && ex.HttpStatus == (int)HttpStatusCode.BadRequest
            && !IsMaxTokensUnsupported(ex)
            && !IsStrictToolsUnsupported(ex))
        {
            _logger.LogWarning("LLM upstream rejected stream_options with HTTP 400 — retrying without it.");
            return await SendStreamingAsync(request, includeStreamOptions: false, useMaxCompletionTokens, token);
        }
    }

    private async Task<HttpResponseMessage> SendStreamingAsync(
        LlmRequest request, bool includeStreamOptions, bool useMaxCompletionTokens, CancellationToken token)
    {
        var http = _httpClientFactory.CreateClient(LlmHttpClient.Name);

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

        var baseUrl = _config.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
        if (!resp.IsSuccessStatusCode)
        {
            await using var errStream = await resp.Content.ReadAsStreamAsync(token);
            var bodyText = await ReadBodyExcerptAsync(errStream, token);
            var status = (int)resp.StatusCode;
            var kind = resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => LlmErrorKind.Unauthorized,
                HttpStatusCode.Forbidden => LlmErrorKind.Unauthorized,
                HttpStatusCode.TooManyRequests => LlmErrorKind.RateLimited,
                _ => LlmErrorKind.UpstreamError,
            };
            _logger.LogWarning("LLM streaming upstream returned {Status} for model {Model}: {BodyExcerpt}",
                status, _config.Model, bodyText);
            resp.Dispose();
            throw new LlmException(kind, $"LLM-Endpoint antwortete mit HTTP {status}.",
                httpStatus: status, bodyExcerpt: bodyText);
        }
        return resp;
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
            var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
            if (!tc.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object) continue;
            var name = fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? "" : "";
            var args = fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String ? ar.GetString() ?? "" : "";
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
            var hasId = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(idEl.GetString());
            var hasFn = tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object;
            var startsNewCall = hasId
                || (hasFn && fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(nm.GetString()));

            int index;
            if (tc.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number)
                index = ix.GetInt32();               // canonical OpenAI incremental stream
            else if (startsNewCall || acc.Count == 0)
                index = autoIndex++;                 // index-less runtime: a new call opens a fresh slot
            else
                index = Math.Max(0, autoIndex - 1);  // index-less arguments continuation → current slot

            if (!acc.TryGetValue(index, out var slot)) { slot = new ToolCallAccumulator(); acc[index] = slot; }
            if (hasId)
                slot.Id = idEl.GetString()!;
            if (hasFn)
            {
                if (fn.TryGetProperty("name", out var nm2) && nm2.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(nm2.GetString()))
                    slot.Name = nm2.GetString()!;
                if (fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String)
                    slot.Arguments.Append(ar.GetString());
            }
        }
    }

    private sealed class ToolCallAccumulator
    {
        public string Id = "";
        public string Name = "";
        public System.Text.StringBuilder Arguments { get; } = new();
    }

    private static async Task<string> ReadBodyExcerptAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            using var sr = new StreamReader(stream);
            var raw = await sr.ReadToEndAsync(ct);
            return raw.Length > BodyExcerptMaxChars
                ? raw[..BodyExcerptMaxChars] + "…"
                : raw;
        }
        catch
        {
            return "<body unreadable>";
        }
    }
}

/// <summary>
/// Const container for the named HttpClient's name. Registered in
/// <see cref="LlmServiceCollectionExtensions.AddNodePilotAi"/> and resolved by
/// <see cref="OpenAiCompatibleLlmClient"/> via <see cref="IHttpClientFactory"/>.
/// </summary>
public static class LlmHttpClient
{
    public const string Name = "Llm";
}

/// <summary>
/// Read-only stream wrapper that throws after <paramref name="maxBytes"/> have been read.
/// L-4: protects <see cref="JsonDocument.ParseAsync(Stream, JsonDocumentOptions, CancellationToken)"/>
/// from gigabyte-scale upstream responses when the LLM endpoint omits Content-Length or lies
/// about it.
/// </summary>
internal sealed class LengthLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private long _read;

    public LengthLimitedStream(Stream inner, long maxBytes) { _inner = inner; _maxBytes = maxBytes; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Advance(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await _inner.ReadAsync(buffer, ct);
        Advance(n);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        Advance(n);
        return n;
    }

    private void Advance(int n)
    {
        _read += n;
        if (_read > _maxBytes)
            throw new InvalidOperationException(
                $"LLM-Antwort überschreitet das Body-Limit ({_read} > {_maxBytes} bytes).");
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

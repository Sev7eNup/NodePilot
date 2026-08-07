using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NodePilot.Ai;

/// <summary>
/// HTTP client for OpenAI's <b>Responses</b> API (<c>POST /v1/responses</c>) — a second wire
/// dialect next to chat completions, selected by <see cref="LlmEndpointGuard.ResolveEndpoint"/>
/// when the configured BaseUrl ends in <c>/responses</c>. Newer OpenAI models are served only
/// there, so this is not a stylistic choice: without it those models are unreachable.
///
/// <para>Differences to <see cref="OpenAiCompatibleLlmClient"/> that matter downstream: the prompt
/// travels as <c>input</c> (not <c>messages</c>), the cap is <c>max_output_tokens</c>, JSON mode is
/// <c>text.format</c>, tool definitions are flat (no nested <c>function</c> object), tool calls and
/// their results are top-level <c>function_call</c>/<c>function_call_output</c> items, and the
/// stream is a sequence of typed events instead of choice deltas.</para>
///
/// <para><b>No compatibility fallbacks.</b> The four quirk retries in the chat-completions client
/// are all Chat-Completions-only: <c>max_tokens</c>→<c>max_completion_tokens</c> and
/// <c>stream_options</c> cannot occur here (those fields don't exist in this dialect), and
/// <c>text.format</c>/<c>strict</c> are not optional extras to degrade away from. A Responses
/// endpoint that rejects them fails loudly rather than silently sending something else.</para>
/// </summary>
public sealed class OpenAiResponsesLlmClient : ILlmClient
{
    private readonly LlmClientConfig _config;
    private readonly LlmHttpTransport _transport;

    public OpenAiResponsesLlmClient(
        IHttpClientFactory httpClientFactory,
        LlmClientConfig config,
        ILogger<OpenAiResponsesLlmClient> logger)
    {
        _config = config;
        _transport = new LlmHttpTransport(httpClientFactory, config, logger);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var body = BuildBody(request, stream: false);

        using var timeoutCts = _transport.CreateTimeoutScope(ct);
        using var resp = await _transport.SendAsync(
            body, HttpCompletionOption.ResponseContentRead, timeoutCts.Token, ct);
        using var doc = await LlmHttpTransport.ReadJsonAsync(resp, ct);

        return ParseResponse(doc.RootElement);
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var timeoutCts = _transport.CreateTimeoutScope(ct);
        var token = timeoutCts.Token;

        using var resp = await _transport.SendAsync(
            BuildBody(request, stream: true), HttpCompletionOption.ResponseHeadersRead, token, ct);

        string? model = null;
        string? status = null;
        string? incompleteReason = null;
        int? promptTokens = null, completionTokens = null;
        var toolAcc = new Dictionary<int, ToolCallAccumulator>();
        // Same semantics as the chat-completions client: the clock starts at the first real output
        // (text delta or tool-call activity), never at connect or prompt prefill, so GenerationMs
        // stays comparable across both dialects.
        long? firstOutputTs = null;

        await foreach (var data in _transport.ReadSseDataAsync(resp, token, ct))
        {
            // Parsing happens outside a yield block (yield is not allowed inside try/catch).
            string? delta = null;
            var sawOutput = false;
            LlmException? failure = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : null;

                switch (type)
                {
                    case "response.output_text.delta":
                        if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                            delta = d.GetString();
                        break;

                    case "response.output_item.added":
                        // Opens a tool-call slot: the call_id and name arrive here, the arguments
                        // stream in afterwards as function_call_arguments deltas.
                        if (root.TryGetProperty("item", out var added) && added.ValueKind == JsonValueKind.Object
                            && ReadString(added, "type") == "function_call")
                        {
                            var slot = Slot(toolAcc, ReadInt(root, "output_index") ?? toolAcc.Count);
                            slot.Id = ReadString(added, "call_id") ?? slot.Id;
                            slot.Name = ReadString(added, "name") ?? slot.Name;
                            sawOutput = true;
                        }
                        break;

                    case "response.function_call_arguments.delta":
                        {
                            var slot = Slot(toolAcc, ReadInt(root, "output_index") ?? Math.Max(0, toolAcc.Count - 1));
                            if (root.TryGetProperty("delta", out var ad) && ad.ValueKind == JsonValueKind.String)
                                slot.Arguments.Append(ad.GetString());
                            sawOutput = true;
                        }
                        break;

                    case "response.output_item.done":
                        // Backfill: servers that never emitted argument deltas still deliver the
                        // finished item here. Against OpenAI this can't misfire — the accumulated
                        // arguments are already non-empty by then.
                        if (root.TryGetProperty("item", out var done) && done.ValueKind == JsonValueKind.Object
                            && ReadString(done, "type") == "function_call")
                        {
                            var slot = Slot(toolAcc, ReadInt(root, "output_index") ?? Math.Max(0, toolAcc.Count - 1));
                            slot.Id = ReadString(done, "call_id") ?? slot.Id;
                            slot.Name = ReadString(done, "name") ?? slot.Name;
                            if (slot.Arguments.Length == 0 && ReadString(done, "arguments") is { } args)
                                slot.Arguments.Append(args);
                            sawOutput = true;
                        }
                        break;

                    case "response.completed":
                    case "response.incomplete":
                        if (root.TryGetProperty("response", out var completed) && completed.ValueKind == JsonValueKind.Object)
                        {
                            model = ReadString(completed, "model") ?? model;
                            status = ReadString(completed, "status") ?? status;
                            incompleteReason = ReadIncompleteReason(completed) ?? incompleteReason;
                            (promptTokens, completionTokens) = ReadUsage(completed, promptTokens, completionTokens);
                        }
                        break;

                    case "response.failed":
                        failure = BuildFailure(root);
                        break;
                }
            }
            catch (JsonException)
            {
                delta = null; // skip this one malformed chunk and keep going
            }

            if (failure is not null) throw failure;

            if (!string.IsNullOrEmpty(delta) || sawOutput)
                firstOutputTs ??= Stopwatch.GetTimestamp();

            if (!string.IsNullOrEmpty(delta))
                yield return new LlmStreamEvent(delta, Model: model);
        }

        var finalToolCalls = ToolCallAccumulator.Materialize(toolAcc);
        var generationMs = firstOutputTs is long outputStart
            ? (int)Stopwatch.GetElapsedTime(outputStart).TotalMilliseconds
            : (int?)null;

        yield return new LlmStreamEvent(null, Done: true, Model: model ?? _config.Model,
            PromptTokens: promptTokens, CompletionTokens: completionTokens,
            ToolCalls: finalToolCalls,
            FinishReason: MapFinishReason(status, incompleteReason, finalToolCalls is not null),
            GenerationMs: generationMs);
    }

    /// <summary>Builds the Responses request body. Deliberately no <c>max_tokens</c>, no
    /// <c>response_format</c>, no <c>stream_options</c> — those belong to the other dialect.</summary>
    private Dictionary<string, object?> BuildBody(LlmRequest request, bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _config.Model,
            ["max_output_tokens"] = _config.MaxTokens,
            ["input"] = BuildInput(request),
            // The Responses API defaults to store: true, which parks every prompt (workflow
            // definitions, DB schemas, source excerpts) in the org's OpenAI dashboard for 30 days.
            // Chat completions store nothing by default, so this only keeps the two dialects at
            // par — switching the endpoint must not silently change where NodePilot's data lands.
            ["store"] = false,
        };
        if (_config.Temperature is double temperature)
            body["temperature"] = temperature;
        if (request.JsonMode)
            body["text"] = new { format = new { type = "json_object" } };
        if (stream)
            body["stream"] = true;
        AppendTools(body, request);
        return body;
    }

    /// <summary>
    /// Builds the <c>input</c> array: <c>[system, ...Conversation]</c> or <c>[system, user]</c>.
    /// Unlike the chat-completions <c>messages</c> array this is not a 1:1 mapping — an assistant
    /// turn that requested tools expands into its optional text message <i>plus</i> one
    /// <c>function_call</c> item per call, so this flattens rather than projects.
    /// </summary>
    private static List<object> BuildInput(LlmRequest request)
    {
        var input = new List<object>(1 + (request.Conversation?.Count ?? 1))
        {
            new { role = "system", content = request.SystemPrompt },
        };
        if (request.Conversation is { Count: > 0 } turns)
            foreach (var turn in turns)
                AppendTurn(input, turn);
        else
            input.Add(new { role = "user", content = request.UserPrompt });
        return input;
    }

    private static void AppendTurn(List<object> input, LlmMessage turn)
    {
        if (string.Equals(turn.Role, "tool", StringComparison.Ordinal))
        {
            input.Add(new { type = "function_call_output", call_id = turn.ToolCallId, output = turn.Content });
            return;
        }

        if (turn.ToolCalls is { Count: > 0 } calls)
        {
            if (!string.IsNullOrEmpty(turn.Content))
                input.Add(new { role = turn.Role, content = turn.Content });
            foreach (var tc in calls)
                input.Add(new { type = "function_call", call_id = tc.Id, name = tc.Name, arguments = tc.ArgumentsJson });
            return;
        }

        input.Add(new { role = turn.Role, content = turn.Content });
    }

    /// <summary>Appends the flat Responses tool schema — no nested <c>function</c> object, unlike chat completions.</summary>
    private static void AppendTools(Dictionary<string, object?> body, LlmRequest request)
    {
        if (request.Tools is not { Count: > 0 } tools) return;
        body["tools"] = tools.Select(t =>
        {
            var tool = new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters,
            };
            if (t.Strict) tool["strict"] = true;
            return tool;
        }).ToArray();
        body["tool_choice"] = request.ToolChoice ?? "auto";
    }

    /// <summary>Parses a non-streaming Responses body: text from <c>output[].message</c>, calls from
    /// <c>output[].function_call</c>, usage from <c>usage.{input,output,total}_tokens</c>.</summary>
    private LlmResponse ParseResponse(JsonElement root)
    {
        // A failed run can still come back as HTTP 200 with status: "failed" — surface it as an
        // upstream error rather than as an empty answer.
        if (ReadString(root, "status") == "failed")
            throw BuildFailure(root, isEnvelope: true);

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "LLM-Antwort enthielt kein 'output'-Array.");
        }

        var text = new StringBuilder();
        var toolCalls = new List<LlmToolCall>();
        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            switch (ReadString(item, "type"))
            {
                case "message":
                    if (item.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
                        foreach (var part in parts.EnumerateArray())
                            if (part.ValueKind == JsonValueKind.Object
                                && ReadString(part, "type") == "output_text"
                                && ReadString(part, "text") is { } chunk)
                                text.Append(chunk);
                    break;

                case "function_call":
                    var name = ReadString(item, "name") ?? "";
                    if (name.Length > 0)
                        toolCalls.Add(new LlmToolCall(
                            ReadString(item, "call_id") ?? "", name, ReadString(item, "arguments") ?? ""));
                    break;

                // "reasoning" and any future item type carry no answer text — ignored on purpose.
            }
        }

        if (text.Length == 0 && toolCalls.Count == 0)
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "LLM-Antwort enthielt weder 'output_text' noch einen 'function_call'.");
        }

        var (promptTokens, completionTokens) = ReadUsage(root, null, null);
        var totalTokens = root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            ? ReadInt(usage, "total_tokens") : null;

        return new LlmResponse(
            text.ToString(),
            ReadString(root, "model") ?? _config.Model,
            promptTokens, completionTokens, totalTokens,
            toolCalls.Count > 0 ? toolCalls : null,
            MapFinishReason(ReadString(root, "status"), ReadIncompleteReason(root), toolCalls.Count > 0));
    }

    /// <summary>
    /// Maps the Responses <c>status</c> onto the chat-completions <c>finish_reason</c> vocabulary
    /// the rest of NodePilot already speaks. Diagnostic only — the chat assistant branches on the
    /// presence of tool calls, not on this string.
    /// </summary>
    private static string? MapFinishReason(string? status, string? incompleteReason, bool hasToolCalls) => (status, hasToolCalls) switch
    {
        (_, true) => "tool_calls",
        ("incomplete", _) when incompleteReason == "max_output_tokens" => "length",
        ("completed", _) => "stop",
        _ => status,
    };

    private static LlmException BuildFailure(JsonElement root, bool isEnvelope = false)
    {
        var response = isEnvelope
            ? root
            : root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object ? r : root;
        var message = response.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
            ? ReadString(err, "message")
            : null;
        return new LlmException(LlmErrorKind.UpstreamError,
            "LLM-Endpoint hat den Lauf als fehlgeschlagen beendet.",
            bodyExcerpt: message ?? "<no error message>");
    }

    private static string? ReadIncompleteReason(JsonElement response) =>
        response.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? ReadString(details, "reason")
            : null;

    private static (int?, int?) ReadUsage(JsonElement response, int? promptTokens, int? completionTokens)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return (promptTokens, completionTokens);
        return (ReadInt(usage, "input_tokens") ?? promptTokens, ReadInt(usage, "output_tokens") ?? completionTokens);
    }

    private static ToolCallAccumulator Slot(Dictionary<int, ToolCallAccumulator> acc, int index)
    {
        if (!acc.TryGetValue(index, out var slot)) { slot = new ToolCallAccumulator(); acc[index] = slot; }
        return slot;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32() : null;

}

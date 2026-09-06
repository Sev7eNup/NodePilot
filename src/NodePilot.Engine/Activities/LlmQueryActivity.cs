using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Core.Interfaces;

namespace NodePilot.Engine.Activities;

/// <summary>
/// Engine-local activity that sends a prompt to an OpenAI-compatible endpoint and returns the
/// text answer, using chat completions or OpenAI's Responses API depending on the base URL's
/// path (see <c>LlmEndpointGuard.ResolveEndpoint</c>). Uses the global <c>Llm:*</c> endpoint by
/// default; per-node config can override <c>baseUrl</c>/<c>model</c>/<c>apiKey</c>/
/// <c>maxTokens</c>/<c>temperature</c>/<c>timeoutSeconds</c>. Gated by <c>Llm:Enabled</c> and
/// shares transport and SSRF guarding with the AI assistant via <see cref="ILlmClientFactory"/>.
/// </summary>
public sealed class LlmQueryActivity : IActivityExecutor
{
    private readonly ILlmClientFactory _clientFactory;
    private readonly IOptionsMonitor<LlmOptions> _options;

    public string ActivityType => "llmQuery";

    public LlmQueryActivity(ILlmClientFactory clientFactory, IOptionsMonitor<LlmOptions> options)
    {
        _clientFactory = clientFactory;
        _options = options;
    }

    public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        => ActivityExecution.RunAsync(async () =>
        {
            // Llm:Enabled gates all LLM egress, even for a node with its own endpoint. Reads the
            // live value so toggling it in Settings takes effect without a restart.
            if (!_options.CurrentValue.Enabled)
                return Fail("AI features are disabled (Llm:Enabled=false). Enable them in Settings → AI to use the LLM Query activity.");

            var prompt = config.GetStringOrNull("prompt");
            if (string.IsNullOrWhiteSpace(prompt))
                return Fail("LLM Query: 'prompt' is required.");

            // An empty systemPrompt passes through with no synthetic default. jsonMode only sets
            // response_format; the activity does not parse or validate the answer as JSON.
            var systemPrompt = config.GetStringOrNull("systemPrompt") ?? string.Empty;
            var jsonMode = config.GetBool("jsonMode", false);
            var model = NullIfBlank(config.GetStringOrNull("model"));
            var apiKey = NullIfBlank(config.GetStringOrNull("apiKey"));

            // Per-node overrides are validated against resolved values (StepRunner already
            // substituted any {{…}} templates). baseUrl is additionally guarded in the factory.
            var baseUrl = NullIfBlank(config.GetStringOrNull("baseUrl"));
            if (baseUrl is not null)
            {
                try { LlmEndpointGuard.NormalizeAndValidateBaseUrl(baseUrl); }
                catch (LlmException ex) { return Fail(ex.Message); }
            }

            if (!TryReadPositiveInt(config, "maxTokens", out var maxTokens, out var maxTokensError))
                return Fail(maxTokensError!);
            if (!TryReadPositiveInt(config, "timeoutSeconds", out var timeoutSeconds, out var timeoutError,
                    zeroMeansUnset: true))
                return Fail(timeoutError!);
            if (!TryReadTemperature(config, out var temperature, out var temperatureError))
                return Fail(temperatureError!);

            var connection = new LlmConnection(
                BaseUrl: baseUrl,
                ApiKey: apiKey,
                Model: model,
                MaxTokens: maxTokens,
                Temperature: temperature,
                TimeoutSeconds: timeoutSeconds);

            ILlmClient client;
            try { client = _clientFactory.Create(connection); }
            catch (LlmException ex) { return Fail(ex.Message); }

            try
            {
                var response = await client.CompleteAsync(new LlmRequest(systemPrompt, prompt!, JsonMode: jsonMode), ct);
                return new ActivityResult
                {
                    Success = true,
                    Output = response.Content,
                    // Token/finishReason keys are always present (empty when the server omitted
                    // usage)
                    // so downstream {{step.param.totalTokens}} etc. never fail to resolve. The
                    // catalog
                    // "number" type is a UI/databus hint only; runtime values are always strings.
                    OutputParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["model"] = response.Model ?? string.Empty,
                        ["promptTokens"] = IntOrEmpty(response.PromptTokens),
                        ["completionTokens"] = IntOrEmpty(response.CompletionTokens),
                        ["totalTokens"] = IntOrEmpty(response.TotalTokens),
                        ["finishReason"] = response.FinishReason ?? string.Empty,
                    },
                };
            }
            catch (LlmException ex)
            {
                var status = ex.HttpStatus is int s ? $" (HTTP {s})" : string.Empty;
                var body = string.IsNullOrEmpty(ex.BodyExcerpt) ? string.Empty : $"\n{ex.BodyExcerpt}";
                return Fail($"LLM Query failed [{ex.Kind}]{status}: {ex.Message}{body}");
            }
        });

    private static ActivityResult Fail(string message) => new() { Success = false, ErrorOutput = message };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string IntOrEmpty(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Reads an optional positive override.
    /// </summary>
    /// <param name="zeroMeansUnset">
    /// Set for <c>timeoutSeconds</c> only. The shared Timeout field writes a literal 0 for an
    /// emptied input and its hint documents 0 as "no timeout", so rejecting it failed every run of
    /// a node whose timeout the author had simply cleared. Other keys keep 0 as an error, where it
    /// is a genuine misconfiguration rather than an empty field.
    /// </param>
    private static bool TryReadPositiveInt(
        JsonElement config, string key, out int? value, out string? error, bool zeroMeansUnset = false)
    {
        value = null;
        error = null;
        if (!config.TryGetProperty(key, out var el) || el.ValueKind == JsonValueKind.Null)
            return true;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v)
            || v < 0 || (v == 0 && !zeroMeansUnset))
        {
            error = $"LLM Query: '{key}' must be a positive integer.";
            return false;
        }
        if (v == 0) return true;
        value = v;
        return true;
    }

    private static bool TryReadTemperature(JsonElement config, out double? value, out string? error)
    {
        value = null;
        error = null;
        if (!config.TryGetProperty("temperature", out var el) || el.ValueKind == JsonValueKind.Null)
            return true;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var v) || v < 0 || v > 2)
        {
            error = "LLM Query: 'temperature' must be a number between 0 and 2.";
            return false;
        }
        value = v;
        return true;
    }
}

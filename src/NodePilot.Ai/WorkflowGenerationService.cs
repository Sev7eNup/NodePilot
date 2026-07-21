using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Ai;

/// <summary>
/// Orchestrates a workflow-generation round trip: system prompt + few-shot example + user
/// prompt → JSON-mode call → tolerant parsing → an optional single retry with a "reply with
/// ONLY JSON" hint → schema validation. If both attempts fail, the method throws an
/// <see cref="LlmException"/> with <see cref="LlmErrorKind.MalformedResponse"/>, which the
/// controller maps to a 502.
/// </summary>
public sealed class WorkflowGenerationService
{
    private const long MaxDefinitionBytes = 5L * 1024 * 1024;

    private readonly ILlmClient _llm;
    private readonly PromptCatalog _prompts;

    public WorkflowGenerationService(ILlmClient llm, PromptCatalog prompts)
    {
        _llm = llm;
        _prompts = prompts;
    }

    public async Task<GenerateWorkflowResponse> GenerateAsync(
        GenerateWorkflowRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var systemPrompt = _prompts.WorkflowSystemPrompt
                           + "\n\n## Reference example workflow (mimic this structure)\n\n```json\n"
                           + _prompts.WorkflowExampleJson
                           + "\n```\n\n## Output envelope\n\n"
                           + "Reply with a single JSON object of shape:\n"
                           + "```\n"
                           + "{\n"
                           + "  \"name\": \"<short workflow name, 3-60 chars>\",\n"
                           + "  \"description\": \"<one-sentence purpose>\",\n"
                           + "  \"definition\": { \"nodes\": [...], \"edges\": [...] }\n"
                           + "}\n"
                           + "```\n"
                           + "No markdown fences, no commentary. The `definition` object follows the same\n"
                           + "schema as the reference example.";

        var userPromptInitial = BuildUserPrompt(request.Prompt, retryReason: null);

        var (envelope, retried, modelEcho, promptTokens, completionTokens, totalTokens) = await CallWithRetry(
            systemPrompt, userPromptInitial, request.Prompt, ct);

        // The envelope holds "definition" as a JsonElement — we serialize it back to compact
        // JSON so the frontend can pass it straight through as CreateWorkflowRequest.DefinitionJson.
        var definitionJson = JsonSerializer.Serialize(envelope.Definition);
        if (Encoding.UTF8.GetByteCount(definitionJson) > MaxDefinitionBytes)
        {
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "Generated workflow definition exceeds the 5 MiB cap.");
        }

        ValidateDefinition(envelope.Definition);

        sw.Stop();
        return new GenerateWorkflowResponse(
            DefinitionJson: definitionJson,
            SuggestedName: envelope.Name,
            SuggestedDescription: envelope.Description,
            NodeCount: CountArray(envelope.Definition, "nodes"),
            EdgeCount: CountArray(envelope.Definition, "edges"),
            Retried: retried,
            DurationMs: (int)sw.ElapsedMilliseconds,
            Model: modelEcho,
            PromptTokens: promptTokens,
            CompletionTokens: completionTokens,
            TotalTokens: totalTokens);
    }

    private async Task<(WorkflowEnvelope env, bool retried, string model, int? promptTokens, int? completionTokens, int? totalTokens)> CallWithRetry(
        string systemPrompt, string userPrompt, string originalUserPrompt, CancellationToken ct)
    {
        Exception? lastParseError = null;
        string? lastRawResponse = null;
        int? accPrompt = null, accCompletion = null, accTotal = null;

        for (var attempt = 0; attempt <= LlmOptions.MaxJsonRetries; attempt++)
        {
            var promptForThisCall = attempt == 0
                ? userPrompt
                : BuildUserPrompt(originalUserPrompt, retryReason: lastRawResponse);

            var resp = await _llm.CompleteAsync(
                new LlmRequest(systemPrompt, promptForThisCall, JsonMode: true), ct);

            lastRawResponse = resp.Content;

            // Accumulate token counts across retry attempts.
            if (resp.PromptTokens.HasValue)
                accPrompt = (accPrompt ?? 0) + resp.PromptTokens.Value;
            if (resp.CompletionTokens.HasValue)
                accCompletion = (accCompletion ?? 0) + resp.CompletionTokens.Value;
            if (resp.TotalTokens.HasValue)
                accTotal = (accTotal ?? 0) + resp.TotalTokens.Value;

            try
            {
                var env = ParseEnvelope(resp.Content);
                return (env, retried: attempt > 0, model: resp.Model, accPrompt, accCompletion, accTotal);
            }
            catch (Exception ex)
            {
                lastParseError = ex;
            }
        }

        throw new LlmException(LlmErrorKind.MalformedResponse,
            $"LLM did not return valid workflow JSON after {LlmOptions.MaxJsonRetries + 1} attempt(s).",
            inner: lastParseError);
    }

    private static string BuildUserPrompt(string userPrompt, string? retryReason)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## User request");
        sb.AppendLine(userPrompt);
        if (!string.IsNullOrWhiteSpace(retryReason))
        {
            sb.AppendLine();
            sb.AppendLine("## Important — retry");
            sb.AppendLine("Your previous response could not be parsed as the required JSON envelope.");
            sb.AppendLine("Reply now with ONLY the JSON object — no markdown fences, no prose, no commentary.");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Tolerant of code fences and leading prose. Finds the outermost balanced JSON object in
    /// the response string, parses it, and maps it onto the envelope shape.
    /// </summary>
    internal static WorkflowEnvelope ParseEnvelope(string raw)
    {
        var jsonText = WorkflowDefinitionJsonHelper.ExtractJsonObject(raw);
        if (jsonText is null)
            throw new InvalidOperationException("No JSON object found in LLM response.");

        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("LLM response is not a JSON object.");

        if (!root.TryGetProperty("definition", out var def) || def.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Envelope is missing 'definition' object.");

        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() ?? "Generated Workflow"
            : "Generated Workflow";

        var desc = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;

        return new WorkflowEnvelope(name.Trim(), desc?.Trim(), def.Clone());
    }

    internal static void ValidateDefinition(JsonElement def)
    {
        if (!def.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "Generated definition is missing 'nodes' array.");
        if (!def.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array)
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "Generated definition is missing 'edges' array.");

        if (nodes.GetArrayLength() == 0)
            throw new LlmException(LlmErrorKind.MalformedResponse,
                "Generated definition has zero nodes - at least one trigger is required.");

        var validation = WorkflowDefinitionStructuralValidator.Validate(def);
        if (!validation.IsValid)
        {
            var error = validation.Error ?? "invalid definition";
            if (error.StartsWith("duplicate", StringComparison.Ordinal))
                error = char.ToUpperInvariant(error[0]) + error[1..];
            throw new LlmException(LlmErrorKind.MalformedResponse,
                $"Generated definition is structurally invalid: {error}");
        }

    }

    private static int CountArray(JsonElement def, string property)
        => def.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.GetArrayLength() : 0;

    internal sealed record WorkflowEnvelope(string Name, string? Description, JsonElement Definition);
}

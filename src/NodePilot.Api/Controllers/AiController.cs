using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NodePilot.Api.Ai;
using NodePilot.Ai;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Api.Telemetry;
using NodePilot.Core.Telemetry;

namespace NodePilot.Api.Controllers;

/// <summary>
/// AI assistant that helps author workflows: PowerShell generation in the script editor and
/// full workflow generation from a free-text prompt. Both endpoints call an OpenAI-compatible
/// LLM endpoint (OpenAI Cloud, Ollama, LM Studio, …) — configuration lives under
/// <c>Llm:*</c>, with the master on/off switch <c>Llm:Enabled</c> (default false → 503).
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize(Roles = "Admin,Operator")]
[EnableRateLimiting("ai-generate")]
public sealed class AiController : ControllerBase
{
    private readonly IOptionsMonitor<LlmOptions> _options;
    private readonly ScriptGenerationService _scriptGen;
    private readonly WorkflowGenerationService _workflowGen;
    private readonly IAuditWriter _audit;
    private readonly ILogger<AiController> _logger;

    public AiController(
        IOptionsMonitor<LlmOptions> options,
        ScriptGenerationService scriptGen,
        WorkflowGenerationService workflowGen,
        IAuditWriter audit,
        ILogger<AiController> logger)
    {
        _options = options;
        _scriptGen = scriptGen;
        _workflowGen = workflowGen;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Generates PowerShell code for a <c>runScript</c> activity and <b>streams</b> it as SSE
    /// (events <c>delta</c>/<c>done</c>/<c>error</c>) so the editor can type the script out live.
    /// The request body carries the user's prompt plus the upstream variables available at that
    /// step (capped). Markdown code fences are stripped as the stream comes in.
    /// </summary>
    [HttpPost("generate-script")]
    public async Task<IActionResult> GenerateScript(GenerateScriptRequest request, CancellationToken ct)
    {
        if (!_options.CurrentValue.Enabled)
            return ServiceUnavailable("LLM_DISABLED",
                "AI assistant is disabled. Set Llm:Enabled=true in configuration.");

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { code = "PROMPT_EMPTY", message = "Prompt must not be empty." });

        await using var en = _scriptGen.StreamAsync(request, ct).GetAsyncEnumerator(ct);

        bool hasFirst;
        try
        {
            hasFirst = await en.MoveNextAsync();
        }
        catch (LlmException ex)
        {
            RecordScriptError(ex);
            return MapLlmException(ex);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            RecordScriptResult("cancelled");
            return new EmptyResult();
        }

        await using var sse = SseResponseWriter.Begin(Response);

        var responseChars = 0;
        var model = "unknown";
        var durationMs = 0;
        int? promptTokens = null, completionTokens = null;

        async Task Write(ScriptStreamEvent e)
        {
            switch (e)
            {
                case ScriptStreamEvent.DeltaEvent d:
                    responseChars += d.Text.Length;
                    await sse.WriteAsync("delta", new { text = d.Text }, ct);
                    break;
                case ScriptStreamEvent.DoneEvent done:
                    model = done.Model;
                    durationMs = done.DurationMs;
                    promptTokens = done.PromptTokens;
                    completionTokens = done.CompletionTokens;
                    await sse.WriteAsync("done", new { model = done.Model, durationMs = done.DurationMs }, ct);
                    break;
            }
        }

        try
        {
            if (hasFirst) await Write(en.Current);
            while (await en.MoveNextAsync()) await Write(en.Current);
        }
        catch (LlmException ex)
        {
            RecordScriptError(ex);
            await sse.WriteAsync("error", new { code = LlmErrorCodes.For(ex), message = ex.Message }, CancellationToken.None);
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            RecordScriptResult("cancelled");
            await ScriptAuditAsync(model, durationMs, responseChars, request, cancelled: true);
            return new EmptyResult();
        }

        RecordScriptSuccess(model, durationMs, promptTokens, completionTokens);
        await ScriptAuditAsync(model, durationMs, responseChars, request, cancelled: false, ct);
        return new EmptyResult();
    }

    private static void RecordScriptResult(string result) =>
        ApiMetrics.LlmCalls.Add(1, new(TelemetryConstants.Attributes.LlmKind, "script"), new("result", result));

    private void RecordScriptError(LlmException ex)
    {
        RecordScriptResult("error");
        ApiMetrics.LlmErrors.Add(1,
            new(TelemetryConstants.Attributes.LlmKind, "script"),
            new(TelemetryConstants.Attributes.LlmErrorKind, ex.Kind.ToString()));
        _logger.LogWarning(ex, "LLM script stream failed: {Kind}", ex.Kind);
    }

    private static void RecordScriptSuccess(string model, int durationMs, int? promptTokens, int? completionTokens)
    {
        RecordScriptResult("success");
        ApiMetrics.LlmCallDuration.Record(durationMs,
            new(TelemetryConstants.Attributes.LlmKind, "script"),
            new(TelemetryConstants.Attributes.LlmModel, model));
        if (promptTokens.HasValue)
            ApiMetrics.LlmTokens.Add(promptTokens.Value,
                new(TelemetryConstants.Attributes.LlmKind, "script"),
                new(TelemetryConstants.Attributes.LlmModel, model), new("token_type", "prompt"));
        if (completionTokens.HasValue)
            ApiMetrics.LlmTokens.Add(completionTokens.Value,
                new(TelemetryConstants.Attributes.LlmKind, "script"),
                new(TelemetryConstants.Attributes.LlmModel, model), new("token_type", "completion"));
    }

    private Task ScriptAuditAsync(string model, int durationMs, int responseChars,
        GenerateScriptRequest request, bool cancelled, CancellationToken ct = default) =>
        _audit.LogAsync(AuditActions.AiScriptGenerated, "Workflow", request.WorkflowId,
            AuditDetails.Json(
                ("model", model),
                ("promptChars", request.Prompt.Length),
                ("upstreamVarCount", request.UpstreamVariables.Count),
                ("responseChars", responseChars),
                ("durationMs", durationMs),
                ("cancelled", cancelled),
                ("stepId", request.StepId)),
            ct);

    /// <summary>
    /// Generates a complete workflow as a JSON definition from a free-text user prompt.
    /// The response contains an already-validated <c>DefinitionJson</c> plus suggested
    /// name and description — the frontend shows this in a preview dialog, the user
    /// confirms, and it is saved via the existing <c>POST /api/workflows</c> endpoint.
    /// </summary>
    [HttpPost("generate-workflow")]
    public async Task<ActionResult<GenerateWorkflowResponse>> GenerateWorkflow(
        GenerateWorkflowRequest request, CancellationToken ct)
    {
        if (!_options.CurrentValue.Enabled)
            return ServiceUnavailable("LLM_DISABLED",
                "AI assistant is disabled. Set Llm:Enabled=true in configuration.");

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { code = "PROMPT_EMPTY", message = "Prompt must not be empty." });

        try
        {
            var resp = await _workflowGen.GenerateAsync(request, ct);

            ApiMetrics.LlmCalls.Add(1,
                new(TelemetryConstants.Attributes.LlmKind, "workflow"),
                new("result", "success"));
            ApiMetrics.LlmCallDuration.Record(resp.DurationMs,
                new(TelemetryConstants.Attributes.LlmKind, "workflow"),
                new(TelemetryConstants.Attributes.LlmModel, resp.Model));
            if (resp.PromptTokens.HasValue)
                ApiMetrics.LlmTokens.Add(resp.PromptTokens.Value,
                    new(TelemetryConstants.Attributes.LlmKind, "workflow"),
                    new(TelemetryConstants.Attributes.LlmModel, resp.Model),
                    new("token_type", "prompt"));
            if (resp.CompletionTokens.HasValue)
                ApiMetrics.LlmTokens.Add(resp.CompletionTokens.Value,
                    new(TelemetryConstants.Attributes.LlmKind, "workflow"),
                    new(TelemetryConstants.Attributes.LlmModel, resp.Model),
                    new("token_type", "completion"));

            await _audit.LogAsync(AuditActions.AiWorkflowGenerated, "Workflow", null,
                AuditDetails.Json(
                    ("model", resp.Model),
                    ("promptChars", request.Prompt.Length),
                    ("nodeCount", resp.NodeCount),
                    ("edgeCount", resp.EdgeCount),
                    ("retried", resp.Retried),
                    ("durationMs", resp.DurationMs)),
                ct);

            return Ok(resp);
        }
        catch (LlmException ex)
        {
            ApiMetrics.LlmCalls.Add(1,
                new(TelemetryConstants.Attributes.LlmKind, "workflow"),
                new("result", "error"));
            ApiMetrics.LlmErrors.Add(1,
                new(TelemetryConstants.Attributes.LlmKind, "workflow"),
                new(TelemetryConstants.Attributes.LlmErrorKind, ex.Kind.ToString()));
            return MapLlmException(ex);
        }
    }

    /// <summary>
    /// Maps <see cref="LlmException"/> kinds to the appropriate HTTP response.
    /// Problems on our side reaching the endpoint (unreachable, timeout, bad API key) → 503;
    /// weaknesses in the upstream response (malformed body, invalid JSON) → 502.
    ///
    /// <see cref="LlmException.BodyExcerpt"/> is included in the response for
    /// <c>UpstreamError</c> so the user sees the real upstream error message in the frontend
    /// (e.g. "context_length_exceeded" or "model does not support response_format")
    /// instead of just "LLM endpoint returned HTTP 400".
    /// </summary>
    private ActionResult MapLlmException(LlmException ex)
    {
        _logger.LogWarning(ex, "LLM call failed: {Kind}", ex.Kind);
        return ex.Kind switch
        {
            LlmErrorKind.Unreachable => ServiceUnavailable("LLM_UNREACHABLE", ex.Message),
            LlmErrorKind.Timeout => ServiceUnavailable("LLM_TIMEOUT", ex.Message),
            LlmErrorKind.Unauthorized => ServiceUnavailable("LLM_UNAUTHORIZED",
                "LLM endpoint rejected the configured API key. Check Llm:ApiKey."),
            LlmErrorKind.RateLimited => ServiceUnavailable("LLM_RATE_LIMITED",
                "LLM endpoint rate-limited the request. Try again shortly."),
            LlmErrorKind.MalformedResponse => BadGateway("LLM_MALFORMED_RESPONSE", ex.Message, ex.BodyExcerpt),
            LlmErrorKind.UpstreamError => BadGateway("LLM_UPSTREAM_ERROR",
                $"LLM endpoint returned HTTP {ex.HttpStatus}.", ex.BodyExcerpt),
            _ => StatusCode(StatusCodes.Status500InternalServerError,
                new { code = "LLM_UNKNOWN", message = ex.Message }),
        };
    }

    private ObjectResult ServiceUnavailable(string code, string message)
        => StatusCode(StatusCodes.Status503ServiceUnavailable, new { code, message });

    private ObjectResult BadGateway(string code, string message, string? bodyExcerpt = null)
        => StatusCode(StatusCodes.Status502BadGateway,
            bodyExcerpt is null
                ? (object)new { code, message }
                : new { code, message, bodyExcerpt });
}

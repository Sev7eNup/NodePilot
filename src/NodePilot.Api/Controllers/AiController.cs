using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NodePilot.Api.Ai;
using NodePilot.Ai;
using NodePilot.Api.Configuration;
using NodePilot.Core.Audit;

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
        if (LlmAvailability.Unavailable(this, _options.CurrentValue) is { } gate) return gate;

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
            return this.MapLlmException(_logger, ex, "LLM call");
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

    private const string ScriptKind = "script";
    private const string WorkflowKind = "workflow";

    private static void RecordScriptResult(string result) =>
        AiStreamSupport.RecordResult(ScriptKind, result);

    private void RecordScriptError(LlmException ex)
    {
        AiStreamSupport.RecordError(ScriptKind, ex);
        _logger.LogWarning(ex, "LLM script stream failed: {Kind}", ex.Kind);
    }

    private static void RecordScriptSuccess(string model, int durationMs, int? promptTokens, int? completionTokens) =>
        AiStreamSupport.RecordSuccess(ScriptKind, model, durationMs, promptTokens, completionTokens);

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
        if (LlmAvailability.Unavailable(this, _options.CurrentValue) is { } gate) return gate;

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { code = "PROMPT_EMPTY", message = "Prompt must not be empty." });

        try
        {
            var resp = await _workflowGen.GenerateAsync(request, ct);

            AiStreamSupport.RecordSuccess(WorkflowKind, resp.Model, resp.DurationMs,
                resp.PromptTokens, resp.CompletionTokens);

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
            AiStreamSupport.RecordError(WorkflowKind, ex);
            return this.MapLlmException(_logger, ex, "LLM call");
        }
    }
}

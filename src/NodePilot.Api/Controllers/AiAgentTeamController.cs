using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NodePilot.Ai;
using NodePilot.Api.Ai;
using NodePilot.Api.Configuration;
using NodePilot.Core.Audit;

namespace NodePilot.Api.Controllers;

/// <summary>
/// Drafts an agent team from a free-text description. The model proposes members and refers to
/// machines, credentials, skills, workflows and MCP tools by name; the host resolves those names
/// against what the caller may use and returns a patch plus issues for the editor to review.
/// Nothing is saved, published or executed here.
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize(Roles = "Admin,Operator")]
[EnableRateLimiting("ai-generate")]
public sealed class AiAgentTeamController(
    IOptionsMonitor<LlmOptions> options,
    AgentTeamDraftingService drafting,
    AgentTeamInventoryBuilder inventories,
    IAuditWriter audit,
    ILogger<AiAgentTeamController> logger) : ControllerBase
{
    private const string Kind = "agent-team";
    private const int MaxPromptChars = 8_000;

    [HttpPost("generate-agent-team")]
    public async Task<ActionResult<GenerateAgentTeamResponse>> GenerateAgentTeam(
        GenerateAgentTeamRequest request, CancellationToken ct)
    {
        if (LlmAvailability.Unavailable(this, options.CurrentValue) is { } gate) return gate;

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { code = "PROMPT_EMPTY", message = "Prompt must not be empty." });
        if (request.Prompt.Length > MaxPromptChars)
            return BadRequest(new { code = "PROMPT_TOO_LONG", message = $"Prompt exceeds {MaxPromptChars} characters." });

        try
        {
            var inventory = await inventories.BuildAsync(User, ct);
            var result = await drafting.DraftAsync(request.Prompt, inventory.ToPromptInventory(), ct);
            var response = AgentTeamDraftResolver.Resolve(result, request.Prompt, inventory, request.CurrentConfig);

            AiStreamSupport.RecordSuccess(Kind, response.Model, response.DurationMs,
                response.PromptTokens, response.CompletionTokens);

            await audit.LogAsync(AuditActions.AiAgentTeamGenerated, "Workflow", null,
                AuditDetails.Json(
                    ("model", response.Model),
                    ("promptChars", request.Prompt.Length),
                    ("memberCount", response.Patch.Members.Length),
                    ("blockingIssues", response.Issues.Count(i => i.Severity == "blocking")),
                    ("warnings", response.Issues.Count(i => i.Severity == "warning")),
                    ("retried", response.Retried),
                    ("durationMs", response.DurationMs)),
                ct);

            return Ok(response);
        }
        catch (LlmException ex)
        {
            AiStreamSupport.RecordError(Kind, ex);
            return this.MapLlmException(logger, ex, "LLM call");
        }
    }
}

using System.Text.Json;
using NodePilot.Core.Agents;

namespace NodePilot.Api.Ai;

/// <summary>
/// Request body for <c>POST /api/ai/generate-agent-team</c>. <paramref name="CurrentConfig"/> is
/// the node's current config; the server merges the draft onto it and validates the merged
/// result, so the patch it returns is exactly what the editor will apply.
/// </summary>
public sealed record GenerateAgentTeamRequest(string Prompt, JsonElement? CurrentConfig);

/// <summary>What the editor writes into the node config: members, task and parallel limit.</summary>
public sealed record AgentTeamPatch(AgentDefinition[] Members, string Task, int? MaxParallelMembers);

/// <summary>A resource the user can pick to resolve an unresolved machine or credential.</summary>
public sealed record AgentTeamCandidate(Guid Id, string Name, string? Detail);

/// <summary>
/// A finding about the draft. <c>blocking</c> issues stop the editor from applying the draft
/// until the user picks a candidate or drops the request; <c>warning</c> issues are informative.
/// <c>Field</c> is <c>machine</c>, <c>credential</c>, <c>serviceIdentity</c>, <c>tool</c>,
/// <c>skill</c> or <c>team</c>.
/// </summary>
public sealed record AgentTeamIssue(
    string Severity,
    string MemberId,
    string Field,
    string Code,
    string Message,
    string? Reference,
    IReadOnlyList<AgentTeamCandidate> Candidates);

/// <param name="Names">Display names for the ids the patch refers to (machines, credentials,
/// skills, workflows, MCP servers), so the preview shows names instead of GUIDs.</param>
public sealed record GenerateAgentTeamResponse(
    AgentTeamPatch Patch,
    IReadOnlyDictionary<Guid, string> Names,
    IReadOnlyList<AgentTeamIssue> Issues,
    bool Retried,
    int DurationMs,
    string Model,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens);

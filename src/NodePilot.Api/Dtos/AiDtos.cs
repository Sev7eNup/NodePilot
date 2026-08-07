using System.Text.Json.Serialization;

namespace NodePilot.Api.Dtos;

/// <summary>Body of <c>POST /api/ai/chat/applied</c> — reports that an AI-generated suggestion
/// was applied to the canvas (recorded for the audit log).</summary>
public sealed record ChatAppliedRequest([property: JsonRequired] Guid WorkflowId, [property: JsonRequired] int NodeCount, [property: JsonRequired] int EdgeCount);

/// <summary>One AI-related audit entry for a workflow (workflow-scoped, used by the panel's activity view).</summary>
public sealed record AiActivityEntryDto(
    DateTime Timestamp,
    Guid? UserId,
    string? Username,
    string Action,
    string? Details);

/// <summary>
/// Effective knowledge-chat capabilities for the current user (drives nav visibility + source badges).
/// <see cref="Llm"/> is the raw "LLM usable" signal (kill-switch on + active profile resolves) independent
/// of the AiKnowledge master switch — the SPA gates every AI entry point's visibility on it (designer
/// assistant, script-editor generate, AI workflow generation), while <see cref="Enabled"/> keeps gating
/// only the knowledge chat itself.
/// </summary>
public sealed record KnowledgeCapabilitiesDto(bool Enabled, bool Llm, bool Docs, bool Operational, bool SourceCode, bool Db);

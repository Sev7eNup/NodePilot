namespace NodePilot.Api.Dtos;

/// <summary>Body of <c>POST /api/ai/chat/applied</c> — reports that an AI-generated suggestion
/// was applied to the canvas (recorded for the audit log).</summary>
public sealed record ChatAppliedRequest(Guid WorkflowId, int NodeCount, int EdgeCount);

/// <summary>One AI-related audit entry for a workflow (workflow-scoped, used by the panel's activity view).</summary>
public sealed record AiActivityEntryDto(
    DateTime Timestamp,
    Guid? UserId,
    string? Username,
    string Action,
    string? Details);

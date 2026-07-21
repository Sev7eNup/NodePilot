namespace NodePilot.Api.Dtos;

public record ResumeDebugRequest(
    string StepId,
    string Mode,
    Dictionary<string, string>? Overrides);

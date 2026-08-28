namespace NodePilot.Core.Validation;

public sealed record WorkflowDefinitionValidationResult(
    bool IsValid,
    string? Error,
    string? Code = null,
    string? NodeId = null)
{
    public static WorkflowDefinitionValidationResult Valid { get; } = new(true, null);

    public static WorkflowDefinitionValidationResult Invalid(
        string error, string? code = null, string? nodeId = null)
        => new(false, error, code, nodeId);
}

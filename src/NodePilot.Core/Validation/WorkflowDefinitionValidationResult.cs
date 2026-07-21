namespace NodePilot.Core.Validation;

public sealed record WorkflowDefinitionValidationResult(bool IsValid, string? Error)
{
    public static WorkflowDefinitionValidationResult Valid { get; } = new(true, null);

    public static WorkflowDefinitionValidationResult Invalid(string error) => new(false, error);
}

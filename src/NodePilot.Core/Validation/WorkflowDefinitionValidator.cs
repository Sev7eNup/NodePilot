using System.Text.Json;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Core.Validation;

public static class WorkflowDefinitionValidator
{
    public static WorkflowDefinitionValidationResult Validate(JsonElement definition)
        => WorkflowDefinitionStructuralValidator.Validate(definition);
}

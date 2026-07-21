using NodePilot.Api.Dtos;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;

namespace NodePilot.Api.Services;

public interface IWorkflowContractDeriver
{
    WorkflowContractResponse Derive(Workflow workflow);
}

/// <summary>
/// API adapter over the backend-owned workflow-definition contract derivation. The Core
/// module owns runtime semantics; this service only attaches workflow identity and maps DTOs.
/// </summary>
public sealed class WorkflowContractDeriver : IWorkflowContractDeriver
{
    public WorkflowContractResponse Derive(Workflow workflow)
    {
        var contract = WorkflowDefinitionContractDeriver.Derive(workflow.DefinitionJson);
        return new WorkflowContractResponse(
            workflow.Id,
            workflow.Name,
            contract.HasManualTrigger,
            contract.HasReturnData,
            contract.HasMultipleReturnDataNodes,
            contract.Inputs.Select(input => new WorkflowContractInput(
                input.Name,
                input.Type,
                input.Required,
                input.Default,
                input.Description,
                input.HasConflict)).ToList(),
            contract.Outputs.Select(output => new WorkflowContractOutput(
                output.Name,
                output.Source)).ToList());
    }
}

using NodePilot.Core.Interfaces;

namespace NodePilot.Ai.Tests.Knowledge;

/// <summary>In-memory <see cref="IOperationalKnowledgeReader"/> test double — canned results + recorded args.</summary>
public sealed class FakeOperationalKnowledgeReader : IOperationalKnowledgeReader
{
    public List<WorkflowKnowledgeSummary> Workflows { get; } = new();
    public WorkflowKnowledgeDetail? Definition { get; set; }
    public List<ExecutionKnowledgeSummary> Executions { get; } = new();
    public List<MachineKnowledgeSummary> Machines { get; } = new();
    public List<ScheduledFireForecast> ScheduledFires { get; } = new();

    public string? LastIdOrName { get; private set; }
    public string? LastStatus { get; private set; }
    public int LastTake { get; private set; }
    public int LastPerWorkflow { get; private set; }

    public Task<IReadOnlyList<WorkflowKnowledgeSummary>> ListWorkflowsAsync(
        AccessibleFolderSet accessible, string? nameFilter, int take, CancellationToken ct)
    {
        LastTake = take;
        return Task.FromResult<IReadOnlyList<WorkflowKnowledgeSummary>>(Workflows);
    }

    public Task<WorkflowKnowledgeDetail?> GetWorkflowDefinitionAsync(
        AccessibleFolderSet accessible, string idOrName, CancellationToken ct)
    {
        LastIdOrName = idOrName;
        return Task.FromResult(Definition);
    }

    public Task<IReadOnlyList<ExecutionKnowledgeSummary>> ListRecentExecutionsAsync(
        AccessibleFolderSet accessible, string? status, int take, CancellationToken ct)
    {
        LastStatus = status;
        LastTake = take;
        return Task.FromResult<IReadOnlyList<ExecutionKnowledgeSummary>>(Executions);
    }

    public Task<IReadOnlyList<ExecutionKnowledgeSummary>> GetWorkflowExecutionsAsync(
        AccessibleFolderSet accessible, string idOrName, int take, CancellationToken ct)
    {
        LastIdOrName = idOrName;
        LastTake = take;
        return Task.FromResult<IReadOnlyList<ExecutionKnowledgeSummary>>(Executions);
    }

    public Task<IReadOnlyList<MachineKnowledgeSummary>> ListMachinesAsync(int take, CancellationToken ct)
    {
        LastTake = take;
        return Task.FromResult<IReadOnlyList<MachineKnowledgeSummary>>(Machines);
    }

    public Task<IReadOnlyList<ScheduledFireForecast>> ListScheduledFiresAsync(
        AccessibleFolderSet accessible, string? idOrName, int perWorkflow, int maxWorkflows, CancellationToken ct)
    {
        LastIdOrName = idOrName;
        LastPerWorkflow = perWorkflow;
        return Task.FromResult<IReadOnlyList<ScheduledFireForecast>>(ScheduledFires);
    }
}

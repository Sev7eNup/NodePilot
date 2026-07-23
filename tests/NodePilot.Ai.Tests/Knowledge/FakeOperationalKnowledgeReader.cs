using NodePilot.Core.Interfaces;

namespace NodePilot.Ai.Tests.Knowledge;

/// <summary>In-memory <see cref="IOperationalKnowledgeReader"/> test double — canned results + recorded args.</summary>
public sealed class FakeOperationalKnowledgeReader : IOperationalKnowledgeReader
{
    public WorkflowKnowledgeDetail? Definition { get; set; }
    public List<ScheduledFireForecast> ScheduledFires { get; } = new();

    public string? LastIdOrName { get; private set; }
    public int LastPerWorkflow { get; private set; }

    public Task<WorkflowKnowledgeDetail?> GetWorkflowDefinitionAsync(
        AccessibleFolderSet accessible, string idOrName, CancellationToken ct)
    {
        LastIdOrName = idOrName;
        return Task.FromResult(Definition);
    }

    public Task<IReadOnlyList<ScheduledFireForecast>> ListScheduledFiresAsync(
        AccessibleFolderSet accessible, string? idOrName, int perWorkflow, int maxWorkflows, CancellationToken ct)
    {
        LastIdOrName = idOrName;
        LastPerWorkflow = perWorkflow;
        return Task.FromResult<IReadOnlyList<ScheduledFireForecast>>(ScheduledFires);
    }
}

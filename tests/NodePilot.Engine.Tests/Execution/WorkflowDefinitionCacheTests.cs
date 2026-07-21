using FluentAssertions;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

public sealed class WorkflowDefinitionCacheTests : IDisposable
{
    public WorkflowDefinitionCacheTests() => WorkflowDefinitionCache.ClearForTests();

    public void Dispose() => WorkflowDefinitionCache.ClearForTests();

    [Fact]
    public void GetOrCompile_ReusesCompiledGraphForSameWorkflowVersion()
    {
        var updatedAt = DateTime.UtcNow;
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Version = 3,
            UpdatedAt = updatedAt,
            DefinitionJson = Definition("manualTrigger", "runScript"),
        };
        var sameVersion = new Workflow
        {
            Id = workflow.Id,
            Version = workflow.Version,
            UpdatedAt = updatedAt,
            DefinitionJson = workflow.DefinitionJson,
        };

        var first = WorkflowDefinitionCache.GetOrCompile(workflow);
        var second = WorkflowDefinitionCache.GetOrCompile(sameVersion);

        second.Should().BeSameAs(first);
        second.RootNodes.Should().ContainSingle(n => n.Id == "manual");
    }

    [Fact]
    public void GetOrCompile_InvalidatesWhenWorkflowVersionChanges()
    {
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Version = 1,
            UpdatedAt = DateTime.UtcNow,
            DefinitionJson = Definition("manualTrigger", "runScript"),
        };
        var first = WorkflowDefinitionCache.GetOrCompile(workflow);

        workflow.Version++;
        workflow.UpdatedAt = workflow.UpdatedAt.AddSeconds(1);
        workflow.DefinitionJson = Definition("manualTrigger", "log");

        var second = WorkflowDefinitionCache.GetOrCompile(workflow);

        second.Should().NotBeSameAs(first);
        second.Nodes.Single(n => n.Id == "activity").Type.Should().Be("log");
    }

    private static string Definition(string triggerType, string activityType) =>
        $$"""
        {
          "nodes": [
            { "id": "manual", "type": "activity", "data": { "activityType": "{{triggerType}}", "label": "Manual" } },
            { "id": "activity", "type": "activity", "data": { "activityType": "{{activityType}}", "label": "Activity", "config": {} } }
          ],
          "edges": [
            { "id": "e1", "source": "manual", "target": "activity", "data": {} }
          ]
        }
        """;
}

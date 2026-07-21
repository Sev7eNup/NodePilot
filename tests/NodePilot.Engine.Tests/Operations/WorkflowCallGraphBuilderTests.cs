using FluentAssertions;
using NodePilot.Core.Operations;
using Xunit;

namespace NodePilot.Engine.Tests.Operations;

public class WorkflowCallGraphBuilderTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static string StartWorkflowDef(string nameOrId) => """
    {
      "nodes": [
        { "id": "t", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
        { "id": "call", "type": "activity", "data": { "activityType": "startWorkflow", "config": { "workflowNameOrId": "__REF__" } } }
      ],
      "edges": [ { "id": "e", "source": "t", "target": "call", "data": {} } ]
    }
    """.Replace("__REF__", nameOrId);

    private static string ForEachDef(string nameOrId) => """
    {
      "nodes": [
        { "id": "t", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
        { "id": "loop", "type": "activity", "data": { "activityType": "forEach", "config": { "items": "items", "childWorkflowNameOrId": "__REF__" } } }
      ],
      "edges": []
    }
    """.Replace("__REF__", nameOrId);

    private static WorkflowCallGraphInput Wf(Guid id, string name, string def) => new(id, name, def);
    private static WorkflowCallGraphInput Leaf(Guid id, string name) => new(id, name, """{"nodes":[],"edges":[]}""");

    [Fact]
    public void Build_StartWorkflowRefById_ResolvesEdge()
    {
        var edges = WorkflowCallGraphBuilder.Build(
        [
            Wf(A, "Parent", StartWorkflowDef(B.ToString())),
            Leaf(B, "Child"),
        ]);

        edges.Should().ContainSingle();
        var e = edges[0];
        e.SourceWorkflowId.Should().Be(A);
        e.TargetWorkflowId.Should().Be(B);
        e.Kind.Should().Be("startWorkflow");
        e.RefStatus.Should().Be(WorkflowRefStatus.Resolved);
        e.CallCount.Should().Be(1);
    }

    [Fact]
    public void Build_RefByName_CaseInsensitive_Resolves()
    {
        var edges = WorkflowCallGraphBuilder.Build(
        [
            Wf(A, "Parent", StartWorkflowDef("child")),
            Leaf(B, "Child"),
        ]);

        edges.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { SourceWorkflowId = A, TargetWorkflowId = (Guid?)B, RefStatus = WorkflowRefStatus.Resolved });
    }

    [Fact]
    public void Build_ForEachChildRef_ResolvesWithForEachKind()
    {
        var edges = WorkflowCallGraphBuilder.Build(
        [
            Wf(A, "Parent", ForEachDef(B.ToString())),
            Leaf(B, "Child"),
        ]);

        edges.Should().ContainSingle().Which.Kind.Should().Be("forEach");
    }

    [Fact]
    public void Build_DynamicTemplateRef_IsMarkedDynamicWithNoTarget()
    {
        var edges = WorkflowCallGraphBuilder.Build(
        [
            Wf(A, "Parent", StartWorkflowDef("{{manual.childName}}")),
            Leaf(B, "Child"),
        ]);

        var e = edges.Should().ContainSingle().Which;
        e.RefStatus.Should().Be(WorkflowRefStatus.Dynamic);
        e.TargetWorkflowId.Should().BeNull();
        e.RawRef.Should().Be("{{manual.childName}}");
    }

    [Fact]
    public void Build_RefToMissingWorkflow_IsUnresolved()
    {
        var edges = WorkflowCallGraphBuilder.Build(
        [
            Wf(A, "Parent", StartWorkflowDef("Nonexistent")),
        ]);

        edges.Should().ContainSingle().Which.RefStatus.Should().Be(WorkflowRefStatus.Unresolved);
    }

    [Fact]
    public void Build_RefToWorkflowOutsideProvidedSet_IsUnresolved_NotLeaked()
    {
        // RBAC scoping: B exists in the system but was filtered out of the caller's accessible set,
        // so the reference must NOT resolve (existence is not leaked across folder boundaries).
        var edges = WorkflowCallGraphBuilder.Build(
        [
            Wf(A, "Parent", StartWorkflowDef(B.ToString())),
        ]);

        var e = edges.Should().ContainSingle().Which;
        e.RefStatus.Should().Be(WorkflowRefStatus.Unresolved);
        e.TargetWorkflowId.Should().BeNull();
    }

    [Fact]
    public void Build_AmbiguousName_MatchingMultipleWorkflows_IsAmbiguous()
    {
        var edges = WorkflowCallGraphBuilder.Build(
        [
            Wf(A, "Parent", StartWorkflowDef("Shared")),
            Leaf(B, "Shared"),
            Leaf(C, "Shared"),
        ]);

        var e = edges.Should().ContainSingle().Which;
        e.RefStatus.Should().Be(WorkflowRefStatus.Ambiguous);
        e.TargetWorkflowId.Should().BeNull();
    }

    [Fact]
    public void Build_TwoNodesReferencingSameChild_CollapseToOneEdgeWithCallCount()
    {
        var def = """
        {
          "nodes": [
            { "id": "t", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
            { "id": "c1", "type": "activity", "data": { "activityType": "startWorkflow", "config": { "workflowNameOrId": "Child" } } },
            { "id": "c2", "type": "activity", "data": { "activityType": "startWorkflow", "config": { "workflowNameOrId": "Child" } } }
          ],
          "edges": []
        }
        """;
        var edges = WorkflowCallGraphBuilder.Build([Wf(A, "Parent", def), Leaf(B, "Child")]);

        edges.Should().ContainSingle().Which.CallCount.Should().Be(2);
    }

    [Fact]
    public void Build_MalformedOrEmptyDefinition_IsSkippedGracefully()
    {
        var edges = WorkflowCallGraphBuilder.Build(
        [
            new WorkflowCallGraphInput(A, "Broken", "{ not json"),
            new WorkflowCallGraphInput(B, "Empty", ""),
            Leaf(C, "Child"),
        ]);

        edges.Should().BeEmpty();
    }

    [Fact]
    public void Build_NoCallNodes_ProducesNoEdges()
    {
        var def = """
        {
          "nodes": [ { "id": "s", "type": "activity", "data": { "activityType": "runScript", "config": { "script": "Get-Date" } } } ],
          "edges": []
        }
        """;
        WorkflowCallGraphBuilder.Build([Wf(A, "Solo", def)]).Should().BeEmpty();
    }
}

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

    // ---- Split derivation: extraction is definition-local and cacheable, resolution is not -----

    [Fact]
    public void ExtractCallSites_LiftsKindAndRawRef_WithoutResolvingAnything()
    {
        // Extraction must not need the other workflows: that independence is exactly what lets a
        // caller cache the result against the workflow's UpdatedAt instead of re-parsing per poll.
        var sites = WorkflowCallGraphBuilder.ExtractCallSites(StartWorkflowDef("Child"));

        sites.Should().ContainSingle().Which.Should().Be(new WorkflowCallSite("startWorkflow", "Child"));
    }

    [Fact]
    public void ExtractCallSites_TrimsTheRefAndReadsForEachToo()
    {
        WorkflowCallGraphBuilder.ExtractCallSites(ForEachDef("  Child  "))
            .Should().ContainSingle().Which.Should().Be(new WorkflowCallSite("forEach", "Child"));
    }

    [Fact]
    public void ExtractCallSites_UnparseableOrEmptyDefinition_YieldsNothing()
    {
        // A broken definition is not an edge, and it must not take the graph down with it.
        WorkflowCallGraphBuilder.ExtractCallSites("{ not json").Should().BeEmpty();
        WorkflowCallGraphBuilder.ExtractCallSites("").Should().BeEmpty();
    }

    [Fact]
    public void BuildFromCallSites_MatchesBuild_ForTheSameDefinitions()
    {
        // The cached path and the parse-everything path must not be able to disagree.
        var inputs = new[] { Wf(A, "Parent", StartWorkflowDef("Child")), Leaf(B, "Child") };

        var direct = WorkflowCallGraphBuilder.Build(inputs);
        var fromSites = WorkflowCallGraphBuilder.BuildFromCallSites(
            inputs.Select(w => new WorkflowCallGraphIdentity(w.Id, w.Name)).ToList(),
            inputs.ToDictionary(w => w.Id, w => WorkflowCallGraphBuilder.ExtractCallSites(w.DefinitionJson)));

        fromSites.Should().BeEquivalentTo(direct);
    }

    [Fact]
    public void BuildFromCallSites_RenamedSibling_ReResolvesFromUnchangedCallSites()
    {
        // The reason call SITES are the cacheable unit and edges are not: a name-based reference
        // resolves against every OTHER workflow's name, so renaming the child changes the parent's
        // edge while the parent's own definition — and therefore its cached call sites — is untouched.
        var parentSites = WorkflowCallGraphBuilder.ExtractCallSites(StartWorkflowDef("Child"));
        var sites = new Dictionary<Guid, IReadOnlyList<WorkflowCallSite>>
        {
            [A] = parentSites,
            [B] = [],
        };

        var before = WorkflowCallGraphBuilder.BuildFromCallSites(
            [new WorkflowCallGraphIdentity(A, "Parent"), new WorkflowCallGraphIdentity(B, "Child")], sites);
        var after = WorkflowCallGraphBuilder.BuildFromCallSites(
            [new WorkflowCallGraphIdentity(A, "Parent"), new WorkflowCallGraphIdentity(B, "Renamed")], sites);

        before.Should().ContainSingle().Which.RefStatus.Should().Be(WorkflowRefStatus.Resolved);
        after.Should().ContainSingle().Which.RefStatus.Should().Be(WorkflowRefStatus.Unresolved);
    }

    [Fact]
    public void BuildFromCallSites_WorkflowWithoutAnEntry_ContributesNoEdges()
    {
        // A workflow the cache has never seen must degrade to "no outgoing calls", not throw.
        var edges = WorkflowCallGraphBuilder.BuildFromCallSites(
            [new WorkflowCallGraphIdentity(A, "Parent"), new WorkflowCallGraphIdentity(B, "Child")],
            new Dictionary<Guid, IReadOnlyList<WorkflowCallSite>>());

        edges.Should().BeEmpty();
    }
}

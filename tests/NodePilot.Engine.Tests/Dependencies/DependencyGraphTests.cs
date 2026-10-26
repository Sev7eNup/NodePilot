using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Dependencies;
using NodePilot.Core.Operations;
using Xunit;

namespace NodePilot.Engine.Tests.Dependencies;

public class DependencyGraphTests
{
    private static DependencyResource Resource(string kind, string name) => new(kind, Guid.NewGuid(), name, "", true, "v1");
    private static object Node(string id, string type, object config, string? target = null, string? credential = null, bool disabled = false)
        => new { id, type = "activity", data = new { activityType = type, label = id, config, targetMachineId = target, credentialId = credential, disabled } };
    private static DependencyFacts Facts(params object[] nodes) => DependencyFacts.Extract(JsonSerializer.Serialize(new { nodes, edges = Array.Empty<object>() }));
    private static DependencyGraph Build(DependencyResource[] resources, Dictionary<Guid, DependencyFacts> facts,
        DependencyMachine[]? machines = null, DependencyCustom[]? customs = null, bool restricted = true,
        WorkflowCallGraphIdentity[]? identities = null) => DependencyGraphBuilder.Build(resources,
            identities ?? resources.Where(r => r.Kind == "workflow").Select(r => new WorkflowCallGraphIdentity(r.Id, r.Name)).ToArray(), facts,
            machines ?? [], customs ?? [], restricted);

    [Fact]
    public void Analyze_DirectIndirectAndRepeatedSteps_CountsUniqueWorkflows()
    {
        var host = Resource("machine", "FS-PROD-01");
        var children = Enumerable.Range(0, 4).Select(i => Resource("workflow", "Child " + i)).ToArray();
        var parents = Enumerable.Range(0, 2).Select(i => Resource("workflow", "Parent " + i)).ToArray();
        var facts = children.ToDictionary(w => w.Id, w => Facts(Node("copy", "runScript", new { script = "test" }, host.Id.ToString()),
            Node("verify", "runScript", new { script = "test" }, host.Id.ToString())));
        foreach (var parent in parents) facts[parent.Id] = Facts(Node("call", "startWorkflow", new { workflowNameOrId = children[0].Id.ToString() }));
        var graph = Build([host, .. children, .. parents], facts, [new(host.Id, host.Name, "fs", null)]);
        var result = graph.Analyze(host.Key, true);
        result.Complete.Should().BeTrue();
        result.Matches.Count(m => m.Distance == 1).Should().Be(4);
        result.Matches.Count(m => m.Distance == 2).Should().Be(2);
        result.Matches.First(m => m.Key == children[0].Key).Paths[0].Links[0].Locations.Should().HaveCount(2);
    }

    [Fact]
    public void Analyze_CredentialOverride_DoesNotPropagateMachineDefault()
    {
        var host = Resource("machine", "Host"); var own = Resource("credential", "Own"); var inherited = Resource("credential", "Default");
        var a = Resource("workflow", "Override"); var b = Resource("workflow", "Inherit");
        var graph = Build([host, own, inherited, a, b], new()
        {
            [a.Id] = Facts(Node("step", "runScript", new { }, host.Id.ToString(), own.Id.ToString())),
            [b.Id] = Facts(Node("step", "runScript", new { }, host.Id.ToString())),
        }, [new(host.Id, host.Name, "host", inherited.Id)]);
        graph.Analyze(inherited.Key, true).Matches.Select(m => m.Key).Should().BeEquivalentTo([host.Key, b.Key]);
        graph.Analyze(a.Key, false).Matches.Select(m => m.Key).Should().Contain(own.Key).And.NotContain(inherited.Key);
        graph.Links.Single(l => l.Source == b.Key && l.Target == inherited.Key).Locations[0].Via.Should().Be(host.Id.ToString());
    }

    [Fact]
    public void Analyze_AgentPermission_IsPossibleAndServiceIdentityIgnoresCredentials()
    {
        var a = Resource("workflow", "Agent"); var b = Resource("workflow", "Allowed");
        var c = Resource("credential", "Unused");
        var graph = Build([a, b, c], new() { [a.Id] = Facts(Node("agent", "aiAgent", new
        {
            agent = new { useServiceIdentity = true, credentialId = c.Id, tools = new[] { new { name = "workflow_run", workflowIds = new[] { b.Id } } } }
        })) });
        graph.Analyze(b.Key, true).Matches.Should().ContainSingle().Which.Possible.Should().BeTrue();
        graph.Analyze(c.Key, true).Matches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("{{globals.HOST}}", "dynamic")]
    [InlineData("free-host", "external")]
    [InlineData("localhost", "local")]
    [InlineData("", "local")]
    [InlineData("00000000-0000-0000-0000-000000000001", "unresolved")]
    public void Build_TargetKinds_AreExplicit(string target, string status)
    {
        var a = Resource("workflow", "A");
        var graph = Build([a], new() { [a.Id] = Facts(Node("step", "runScript", new { }, target)) });
        graph.Problems.Should().Contain(p => p.Kind == "machine" && p.Status == status);
        graph.Links.Should().BeEmpty();
    }

    [Fact]
    public void Build_HiddenExactName_DoesNotResolveToVisibleCaseVariant()
    {
        var a = Resource("workflow", "A"); var visible = Resource("workflow", "child"); var hidden = Resource("workflow", "Child");
        var graph = Build([a, visible], new() { [a.Id] = Facts(Node("call", "startWorkflow", new { workflowNameOrId = "Child" })) },
            identities: [new(a.Id, a.Name), new(visible.Id, visible.Name), new(hidden.Id, hidden.Name)]);
        graph.Links.Should().BeEmpty();
        graph.Problems.Should().ContainSingle().Which.Status.Should().Be("unresolved");
        JsonSerializer.Serialize(new { graph.Resources, graph.Links, graph.Problems }).Should().NotContain(hidden.Id.ToString());
    }

    [Fact]
    public void Build_ExactCaseWinsAndAmbiguityIsExplicit()
    {
        var a = Resource("workflow", "A"); var exact = Resource("workflow", "Child"); var variant = Resource("workflow", "child");
        var graph = Build([a, exact, variant], new() { [a.Id] = Facts(Node("call", "startWorkflow", new { workflowNameOrId = "Child" }),
            Node("other", "startWorkflow", new { workflowNameOrId = "CHILD" })) });
        graph.Links.Should().ContainSingle().Which.Target.Should().Be(exact.Key);
        graph.Problems.Should().ContainSingle().Which.Status.Should().Be("ambiguous");
    }

    [Fact]
    public void Extract_KnownConfigAndCustomDefaults_FindsGlobalsWithoutReadingAnnotations()
    {
        var facts = Facts(Node("step", "restApi", new { url = "{{globals.URL}}", headers = new { X = "{{globals.KEY}}" }, nonsense = "{{globals.IGNORED}}" }));
        facts.References.Select(r => r.Value).Should().BeEquivalentTo(["URL", "KEY"]);
        var custom = DependencyFacts.ExtractCustom("Write-Output {{globals.MESSAGE}}", """[{"name":"path","default":"{{globals.PATH}}"}]""");
        custom.References.Select(r => r.Value).Should().BeEquivalentTo(["MESSAGE", "PATH"]);
    }

    [Fact]
    public void Analyze_CustomDefault_OverridePreventsFalseIndirectUse()
    {
        var a = Resource("workflow", "Override"); var b = Resource("workflow", "Inherit"); var custom = Resource("customActivity", "Activity"); var variable = Resource("variable", "PATH");
        var graph = Build([a, b, custom, variable], new()
        {
            [a.Id] = Facts(Node("step", "custom:copy", new { __customDefinitionId = custom.Id, path = "literal" })),
            [b.Id] = Facts(Node("step", "custom:copy", new { __customDefinitionId = custom.Id })),
        }, customs: [new(custom.Id, "copy", false, DependencyFacts.ExtractCustom("test", """[{"name":"path","default":"{{globals.PATH}}"}]"""))]);
        graph.Analyze(variable.Key, true).Matches.Select(m => m.Key).Should().BeEquivalentTo([custom.Key, b.Key]);
        graph.Analyze(a.Key, false).Matches.Select(m => m.Key).Should().NotContain(variable.Key);
    }

    [Fact]
    public void Analyze_CustomDefault_EvidenceExcludesOverridingInvocations()
    {
        var workflow = Resource("workflow", "Mixed"); var custom = Resource("customActivity", "Activity"); var variable = Resource("variable", "PATH");
        var graph = Build([workflow, custom, variable], new()
        {
            [workflow.Id] = Facts(Node("override", "custom:copy", new { __customDefinitionId = custom.Id, path = "literal" }),
                Node("inherit", "custom:copy", new { __customDefinitionId = custom.Id })),
        }, customs: [new(custom.Id, "copy", false, DependencyFacts.ExtractCustom("test", """[{"name":"path","default":"{{globals.PATH}}"}]"""))]);
        foreach (var result in new[] { graph.Analyze(variable.Key, true).Matches.Single(m => m.Key == workflow.Key),
            graph.Analyze(workflow.Key, false).Matches.Single(m => m.Key == variable.Key) })
            result.Paths.Single().Links[0].Locations.Should().ContainSingle().Which.NodeId.Should().Be("inherit");
    }

    [Fact]
    public void Analyze_CycleAndMultiplePaths_TerminatesWithoutCountingRoot()
    {
        var a = Resource("workflow", "A"); var b = Resource("workflow", "B"); var c = Resource("workflow", "C");
        var graph = Build([a, b, c], new()
        {
            [a.Id] = Facts(Node("b", "startWorkflow", new { workflowNameOrId = b.Id }), Node("c", "startWorkflow", new { workflowNameOrId = c.Id })),
            [b.Id] = Facts(Node("c", "startWorkflow", new { workflowNameOrId = c.Id })),
            [c.Id] = Facts(Node("a", "startWorkflow", new { workflowNameOrId = a.Id })),
        });
        var result = graph.Analyze(a.Key, false);
        result.Complete.Should().BeTrue();
        result.Matches.Should().HaveCount(2);
        result.Matches.Single(m => m.Key == c.Key).Distance.Should().Be(1);
        result.Matches.Single(m => m.Key == c.Key).Paths.Should().HaveCount(2);
        graph.Analyze(a.Key, false, maxEdges: 1).Complete.Should().BeFalse();
        graph.Analyze(a.Key, false, maxDepth: 0).Complete.Should().BeFalse();
    }

    [Fact]
    public void Extract_InvalidAndDisabledDefinitions_PreserveCoverageAndLocation()
    {
        DependencyFacts.Extract("not json").Valid.Should().BeFalse();
        var facts = Facts(Node("call", "startWorkflow", new { workflowNameOrId = "Missing" }, disabled: true));
        facts.References.Should().ContainSingle().Which.Disabled.Should().BeTrue();
    }
}

using FluentAssertions;
using NodePilot.Core.Operations;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.WorkflowDefinitions;

/// <summary>
/// These facts replace what several list endpoints used to derive by reading and parsing every
/// workflow definition on every request, so they have to answer exactly what those endpoints
/// asked before.
/// </summary>
public class WorkflowDefinitionFactsTests
{
    private static readonly Guid MachineA = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid MachineB = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private static string Node(string activityType, string extra = "")
        => $"{{\"id\":\"n{Guid.NewGuid():N}\",\"type\":\"activity\",\"data\":{{\"activityType\":\"{activityType}\"{extra}}}}}";

    private static string Definition(params string[] nodes)
        => $"{{\"nodes\":[{string.Join(",", nodes)}],\"edges\":[]}}";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"nodes\":\"wrong shape\"}")]
    public void Extract_UnusableDefinition_YieldsEmptyFacts(string? definitionJson)
    {
        // A broken definition is not an error here: it must not take a list endpoint down.
        var facts = WorkflowDefinitionFacts.Extract(definitionJson);

        facts.CallSites.Should().BeEmpty();
        facts.MachineRefs.Should().BeEmpty();
        facts.HasManualTriggerParameters.Should().BeFalse();
    }

    [Fact]
    public void Extract_MachineRefs_AreDistinctAndParseableOnly()
    {
        // The same host targeted twice is one reference; a templated target cannot be attributed
        // to a machine without the runtime resolver, so it is skipped.
        var facts = WorkflowDefinitionFacts.Extract(Definition(
            Node("runScript", $",\"targetMachineId\":\"{MachineA}\""),
            Node("runScript", $",\"targetMachineId\":\"{MachineA}\""),
            Node("runScript", $",\"targetMachineId\":\"{MachineB}\""),
            Node("runScript", ",\"targetMachineId\":\"{{globals.targetHost}}\""),
            Node("delay")));

        facts.MachineRefs.Should().BeEquivalentTo([MachineA, MachineB]);
    }

    [Fact]
    public void Extract_CallSites_MatchTheCallGraphExtractor()
    {
        var definition = Definition(
            Node("startWorkflow", ",\"config\":{\"workflowNameOrId\":\"Child\"}"),
            Node("forEach", ",\"config\":{\"childWorkflowNameOrId\":\"Loop\"}"));

        WorkflowDefinitionFacts.Extract(definition).CallSites
            .Should().Equal(WorkflowCallGraphBuilder.ExtractCallSites(definition));
    }

    [Fact]
    public void Extract_ManualTriggerWithParameters_IsFlagged()
    {
        var facts = WorkflowDefinitionFacts.Extract(Definition(
            Node("manualTrigger", ",\"config\":{\"parameters\":[{\"name\":\"host\",\"type\":\"string\"}]}")));

        facts.HasManualTriggerParameters.Should().BeTrue();
    }

    [Theory]
    [InlineData(",\"config\":{\"parameters\":[]}")]
    [InlineData(",\"config\":{}")]
    [InlineData("")]
    public void Extract_ManualTriggerWithoutParameters_IsNotFlagged(string extra)
    {
        // Starting such a workflow asks the caller for nothing, so the list can run it directly.
        WorkflowDefinitionFacts.Extract(Definition(Node("manualTrigger", extra)))
            .HasManualTriggerParameters.Should().BeFalse();
    }

    [Fact]
    public void Extract_NoManualTrigger_IsNotFlagged()
    {
        WorkflowDefinitionFacts.Extract(Definition(Node("scheduleTrigger"), Node("runScript")))
            .HasManualTriggerParameters.Should().BeFalse();
    }

    [Fact]
    public void Extract_FirstManualTriggerWins()
    {
        // Mirrors the run dialog, which takes the first manual trigger it finds. A second one is
        // not consulted even when the first declares nothing.
        var facts = WorkflowDefinitionFacts.Extract(Definition(
            Node("manualTrigger", ",\"config\":{\"parameters\":[]}"),
            Node("manualTrigger", ",\"config\":{\"parameters\":[{\"name\":\"host\"}]}")));

        facts.HasManualTriggerParameters.Should().BeFalse();
    }
}

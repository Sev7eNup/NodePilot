using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.WorkflowDefinitions;

public class ParseNodesEdgesTests
{
    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    #region ParseNodes

    [Fact]
    public void ParseNodes_ValidJson_ReturnsAllNodes()
    {
        var json = Parse("""
        {
            "nodes": [
                {
                    "id": "step-1",
                    "type": "activity",
                    "position": {"x":0,"y":0},
                    "data": { "label": "A", "activityType": "runScript" }
                },
                {
                    "id": "step-2",
                    "type": "activity",
                    "position": {"x":0,"y":0},
                    "data": { "label": "B", "activityType": "restApi" }
                }
            ]
        }
        """);

        var nodes = WorkflowDefinitionParser.ParseNodes(json);

        nodes.Should().HaveCount(2);
        nodes[0].Id.Should().Be("step-1");
        nodes[1].Id.Should().Be("step-2");
    }

    [Fact]
    public void ParseNodes_MissingNodesProperty_ReturnsEmptyList()
    {
        var json = Parse("""{ "edges": [] }""");

        var nodes = WorkflowDefinitionParser.ParseNodes(json);

        nodes.Should().BeEmpty();
    }

    [Fact]
    public void ParseNodes_NodeWithOutputVariable_ParsesCorrectly()
    {
        var json = Parse("""
        {
            "nodes": [
                {
                    "id": "step-1",
                    "type": "activity",
                    "position": {"x":0,"y":0},
                    "data": {
                        "label": "Check Disk",
                        "activityType": "runScript",
                        "outputVariable": "diskCheck"
                    }
                }
            ]
        }
        """);

        var nodes = WorkflowDefinitionParser.ParseNodes(json);

        nodes.Should().ContainSingle();
        nodes[0].Data.OutputVariable.Should().Be("diskCheck");
    }

    [Fact]
    public void ParseNodes_NodeWithTargetMachineId_ParsesGuid()
    {
        var targetId = "00000000-0000-0000-0000-000000000001";
        var json = Parse($$"""
        {
            "nodes": [
                {
                    "id": "step-1",
                    "type": "activity",
                    "position": {"x":0,"y":0},
                    "data": {
                        "label": "Test",
                        "activityType": "runScript",
                        "targetMachineId": "{{targetId}}"
                    }
                }
            ]
        }
        """);

        var nodes = WorkflowDefinitionParser.ParseNodes(json);

        nodes[0].Data.TargetMachineRaw.Should().Be(targetId);
    }

    [Fact]
    public void ParseNodes_ActivityTypeFromData_OverridesNodeType()
    {
        var json = Parse("""
        {
            "nodes": [
                {
                    "id": "step-1",
                    "type": "activity",
                    "position": {"x":0,"y":0},
                    "data": {
                        "label": "Test",
                        "activityType": "runScript"
                    }
                }
            ]
        }
        """);

        var nodes = WorkflowDefinitionParser.ParseNodes(json);

        nodes[0].Type.Should().Be("runScript");
        nodes[0].Type.Should().NotBe("activity");
    }

    #endregion

    #region ParseEdges

    [Fact]
    public void ParseEdges_ValidJson_ReturnsAllEdges()
    {
        var json = Parse("""
        {
            "edges": [
                {
                    "id": "e1",
                    "source": "step-1",
                    "target": "step-2",
                    "data": { "label": "On Success", "condition": "step-1.success", "disabled": false }
                },
                {
                    "id": "e2",
                    "source": "step-2",
                    "target": "step-3",
                    "data": { "label": "Next", "condition": null, "disabled": false }
                }
            ]
        }
        """);

        var edges = WorkflowDefinitionParser.ParseEdges(json);

        edges.Should().HaveCount(2);
        edges[0].Id.Should().Be("e1");
        edges[0].Source.Should().Be("step-1");
        edges[0].Target.Should().Be("step-2");
        edges[1].Id.Should().Be("e2");
    }

    [Fact]
    public void ParseEdges_DisabledEdge_SetsDisabledTrue()
    {
        var json = Parse("""
        {
            "edges": [
                {
                    "id": "e1",
                    "source": "step-1",
                    "target": "step-2",
                    "data": { "disabled": true }
                }
            ]
        }
        """);

        var edges = WorkflowDefinitionParser.ParseEdges(json);

        edges[0].Disabled.Should().BeTrue();
    }

    [Fact]
    public void ParseEdges_EdgeWithCondition_ParsesCondition()
    {
        var json = Parse("""
        {
            "edges": [
                {
                    "id": "e1",
                    "source": "step-1",
                    "target": "step-2",
                    "data": { "condition": "step-1.success", "label": "On Success" }
                }
            ]
        }
        """);

        var edges = WorkflowDefinitionParser.ParseEdges(json);

        edges[0].Condition.Should().Be("step-1.success");
        edges[0].Label.Should().Be("On Success");
    }

    [Fact]
    public void ParseEdges_EdgeWithConditionExpression_ParsesStructuredExpression()
    {
        var json = Parse("""
        {
            "edges": [
                {
                    "id": "e1",
                    "source": "step-1",
                    "target": "step-2",
                    "data": {
                        "label": "If OK",
                        "conditionExpression": {
                            "type": "comparison",
                            "left": { "kind": "variable", "stepId": "step-1", "field": "output" },
                            "op": "==",
                            "right": { "kind": "literal", "value": "OK" }
                        }
                    }
                }
            ]
        }
        """);

        var edges = WorkflowDefinitionParser.ParseEdges(json);

        edges[0].ConditionExpression.Should().NotBeNull();
        edges[0].ConditionExpression!.Value.GetProperty("type").GetString().Should().Be("comparison");
        edges[0].ConditionExpression!.Value.GetProperty("op").GetString().Should().Be("==");
    }

    [Fact]
    public void ParseEdges_MissingEdgesProperty_ReturnsEmptyList()
    {
        var json = Parse("""{ "nodes": [] }""");

        var edges = WorkflowDefinitionParser.ParseEdges(json);

        edges.Should().BeEmpty();
    }

    [Fact]
    public void ParseEdges_EdgeWithoutData_DefaultsToNullCondition()
    {
        var json = Parse("""
        {
            "edges": [
                {
                    "id": "e1",
                    "source": "step-1",
                    "target": "step-2"
                }
            ]
        }
        """);

        var edges = WorkflowDefinitionParser.ParseEdges(json);

        edges[0].Condition.Should().BeNull();
        edges[0].Label.Should().BeNull();
        edges[0].Disabled.Should().BeFalse();
    }

    #endregion
}

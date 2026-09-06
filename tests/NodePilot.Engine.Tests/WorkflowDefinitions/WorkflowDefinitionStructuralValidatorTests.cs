using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Validation;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.WorkflowDefinitions;

/// <summary>
/// Drives each structural-validation rule of <see cref="WorkflowDefinitionStructuralValidator"/>
/// with
/// a purpose-built malformed (or valid) definition and asserts the exact rule that fires.
/// </summary>
public class WorkflowDefinitionStructuralValidatorTests
{
    private static WorkflowDefinitionValidationResult Validate(string json)
        => WorkflowDefinitionStructuralValidator.Validate(JsonDocument.Parse(json).RootElement);

    // ---------- root ----------

    [Fact]
    public void Validate_RootNotObject_IsInvalid()
    {
        var result = Validate("[]");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("root must be a JSON object");
    }

    [Fact]
    public void Validate_EmptyObject_IsValid()
    {
        var result = Validate("{}");
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    // ---------- nodes / edges containers ----------

    [Fact]
    public void Validate_NodesNotArray_IsInvalid()
    {
        var result = Validate("""{ "nodes": {} }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes must be an array");
    }

    [Fact]
    public void Validate_EdgesNotArray_IsInvalid()
    {
        var result = Validate("""{ "edges": {} }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("edges must be an array");
    }

    // ---------- node shape ----------

    [Fact]
    public void Validate_NodeNotObject_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ "notAnObject" ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0] must be an object");
    }

    [Fact]
    public void Validate_NodeMissingId_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "data": {} } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].id must be a non-empty string");
    }

    [Fact]
    public void Validate_NodeBlankId_IsInvalid()
    {
        // Whitespace-only id: present + string but fails the IsNullOrWhiteSpace guard.
        var result = Validate("""{ "nodes": [ { "id": "   ", "data": {} } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].id must be a non-empty string");
    }

    [Fact]
    public void Validate_NodeNonStringId_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": 123, "data": {} } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].id must be a non-empty string");
    }

    [Fact]
    public void Validate_DuplicateNodeId_IsInvalid()
    {
        var result = Validate("""
        { "nodes": [
            { "id": "n1", "type": "activity", "data": { "activityType": "runScript" } },
            { "id": "n1", "type": "activity", "data": { "activityType": "runScript" } }
        ] }
        """);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("duplicate node id 'n1'");
    }

    [Fact]
    public void Validate_NodeMissingData_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1" } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data must be an object");
    }

    [Fact]
    public void Validate_NodeDataNotObject_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1", "data": "nope" } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data must be an object");
    }

    // ---------- annotation nodes (stickyNote / group) ----------

    [Fact]
    public void Validate_AnnotationNode_WithValidLabel_IsValid()
    {
        var result = Validate("""{ "nodes": [ { "id": "s1", "type": "stickyNote", "data": { "label": "note" } } ] }""");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_AnnotationNode_WithoutActivityType_IsValid()
    {
        // Annotation nodes never require an activityType — the early return skips the whole
        // activity branch.
        var result = Validate("""{ "nodes": [ { "id": "g1", "type": "group", "data": {} } ] }""");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_AnnotationNode_BadLabelType_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "s1", "type": "stickyNote", "data": { "label": 5 } } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data.label must be a string");
    }

    // ---------- activityType resolution ----------

    [Fact]
    public void Validate_ActivityTypeEmptyString_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": "  " } } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data.activityType must be a non-empty string");
    }

    [Fact]
    public void Validate_ActivityTypeNonString_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": 42 } } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data.activityType must be a non-empty string");
    }

    [Fact]
    public void Validate_NodeTypeIsConcreteActivity_NoActivityType_IsValid()
    {
        // No data.activityType, but node.type is a concrete (non-"activity") built-in type to used
        // as the type.
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "runScript", "data": {} } ] }""");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_NodeTypeIsActivity_NoActivityType_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "activity", "data": {} } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data.activityType is required unless node.type is a concrete activity type");
    }

    [Fact]
    public void Validate_NoTypeAndNoActivityType_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1", "data": {} } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data.activityType is required unless node.type is a concrete activity type");
    }

    // ---------- custom activity types ----------

    [Fact]
    public void Validate_ValidCustomActivityType_IsValid()
    {
        // Grammar-only acceptance in Core; existence/enabled is enforced at run time.
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": "custom:disk_check" } } ] }""");
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_InvalidCustomActivityType_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": "custom:bad slug" } } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data.activityType 'custom:bad slug' is not a valid custom activity type (expected custom:<slug>)");
    }

    [Fact]
    public void Validate_UnknownBuiltInActivityType_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": "totallyUnknown" } } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data.activityType references unknown activity type 'totallyUnknown'");
    }

    // ---------- optional node string fields ----------

    [Fact]
    public void Validate_NodeOutputVariableWrongType_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": "runScript", "outputVariable": 5 } } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data.outputVariable must be a string");
    }

    [Fact]
    public void Validate_NodeBreakpointConditionWrongType_IsInvalid()
    {
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": "runScript", "breakpointCondition": true } } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("nodes[0].data.breakpointCondition must be a string");
    }

    [Fact]
    public void Validate_NodeNullOptionalString_IsValid()
    {
        // allowNull: true — an explicit JSON null is accepted for the optional string fields.
        var result = Validate("""{ "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": "runScript", "targetMachineId": null, "credentialId": null } } ] }""");
        result.IsValid.Should().BeTrue();
    }

    // ---------- edge shape ----------

    [Fact]
    public void Validate_EdgeNotObject_IsInvalid()
    {
        var result = Validate("""{ "nodes": [], "edges": [ "nope" ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("edges[0] must be an object");
    }

    [Fact]
    public void Validate_EdgeMissingId_IsInvalid()
    {
        var result = Validate("""{ "nodes": [], "edges": [ { "source": "a", "target": "b" } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("edges[0].id must be a non-empty string");
    }

    [Fact]
    public void Validate_DuplicateEdgeId_IsInvalid()
    {
        var result = Validate("""
        { "nodes": [
            { "id": "n1", "type": "activity", "data": { "activityType": "runScript" } },
            { "id": "n2", "type": "activity", "data": { "activityType": "runScript" } }
        ], "edges": [
            { "id": "e1", "source": "n1", "target": "n2" },
            { "id": "e1", "source": "n2", "target": "n1" }
        ] }
        """);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("duplicate edge id 'e1'");
    }

    [Fact]
    public void Validate_EdgeMissingSource_IsInvalid()
    {
        var result = Validate("""{ "nodes": [], "edges": [ { "id": "e1", "target": "b" } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("edges[0].source must be a non-empty string");
    }

    [Fact]
    public void Validate_EdgeMissingTarget_IsInvalid()
    {
        var result = Validate("""{ "nodes": [], "edges": [ { "id": "e1", "source": "a" } ] }""");
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("edges[0].target must be a non-empty string");
    }

    [Fact]
    public void Validate_EdgeSourceReferencesUnknownNode_IsInvalid()
    {
        var result = Validate("""
        { "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": "runScript" } } ],
          "edges": [ { "id": "e1", "source": "ghost", "target": "n1" } ] }
        """);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("edges[0].source references unknown node 'ghost'");
    }

    [Fact]
    public void Validate_EdgeTargetReferencesUnknownNode_IsInvalid()
    {
        var result = Validate("""
        { "nodes": [ { "id": "n1", "type": "activity", "data": { "activityType": "runScript" } } ],
          "edges": [ { "id": "e1", "source": "n1", "target": "ghost" } ] }
        """);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("edges[0].target references unknown node 'ghost'");
    }

    [Fact]
    public void Validate_EdgeDataLabelWrongType_IsInvalid()
    {
        var result = Validate("""
        { "nodes": [
            { "id": "n1", "type": "activity", "data": { "activityType": "runScript" } },
            { "id": "n2", "type": "activity", "data": { "activityType": "runScript" } }
        ], "edges": [ { "id": "e1", "source": "n1", "target": "n2", "data": { "label": 7 } } ] }
        """);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("edges[0].data.label must be a string");
    }

    [Fact]
    public void Validate_MultipleIncomingEdgesToOrdinaryActivity_IsInvalid()
    {
        var result = Validate("""
        { "nodes": [
            { "id": "a", "type": "activity", "data": { "activityType": "runScript" } },
            { "id": "b", "type": "activity", "data": { "activityType": "runScript" } },
            { "id": "target", "type": "activity", "data": { "activityType": "log" } }
        ], "edges": [
            { "id": "e1", "source": "a", "target": "target" },
            { "id": "e2", "source": "b", "target": "target" }
        ] }
        """);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("node 'target' has multiple incoming edges; insert a junction before it");
        result.Code.Should().Be("fan-in-requires-junction");
        result.NodeId.Should().Be("target");
    }

    [Fact]
    public void Validate_DuplicateConnection_IsInvalidWithoutBeingReportedAsFanIn()
    {
        var result = Validate("""
        { "nodes": [
            { "id": "source", "type": "activity", "data": { "activityType": "runScript" } },
            { "id": "target", "type": "activity", "data": { "activityType": "log" } }
        ], "edges": [
            { "id": "e1", "source": "source", "target": "target" },
            { "id": "e2", "source": "source", "target": "target" }
        ] }
        """);

        result.IsValid.Should().BeFalse();
        result.Code.Should().Be("duplicate-edge");
        result.NodeId.Should().Be("source");
    }

    [Fact]
    public void Validate_MultipleIncomingEdgesToJunction_IsValid()
    {
        var result = Validate("""
        { "nodes": [
            { "id": "a", "type": "activity", "data": { "activityType": "runScript" } },
            { "id": "b", "type": "activity", "data": { "activityType": "runScript" } },
            { "id": "join", "type": "activity", "data": { "activityType": "junction", "config": { "mode": "waitAll" } } },
            { "id": "target", "type": "activity", "data": { "activityType": "log" } }
        ], "edges": [
            { "id": "e1", "source": "a", "target": "join" },
            { "id": "e2", "source": "b", "target": "join" },
            { "id": "e3", "source": "join", "target": "target" }
        ] }
        """);

        result.IsValid.Should().BeTrue();
    }

    // ---------- edge conditions ----------

    private static string TwoNodes(string edgeData) => $$"""
        { "nodes": [
            { "id": "n1", "type": "activity", "data": { "activityType": "runScript", "outputVariable": "hostInfo" } },
            { "id": "n2", "type": "activity", "data": { "activityType": "log" } }
        ], "edges": [ { "id": "e1", "source": "n1", "target": "n2", "data": {{edgeData}} } ] }
        """;

    [Theory]
    [InlineData("n1.success")]
    [InlineData("n1.failed")]
    [InlineData("hostInfo.failed")]
    public void Validate_LegacyConditionOnKnownStepOrAlias_IsValid(string condition)
    {
        Validate(TwoNodes($$"""{ "condition": "{{condition}}" }""")).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("ghost.success", "edges[0].data.condition references unknown step 'ghost'")]
    [InlineData("n1.done", "edges[0].data.condition must have the form <stepId>.success or <stepId>.failed")]
    [InlineData("garbage", "edges[0].data.condition must have the form <stepId>.success or <stepId>.failed")]
    public void Validate_LegacyConditionMalformedOrUnknownStep_IsInvalid(string condition, string expectedError)
    {
        var result = Validate(TwoNodes($$"""{ "condition": "{{condition}}" }"""));
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be(expectedError);
        result.Code.Should().Be("invalid-edge-condition");
        result.NodeId.Should().Be("n1");
    }

    [Fact]
    public void Validate_ConditionNotAString_IsInvalid()
    {
        var result = Validate(TwoNodes("""{ "condition": 5 }"""));
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be("edges[0].data.condition must be a string");
    }

    [Fact]
    public void Validate_WellFormedConditionExpression_IsValid()
    {
        var result = Validate(TwoNodes("""
            { "condition": null, "conditionExpression": { "type": "group", "op": "and", "children": [
                { "type": "comparison", "op": "==",
                  "left": { "kind": "variable", "stepId": "hostInfo", "field": "param", "paramName": "exitCode" },
                  "right": { "kind": "literal", "value": "0" } },
                { "type": "not", "child": { "type": "comparison", "op": "isEmpty",
                  "left": { "kind": "variable", "source": "global", "name": "ENV" } } },
                { "type": "comparison", "op": "isTrue", "left": { "kind": "variable", "source": "manual", "name": "force" } }
            ] } }
            """));
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("""{ "type": "maybe" }""", "edges[0].data.conditionExpression.type 'maybe' is not a condition type")]
    [InlineData("""{ "op": "AND", "children": [] }""", "edges[0].data.conditionExpression.type is required")]
    [InlineData("""{ "type": "group", "op": "XOR", "children": [] }""", "edges[0].data.conditionExpression.op must be AND or OR")]
    [InlineData("""{ "type": "group", "op": "AND" }""", "edges[0].data.conditionExpression.children must be an array")]
    [InlineData("""{ "type": "not" }""", "edges[0].data.conditionExpression.child is required")]
    [InlineData("""{ "type": "comparison", "left": { "kind": "literal", "value": "a" } }""", "edges[0].data.conditionExpression.op is required")]
    [InlineData("""{ "type": "comparison", "op": "like", "left": { "kind": "literal", "value": "a" } }""", "edges[0].data.conditionExpression.op 'like' is not a comparison operator")]
    [InlineData("""{ "type": "comparison", "op": "==" }""", "edges[0].data.conditionExpression.left is required")]
    [InlineData("""{ "type": "comparison", "op": "contains", "left": { "kind": "literal", "value": "a" } }""", "edges[0].data.conditionExpression.right is required for operator 'contains'")]
    [InlineData("""{ "type": "comparison", "op": "isEmpty", "left": { "kind": "variable", "stepId": "ghost" } }""", "edges[0].data.conditionExpression.left.stepId references unknown step 'ghost'")]
    [InlineData("""{ "type": "comparison", "op": "isEmpty", "left": { "kind": "variable", "stepId": "n1", "field": "param" } }""", "edges[0].data.conditionExpression.left.paramName is required for field 'param'")]
    [InlineData("""{ "type": "comparison", "op": "isEmpty", "left": { "kind": "variable", "stepId": "n1", "field": "stdout" } }""", "edges[0].data.conditionExpression.left.field 'stdout' is not an operand field")]
    [InlineData("""{ "type": "comparison", "op": "isEmpty", "left": { "kind": "variable", "source": "event", "name": "status" } }""", "edges[0].data.conditionExpression.left.source 'event' is not available on a workflow edge")]
    [InlineData("""{ "type": "group", "op": "OR", "children": [ { "type": "comparison", "op": "==", "left": { "kind": "literal", "value": "a" }, "right": "b" } ] }""", "edges[0].data.conditionExpression.children[0].right must be an object")]
    public void Validate_MalformedConditionExpression_IsInvalid(string expression, string expectedError)
    {
        var result = Validate(TwoNodes($$"""{ "conditionExpression": {{expression}} }"""));
        result.IsValid.Should().BeFalse();
        result.Error.Should().Be(expectedError);
        result.Code.Should().Be("invalid-edge-condition");
        result.NodeId.Should().Be("n1");
    }

    [Fact]
    public void Validate_NullConditionExpression_IsValid()
    {
        Validate(TwoNodes("""{ "conditionExpression": null }""")).IsValid.Should().BeTrue();
    }

    // ---------- full happy path ----------

    [Fact]
    public void Validate_FullValidDefinition_IsValid()
    {
        var result = Validate("""
        { "nodes": [
            { "id": "n1", "type": "activity", "data": { "activityType": "runScript", "label": "Run", "outputVariable": "out", "targetMachineId": null } },
            { "id": "n2", "type": "activity", "data": { "activityType": "delay" } }
        ], "edges": [ { "id": "e1", "source": "n1", "target": "n2", "data": { "label": "next" } } ] }
        """);
        result.IsValid.Should().BeTrue();
        result.Error.Should().BeNull();
    }
}

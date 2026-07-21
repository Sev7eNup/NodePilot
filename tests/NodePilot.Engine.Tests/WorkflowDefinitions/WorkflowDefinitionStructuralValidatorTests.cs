using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Validation;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Engine.Tests.WorkflowDefinitions;

/// <summary>
/// Drives each structural-validation rule of <see cref="WorkflowDefinitionStructuralValidator"/> with
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
        // Annotation nodes never require an activityType — the early return skips the whole activity branch.
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
        // No data.activityType, but node.type is a concrete (non-"activity") built-in type → used as the type.
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

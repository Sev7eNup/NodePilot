using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Validation;
using Xunit;

namespace NodePilot.Api.Tests.Validation;

public class WorkflowDefinitionValidatorTests
{
    private static WorkflowDefinitionValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return WorkflowDefinitionValidator.Validate(doc.RootElement);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"steps":[]}""")]
    [InlineData("""{"nodes":[],"edges":[]}""")]
    public void Validate_AllowsEmptyOrLegacyObjectShapes(string json)
    {
        Validate(json).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_AllowsParserCompatibleReactFlowShape()
    {
        var result = Validate("""
        {
            "nodes": [
                {"id":"a","type":"activity","data":{"label":"A","activityType":"manualTrigger"}},
                {"id":"b","type":"activity","data":{"label":"B","activityType":"runScript"}}
            ],
            "edges": [
                {"id":"e1","source":"a","target":"b","data":{"label":"Next"}}
            ]
        }
        """);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_RejectsNodeWithoutData()
    {
        var result = Validate("""{"nodes":[{"id":"a","type":"activity"}],"edges":[]}""");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("nodes[0].data");
    }

    [Fact]
    public void Validate_RejectsGenericNodeTypeWithoutActivityType()
    {
        var result = Validate("""{"nodes":[{"id":"a","type":"activity","data":{}}],"edges":[]}""");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("activityType");
    }

    [Fact]
    public void Validate_RejectsEdgesThatReferenceUnknownNodes()
    {
        var result = Validate("""
        {
            "nodes": [{"id":"a","type":"activity","data":{"activityType":"manualTrigger"}}],
            "edges": [{"id":"e1","source":"a","target":"missing"}]
        }
        """);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("unknown node 'missing'");
    }

    [Fact]
    public void Validate_RejectsDuplicateNodeIds()
    {
        var result = Validate("""
        {
            "nodes": [
                {"id":"a","type":"activity","data":{"activityType":"manualTrigger"}},
                {"id":"a","type":"activity","data":{"activityType":"runScript"}}
            ],
            "edges": []
        }
        """);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("duplicate node id");
    }

    [Fact]
    public void Validate_RejectsOptionalStringsThatWouldThrowInParser()
    {
        var result = Validate("""{"nodes":[{"id":"a","data":{"activityType":"runScript","label":42}}],"edges":[]}""");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("label");
    }

    [Fact]
    public void Validate_RejectsUnknownActivityType()
    {
        var result = Validate("""{"nodes":[{"id":"a","type":"activity","data":{"activityType":"madeUpActivity"}}],"edges":[]}""");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("madeUpActivity");
    }

    [Fact]
    public void Validate_AllowsStickyNoteWithoutCatalogActivity()
    {
        var result = Validate("""
        {
            "nodes": [
                {"id":"n1","type":"stickyNote","data":{"label":"Note","activityType":"note","text":"phase A"}}
            ],
            "edges": []
        }
        """);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_AllowsGroupNodeWithoutCatalogActivity()
    {
        var result = Validate("""
        {
            "nodes": [
                {"id":"n1","type":"group","data":{"label":"Phase A"}}
            ],
            "edges": []
        }
        """);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_StickyNoteStillRequiresDataObject()
    {
        var result = Validate("""{"nodes":[{"id":"n1","type":"stickyNote"}],"edges":[]}""");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("data");
    }

    [Fact]
    public void Validate_StickyNoteLabelMustBeString()
    {
        var result = Validate("""{"nodes":[{"id":"n1","type":"stickyNote","data":{"label":42}}],"edges":[]}""");

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("label");
    }
}

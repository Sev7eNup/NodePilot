using FluentAssertions;
using NodePilot.Api.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// A live waitNofM junction must be satisfiable by its enabled inputs; the scheduler would
/// otherwise skip it and everything after it on every run.
/// </summary>
public class JunctionRequiredCountValidationTests
{
    private static string Definition(string mode, int requiredCount, bool disableEdge = false, bool disableJunction = false) =>
        $$"""
        {
          "nodes": [
            { "id": "t", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
            { "id": "a", "type": "activity", "data": { "activityType": "log", "config": {} } },
            { "id": "b", "type": "activity", "data": { "activityType": "log", "config": {} } },
            { "id": "j", "type": "activity", "data": { "activityType": "junction", "disabled": {{(disableJunction ? "true" : "false")}},
              "config": { "mode": "{{mode}}", "requiredCount": {{requiredCount}} } } }
          ],
          "edges": [
            { "id": "e1", "source": "t", "target": "a" },
            { "id": "e2", "source": "t", "target": "b" },
            { "id": "e3", "source": "a", "target": "j" },
            { "id": "e4", "source": "b", "target": "j", "data": { "disabled": {{(disableEdge ? "true" : "false")}} } }
          ]
        }
        """;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ValidateDefinition_RequiredCountWithinInputs_IsAccepted(int requiredCount)
        => JunctionRequiredCountValidation.ValidateDefinition(Definition("waitNofM", requiredCount)).Should().BeNull();

    [Fact]
    public void ValidateDefinition_RequiredCountAboveInputs_IsReported()
    {
        var error = JunctionRequiredCountValidation.ValidateDefinition(Definition("waitNofM", 3));

        error.Should().NotBeNull();
        error.Should().Contain("'j'").And.Contain("3").And.Contain("2");
    }

    [Fact]
    public void ValidateDefinition_DisabledIncomingEdge_DoesNotCountAsAnInput()
        => JunctionRequiredCountValidation.ValidateDefinition(Definition("waitNofM", 2, disableEdge: true))
            .Should().NotBeNull("the scheduler never sees a disabled edge");

    [Fact]
    public void ValidateDefinition_OtherJunctionModes_IgnoreRequiredCount()
    {
        JunctionRequiredCountValidation.ValidateDefinition(Definition("waitAll", 5)).Should().BeNull();
        JunctionRequiredCountValidation.ValidateDefinition(Definition("waitAny", 5)).Should().BeNull();
    }

    [Fact]
    public void ValidateDefinition_DisabledJunction_IsIgnored()
        => JunctionRequiredCountValidation.ValidateDefinition(Definition("waitNofM", 5, disableJunction: true))
            .Should().BeNull();

    [Fact]
    public void ValidateDefinition_MalformedDefinition_IsLeftToStructuralValidation()
        => JunctionRequiredCountValidation.ValidateDefinition("not json").Should().BeNull();
}

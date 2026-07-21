using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine;
using NodePilot.Engine.Conditions;
using Xunit;

namespace NodePilot.Engine.Tests.Conditions;

public class EvaluateConditionTests
{
    [Fact]
    public void EvaluateCondition_StepSuccess_ReturnsTrueWhenSucceeded()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true }
        };

        var result = ConditionEvaluator.EvaluateLegacy("step1.success", results);

        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_StepSuccess_ReturnsFalseWhenFailed()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = false }
        };

        var result = ConditionEvaluator.EvaluateLegacy("step1.success", results);

        result.Should().BeFalse();
    }

    [Fact]
    public void EvaluateCondition_StepFailed_ReturnsTrueWhenFailed()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = false }
        };

        var result = ConditionEvaluator.EvaluateLegacy("step1.failed", results);

        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_StepFailed_ReturnsFalseWhenSucceeded()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true }
        };

        var result = ConditionEvaluator.EvaluateLegacy("step1.failed", results);

        result.Should().BeFalse();
    }

    [Fact]
    public void EvaluateCondition_UnknownStep_ReturnsTrue()
    {
        var results = new Dictionary<string, ActivityResult>();

        var result = ConditionEvaluator.EvaluateLegacy("nonexistent.success", results);

        result.Should().BeTrue();
    }

    [Fact]
    public void EvaluateCondition_MalformedCondition_ReturnsTrue()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true }
        };

        var result = ConditionEvaluator.EvaluateLegacy("malformed-no-dot", results);

        result.Should().BeTrue();
    }
}

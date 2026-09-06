using FluentAssertions;
using NodePilot.Core.Interfaces;
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
    public void EvaluateCondition_UnknownStep_ReturnsFalse()
    {
        // A step without a result in this run neither succeeded nor failed — the edge stays shut.
        var results = new Dictionary<string, ActivityResult>();

        ConditionEvaluator.EvaluateLegacy("nonexistent.success", results).Should().BeFalse();
        ConditionEvaluator.EvaluateLegacy("nonexistent.failed", results).Should().BeFalse();
    }

    [Theory]
    [InlineData("malformed-no-dot")]
    [InlineData("step1.done")]
    [InlineData(".success")]
    public void EvaluateCondition_MalformedCondition_Throws(string condition)
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true }
        };

        var act = () => ConditionEvaluator.EvaluateLegacy(condition, results);

        act.Should().Throw<ConditionEvaluationException>()
            .WithMessage("*must have the form <stepId>.success or <stepId>.failed*");
    }

    [Fact]
    public void EvaluateCondition_OutputVariableAlias_ResolvesToTheStep()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["step-1"] = new() { Success = false }
        };
        var aliases = new Dictionary<string, string> { ["hostInfo"] = "step-1" };

        ConditionEvaluator.EvaluateLegacy("hostInfo.failed", results, aliases).Should().BeTrue();
        ConditionEvaluator.EvaluateLegacy("hostInfo.success", results, aliases).Should().BeFalse();
    }
}

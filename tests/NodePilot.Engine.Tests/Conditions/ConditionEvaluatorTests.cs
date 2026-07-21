using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Conditions;
using Xunit;

namespace NodePilot.Engine.Tests.Conditions;

public class ConditionEvaluatorTests
{
    private static JsonElement Expr(string json) => JsonDocument.Parse(json).RootElement;

    private static Dictionary<string, ActivityResult> MakeResults()
        => new()
        {
            ["stepA"] = new() { Success = true, Output = "Hello World", OutputParameters = new() { ["status"] = "Running", ["count"] = "42" } },
            ["stepB"] = new() { Success = false, Output = "", ErrorOutput = "boom" },
        };

    [Fact]
    public void Evaluate_EqualsString_Matches()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""Hello World""}}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_EqualsNumeric_CoercesDecimal()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""param"",""paramName"":""count""},""op"":"">"",""right"":{""kind"":""literal"",""value"":""10""}}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_Contains_PositiveAndNegative()
    {
        var pos = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""contains"",""right"":{""kind"":""literal"",""value"":""World""}}");
        var neg = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""contains"",""right"":{""kind"":""literal"",""value"":""xyz""}}");
        ConditionEvaluator.Evaluate(pos, MakeResults()).Should().BeTrue();
        ConditionEvaluator.Evaluate(neg, MakeResults()).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_StartsWithEndsWith()
    {
        var sw = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""startsWith"",""right"":{""kind"":""literal"",""value"":""Hello""}}");
        var ew = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""endsWith"",""right"":{""kind"":""literal"",""value"":""World""}}");
        ConditionEvaluator.Evaluate(sw, MakeResults()).Should().BeTrue();
        ConditionEvaluator.Evaluate(ew, MakeResults()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_MatchesRegex()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""matches"",""right"":{""kind"":""literal"",""value"":""^Hello.*""}}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_IsEmpty_OnMissingVariable()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""ghost"",""field"":""output""},""op"":""isEmpty""}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_IsNotEmpty_OnPresent()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""isNotEmpty""}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_AndGroup_ShortCircuits()
    {
        var expr = Expr(@"{""type"":""group"",""op"":""AND"",""children"":[
            {""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""param"",""paramName"":""status""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""Running""}},
            {""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""contains"",""right"":{""kind"":""literal"",""value"":""Hello""}}
        ]}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_OrGroup_TrueIfAny()
    {
        var expr = Expr(@"{""type"":""group"",""op"":""OR"",""children"":[
            {""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""nope""}},
            {""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""contains"",""right"":{""kind"":""literal"",""value"":""World""}}
        ]}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_NotInverts()
    {
        var expr = Expr(@"{""type"":""not"",""child"":{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""contains"",""right"":{""kind"":""literal"",""value"":""World""}}}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_SuccessField_MapsToBoolean()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""success""},""op"":""isTrue""}");
        var exprNeg = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepB"",""field"":""success""},""op"":""isTrue""}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
        ConditionEvaluator.Evaluate(exprNeg, MakeResults()).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_UnresolvedVariable_SafeFailsComparison()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""ghost"",""field"":""output""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""X""}}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_NotEquals()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""!="",""right"":{""kind"":""literal"",""value"":""Nope""}}");
        var exprEq = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""!="",""right"":{""kind"":""literal"",""value"":""Hello World""}}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
        ConditionEvaluator.Evaluate(exprEq, MakeResults()).Should().BeFalse();
    }

    [Theory]
    [InlineData("<", "42", "50", true)]
    [InlineData("<", "42", "42", false)]
    [InlineData("<=", "42", "42", true)]
    [InlineData("<=", "42", "41", false)]
    [InlineData(">=", "42", "42", true)]
    [InlineData(">=", "42", "50", false)]
    public void Evaluate_NumericOrdering(string op, string leftVal, string rightVal, bool expected)
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["stepA"] = new() { Output = leftVal },
        };
        var expr = Expr($@"{{""type"":""comparison"",""left"":{{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""}},""op"":""{op}"",""right"":{{""kind"":""literal"",""value"":""{rightVal}""}}}}");
        ConditionEvaluator.Evaluate(expr, results).Should().Be(expected);
    }

    [Fact]
    public void Evaluate_LiteralWithTemplate_ResolvesVariable()
    {
        // RHS contains {{stepA.param.status}} which resolves to "Running"
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""literal"",""value"":""Running""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""{{stepA.param.status}}""}}");
        ConditionEvaluator.Evaluate(expr, MakeResults()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_LiteralTemplate_WithAlias()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""{{myAlias.output}}""}}");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["myAlias"] = "stepA" };
        ConditionEvaluator.Evaluate(expr, MakeResults(), map).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_OutputVariableAlias_ResolvesViaMap()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""myAlias"",""field"":""output""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""Hello World""}}");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["myAlias"] = "stepA" };
        ConditionEvaluator.Evaluate(expr, MakeResults(), map).Should().BeTrue();
    }

    /// <summary>
    /// Pre-fix: an edge condition referencing <c>{{globals.ENV}}</c> evaluated against a
    /// literal-shaped operand silently returned "" (regex didn't match), so any comparison
    /// against a global was false-on-arrival regardless of the actual value. Both the
    /// literal-template pre-pass and the structured variable operand must now resolve them.
    /// </summary>
    [Fact]
    public void Evaluate_LiteralTemplate_ResolvesGlobalVariable()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""Hello {{globals.SUFFIX}}""}}");
        var globals = new Dictionary<string, string>(StringComparer.Ordinal) { ["SUFFIX"] = "World" };
        ConditionEvaluator.Evaluate(expr, MakeResults(), null, globals).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_StructuredVariableOperand_ResolvesGlobalSource()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""source"":""global"",""name"":""ENV""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""production""}}");
        var globals = new Dictionary<string, string>(StringComparer.Ordinal) { ["ENV"] = "production" };
        ConditionEvaluator.Evaluate(expr, MakeResults(), null, globals).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_LiteralTemplate_ResolvesManualParam()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""stepId"":""stepA"",""field"":""output""},""op"":""contains"",""right"":{""kind"":""literal"",""value"":""{{manual.who}}""}}");
        var manual = new Dictionary<string, string>(StringComparer.Ordinal) { ["who"] = "World" };
        ConditionEvaluator.Evaluate(expr, MakeResults(), null, null, manual).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_StructuredVariableOperand_ResolvesManualSource()
    {
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""source"":""manual"",""name"":""env""},""op"":""=="",""right"":{""kind"":""literal"",""value"":""staging""}}");
        var manual = new Dictionary<string, string>(StringComparer.Ordinal) { ["env"] = "staging" };
        ConditionEvaluator.Evaluate(expr, MakeResults(), null, null, manual).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_GlobalSource_MissingName_ReturnsEmpty()
    {
        // Safe-fail: a reference to an undeclared global returns "" rather than throwing,
        // matching the existing behaviour for missing step variables.
        var expr = Expr(@"{""type"":""comparison"",""left"":{""kind"":""variable"",""source"":""global"",""name"":""NOPE""},""op"":""isEmpty""}");
        var globals = new Dictionary<string, string>(StringComparer.Ordinal) { ["ENV"] = "production" };
        ConditionEvaluator.Evaluate(expr, MakeResults(), null, globals).Should().BeTrue();
    }
}

using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class DecisionActivityTests
{
    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement;

    private static StepExecutionContext Ctx(
        IReadOnlyDictionary<string, ActivityResult>? results = null,
        IReadOnlyDictionary<string, string>? globals = null,
        IReadOnlyDictionary<string, string>? manual = null)
        => new()
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "decision-1",
            PreviousResults = results,
            GlobalVariables = globals,
            InputParameters = manual,
        };

    [Fact]
    public async Task FirstMatchingCase_Wins()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["env"] = new ActivityResult { Success = true, OutputParameters = { ["value"] = "prod" } },
        };

        var cfg = Cfg(@"{
            ""defaultCaseName"":""default"",
            ""cases"":[
                { ""name"":""prod"", ""condition"":{
                    ""type"":""comparison"",
                    ""op"":""=="",
                    ""left"":{""kind"":""variable"",""stepId"":""env"",""field"":""param"",""paramName"":""value""},
                    ""right"":{""kind"":""literal"",""value"":""prod""}
                }},
                { ""name"":""staging"", ""condition"":{
                    ""type"":""comparison"",
                    ""op"":""=="",
                    ""left"":{""kind"":""variable"",""stepId"":""env"",""field"":""param"",""paramName"":""value""},
                    ""right"":{""kind"":""literal"",""value"":""staging""}
                }}
            ]
        }");

        var result = await new DecisionActivity().ExecuteAsync(Ctx(results), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["case"].Should().Be("prod");
        result.OutputParameters["matched"].Should().Be("true");
    }

    [Fact]
    public async Task GlobalOperand_ResolvesFromContext_StructuredAndLiteralTemplate()
    {
        // Regression: DecisionActivity used to call ConditionEvaluator without global/manual
        // context, so both a source:"global" operand and a {{globals.X}} literal resolved to ""
        // and silently took the wrong branch. Pin both forms.
        var cfg = Cfg(@"{
            ""defaultCaseName"":""default"",
            ""cases"":[
                { ""name"":""prod-structured"", ""condition"":{
                    ""type"":""comparison"",
                    ""op"":""=="",
                    ""left"":{""kind"":""variable"",""source"":""global"",""name"":""ENV""},
                    ""right"":{""kind"":""literal"",""value"":""production""}
                }},
                { ""name"":""prod-literal"", ""condition"":{
                    ""type"":""comparison"",
                    ""op"":""=="",
                    ""left"":{""kind"":""literal"",""value"":""env is {{globals.ENV}}""},
                    ""right"":{""kind"":""literal"",""value"":""env is production""}
                }}
            ]
        }");
        var globals = new Dictionary<string, string>(StringComparer.Ordinal) { ["ENV"] = "production" };

        var result = await new DecisionActivity().ExecuteAsync(Ctx(globals: globals), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["case"].Should().Be("prod-structured");
        result.OutputParameters["matched"].Should().Be("true");
    }

    [Fact]
    public async Task ManualOperand_ResolvesFromContext_StructuredAndLiteralTemplate()
    {
        var cfg = Cfg(@"{
            ""defaultCaseName"":""default"",
            ""cases"":[
                { ""name"":""no-match-structured"", ""condition"":{
                    ""type"":""comparison"",
                    ""op"":""=="",
                    ""left"":{""kind"":""variable"",""source"":""manual"",""name"":""stage""},
                    ""right"":{""kind"":""literal"",""value"":""prod""}
                }},
                { ""name"":""staging-literal"", ""condition"":{
                    ""type"":""comparison"",
                    ""op"":""contains"",
                    ""left"":{""kind"":""literal"",""value"":""stage={{manual.stage}}""},
                    ""right"":{""kind"":""literal"",""value"":""staging""}
                }}
            ]
        }");
        var manual = new Dictionary<string, string>(StringComparer.Ordinal) { ["stage"] = "staging" };

        var result = await new DecisionActivity().ExecuteAsync(Ctx(manual: manual), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["case"].Should().Be("staging-literal");
        result.OutputParameters["matched"].Should().Be("true");
    }

    [Fact]
    public async Task NoMatch_FallsBackToDefault()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["env"] = new ActivityResult { Success = true, OutputParameters = { ["value"] = "qa" } },
        };

        var cfg = Cfg(@"{
            ""defaultCaseName"":""fallback"",
            ""cases"":[
                { ""name"":""prod"", ""condition"":{
                    ""type"":""comparison"",
                    ""op"":""=="",
                    ""left"":{""kind"":""variable"",""stepId"":""env"",""field"":""param"",""paramName"":""value""},
                    ""right"":{""kind"":""literal"",""value"":""prod""}
                }}
            ]
        }");

        var result = await new DecisionActivity().ExecuteAsync(Ctx(results), cfg, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters["case"].Should().Be("fallback");
        result.OutputParameters["matched"].Should().Be("false");
    }

    [Fact]
    public async Task MissingCasesArray_UsesDefault()
    {
        var cfg = Cfg("{}");
        var result = await new DecisionActivity().ExecuteAsync(Ctx(), cfg, CancellationToken.None);
        result.Success.Should().BeTrue();
        result.OutputParameters["case"].Should().Be("default");
        result.OutputParameters["matched"].Should().Be("false");
    }

    [Fact]
    public async Task EmptyCasesArray_UsesDefault()
    {
        var cfg = Cfg("{\"cases\":[]}");
        var result = await new DecisionActivity().ExecuteAsync(Ctx(), cfg, CancellationToken.None);
        result.Success.Should().BeTrue();
        result.OutputParameters["case"].Should().Be("default");
    }

    [Fact]
    public async Task UnnamedCases_AreIgnored()
    {
        // A case without a 'name' can't be referenced by edges — it gets skipped.
        var cfg = Cfg(@"{
            ""cases"":[
                { ""condition"":{ ""type"":""comparison"",""op"":""isTrue"",
                    ""left"":{""kind"":""literal"",""value"":""true""}}}
            ]
        }");
        var result = await new DecisionActivity().ExecuteAsync(Ctx(), cfg, CancellationToken.None);
        result.OutputParameters["case"].Should().Be("default");
    }

    [Fact]
    public async Task MalformedCondition_DoesNotMatchAndContinues()
    {
        // The first case has a broken condition (no "type") → it must be skipped.
        // The second case matches.
        var cfg = Cfg(@"{
            ""cases"":[
                { ""name"":""bad"", ""condition"":{} },
                { ""name"":""good"", ""condition"":{
                    ""type"":""comparison"",
                    ""op"":""isTrue"",
                    ""left"":{""kind"":""literal"",""value"":""true""}
                }}
            ]
        }");
        var result = await new DecisionActivity().ExecuteAsync(Ctx(), cfg, CancellationToken.None);
        result.OutputParameters["case"].Should().Be("good");
    }

    [Fact]
    public async Task NullPreviousResults_DoesNotThrow()
    {
        // When the engine builds the context without PreviousResults (the step-tester path), the
        // decision node must not throw a NullReferenceException — it should just fall through to
        // the default case.
        var cfg = Cfg(@"{
            ""cases"":[
                { ""name"":""needs-upstream"", ""condition"":{
                    ""type"":""comparison"",""op"":""=="",
                    ""left"":{""kind"":""variable"",""stepId"":""x"",""field"":""output""},
                    ""right"":{""kind"":""literal"",""value"":""y""}
                }}
            ]
        }");
        var result = await new DecisionActivity().ExecuteAsync(Ctx(null), cfg, CancellationToken.None);
        result.Success.Should().BeTrue();
        result.OutputParameters["case"].Should().Be("default");
    }

    [Fact]
    public async Task GroupCondition_AndOr_Works()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["check"] = new ActivityResult
            {
                Success = true,
                OutputParameters = { ["a"] = "1", ["b"] = "2" },
            },
        };

        // a == 1 AND b == 2 -> true → matches "both"
        var cfg = Cfg(@"{
            ""cases"":[
                { ""name"":""both"", ""condition"":{
                    ""type"":""group"",""op"":""AND"",
                    ""children"":[
                        { ""type"":""comparison"",""op"":""=="",
                          ""left"":{""kind"":""variable"",""stepId"":""check"",""field"":""param"",""paramName"":""a""},
                          ""right"":{""kind"":""literal"",""value"":""1""}},
                        { ""type"":""comparison"",""op"":""=="",
                          ""left"":{""kind"":""variable"",""stepId"":""check"",""field"":""param"",""paramName"":""b""},
                          ""right"":{""kind"":""literal"",""value"":""2""}}
                    ]
                }}
            ]
        }");
        var result = await new DecisionActivity().ExecuteAsync(Ctx(results), cfg, CancellationToken.None);
        result.OutputParameters["case"].Should().Be("both");
    }
}

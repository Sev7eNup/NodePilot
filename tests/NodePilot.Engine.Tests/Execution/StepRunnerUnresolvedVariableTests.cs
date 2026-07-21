using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// Unit tests for <see cref="StepRunner.FindUnresolvedStepReferences"/>.
/// The method is the guard that detects leftover {{step.field}} placeholders after
/// variable resolution and causes the engine to fail the step with a clear error.
/// </summary>
public class StepRunnerUnresolvedVariableTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void FindUnresolvedStepReferences_FullyResolved_ReturnsEmpty()
    {
        var config = Parse("""{"level":"info","message":"hello world"}""");
        StepRunner.FindUnresolvedStepReferences("log", config).Should().BeEmpty();
    }

    [Fact]
    public void FindUnresolvedStepReferences_UnknownStepOutput_ReturnsPattern()
    {
        var config = Parse("""{"level":"info","message":"{{nonexistent.output}}"}""");
        var result = StepRunner.FindUnresolvedStepReferences("log", config);
        result.Should().ContainSingle().Which.Should().Be("{{nonexistent.output}}");
    }

    [Fact]
    public void FindUnresolvedStepReferences_UnknownStepError_ReturnsPattern()
    {
        var config = Parse("""{"level":"error","message":"{{step.error}}"}""");
        var result = StepRunner.FindUnresolvedStepReferences("log", config);
        result.Should().ContainSingle().Which.Should().Be("{{step.error}}");
    }

    [Fact]
    public void FindUnresolvedStepReferences_UnknownParam_ReturnsPattern()
    {
        var config = Parse("""{"url":"https://api.test/{{step.param.userId}}"}""");
        var result = StepRunner.FindUnresolvedStepReferences("restApi", config);
        result.Should().ContainSingle().Which.Should().Be("{{step.param.userId}}");
    }

    [Fact]
    public void FindUnresolvedStepReferences_MultipleUnresolved_ReturnsAll()
    {
        var config = Parse("""{"m1":"{{a.output}}","m2":"{{b.output}}"}""");
        var result = StepRunner.FindUnresolvedStepReferences("log", config);
        result.Should().HaveCount(2);
        result.Should().Contain("{{a.output}}");
        result.Should().Contain("{{b.output}}");
    }

    [Fact]
    public void FindUnresolvedStepReferences_DuplicatePattern_DeduplicatesResult()
    {
        var config = Parse("""{"m1":"{{a.output}}","m2":"{{a.output}}"}""");
        var result = StepRunner.FindUnresolvedStepReferences("log", config);
        result.Should().ContainSingle().Which.Should().Be("{{a.output}}");
    }

    [Fact]
    public void FindUnresolvedStepReferences_NoBraces_ReturnsEmpty()
    {
        var config = Parse("""{"level":"info","message":"literal text"}""");
        StepRunner.FindUnresolvedStepReferences("log", config).Should().BeEmpty();
    }

    // ---- Protected-field (sql / databaseTrigger) behaviour ----

    [Fact]
    public void FindUnresolvedStepReferences_SqlProtectedQueryField_IsIgnored()
    {
        // sql.query is in FieldsNotToResolve — leftover {{...}} there is intentional.
        // Only non-protected fields (e.g. a description) must be checked.
        var config = Parse("""{"query":"SELECT * FROM T WHERE id={{manual.id}}"}""");
        StepRunner.FindUnresolvedStepReferences("sql", config).Should().BeEmpty();
    }

    [Fact]
    public void FindUnresolvedStepReferences_SqlUnprotectedField_IsChecked()
    {
        var config = Parse("""{"query":"SELECT 1","description":"{{missing.output}}"}""");
        var result = StepRunner.FindUnresolvedStepReferences("sql", config);
        result.Should().ContainSingle().Which.Should().Be("{{missing.output}}");
    }

    [Fact]
    public void FindUnresolvedStepReferences_DatabaseTriggerQueryField_IsIgnored()
    {
        var config = Parse("""{"query":"SELECT id FROM jobs WHERE ts > {{prev.param.sentinel}}"}""");
        StepRunner.FindUnresolvedStepReferences("databaseTrigger", config).Should().BeEmpty();
    }

    // ---- Patterns that look like {{...}} but do NOT match StepPattern ----

    [Fact]
    public void FindUnresolvedStepReferences_GlobalsPattern_IsNotFlagged()
    {
        // {{globals.NAME}} is a different pattern resolved by GlobalsPattern — StepRunner
        // only checks step-reference patterns (output / error / param.xxx).
        var config = Parse("""{"level":"info","message":"{{globals.UNKNOWN_KEY}}"}""");
        StepRunner.FindUnresolvedStepReferences("log", config).Should().BeEmpty();
    }

    // ---- .success tail (added 2026-05-17 — fixture-audit fallout) ----

    [Fact]
    public void FindUnresolvedStepReferences_UnknownStepSuccess_IsFlagged()
    {
        // {{step.success}} used to slip past the regex entirely and survive as a
        // literal string in returnData / log messages. Now it's part of StepPattern
        // and must be caught when the step is unknown.
        var config = Parse("""{"message":"step ok? {{missing.success}}"}""");
        var result = StepRunner.FindUnresolvedStepReferences("log", config);
        result.Should().ContainSingle().Which.Should().Be("{{missing.success}}");
    }

    // ---- Granular diagnostic (A: step-missing vs param-missing vs value-empty) ----

    [Fact]
    public void FormatUnresolvedDiagnostic_StepMissing_NamesTheStep()
    {
        var diag = StepRunner.FormatUnresolvedDiagnostic(
            unresolved: new[] { "{{wmi_os.param.Caption}}" },
            previousResults: new Dictionary<string, ActivityResult>(),
            outputVariableToStepId: new Dictionary<string, string>());

        diag.Should().Contain("Missing step");
        diag.Should().Contain("{{wmi_os.param.Caption}}");
    }

    [Fact]
    public void FormatUnresolvedDiagnostic_ParamMissing_NamesParamAndListsAvailable()
    {
        // wmiQuery producer ran successfully but emits no per-property params.
        // The author needs to see WHICH param keys ARE available so they can
        // either fix the reference or realise the activity doesn't expose them.
        var results = new Dictionary<string, ActivityResult>
        {
            ["wmi-1"] = new()
            {
                Success = true,
                Output = "raw CIM dump",
                OutputParameters = new Dictionary<string, string> { ["count"] = "1" },
            },
        };
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wmi_os"] = "wmi-1",
        };

        var diag = StepRunner.FormatUnresolvedDiagnostic(
            unresolved: new[] { "{{wmi_os.param.Caption}}" },
            previousResults: results,
            outputVariableToStepId: aliases);

        diag.Should().NotContain("Missing step",
            "the step DOES exist — only the param is wrong");
        diag.Should().Contain("did not emit param(s) [Caption]");
        diag.Should().Contain("count",
            "the diagnostic must list available param keys so the author knows what they CAN reach");
    }

    [Fact]
    public void FormatUnresolvedDiagnostic_ParamMissing_ListsNoneWhenProducerEmitsZero()
    {
        // Edge case: producer ran but OutputParameters is empty (wmiQuery in its
        // original form). We still want a useful message rather than an empty list.
        var results = new Dictionary<string, ActivityResult>
        {
            ["wmi-1"] = new()
            {
                Success = true,
                Output = "raw CIM dump",
                OutputParameters = new Dictionary<string, string>(),
            },
        };
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wmi_os"] = "wmi-1",
        };

        var diag = StepRunner.FormatUnresolvedDiagnostic(
            unresolved: new[] { "{{wmi_os.param.Caption}}" },
            previousResults: results,
            outputVariableToStepId: aliases);

        diag.Should().Contain("(available: (none))");
    }

    [Fact]
    public void FormatUnresolvedDiagnostic_MixedFailures_SegregatesBuckets()
    {
        // One missing-step + one missing-param + one empty-value should yield three
        // distinct sections so an author repairing a busted workflow can fix each
        // class of failure independently.
        var results = new Dictionary<string, ActivityResult>
        {
            ["s1"] = new()
            {
                Success = true,
                Output = "",
                OutputParameters = new Dictionary<string, string> { ["x"] = "1" },
            },
        };

        var diag = StepRunner.FormatUnresolvedDiagnostic(
            unresolved: new[]
            {
                "{{ghost.output}}",
                "{{s1.param.missing}}",
                "{{s1.output}}",
            },
            previousResults: results,
            outputVariableToStepId: new Dictionary<string, string>());

        diag.Should().Contain("Missing step");
        diag.Should().Contain("{{ghost.output}}");
        diag.Should().Contain("did not emit param(s) [missing]");
        diag.Should().Contain("empty");
        diag.Should().Contain("output");
    }

    [Fact]
    public void FormatUnresolvedDiagnostic_GroupsParamsBySteppLabel()
    {
        // Common case: returnData references three params from the same producer
        // step. We want one grouped sentence per step instead of N near-identical
        // ones — the screenshot the user posted had this exact pattern.
        var results = new Dictionary<string, ActivityResult>
        {
            ["wmi-1"] = new()
            {
                Success = true,
                OutputParameters = new Dictionary<string, string>(),
            },
        };
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wmi_os"] = "wmi-1",
        };

        var diag = StepRunner.FormatUnresolvedDiagnostic(
            unresolved: new[]
            {
                "{{wmi_os.param.Caption}}",
                "{{wmi_os.param.Version}}",
                "{{wmi_os.param.BuildNumber}}",
            },
            previousResults: results,
            outputVariableToStepId: aliases);

        // One "did not emit param(s) [...]" sentence carrying ALL three names.
        diag.Should().Contain("did not emit param(s) [Caption, Version, BuildNumber]");
        diag.Split("did not emit").Length.Should().Be(2,
            "the diagnostic must coalesce repeated step references into a single sentence");
    }

    [Fact]
    public void FormatUnresolvedDiagnostic_AlwaysIncludesRawTokenList()
    {
        // Even when we route the diagnosis into buckets, the raw token list at the
        // start is useful for log-grep / dashboards.
        var diag = StepRunner.FormatUnresolvedDiagnostic(
            unresolved: new[] { "{{x.output}}", "{{y.param.z}}" },
            previousResults: new Dictionary<string, ActivityResult>(),
            outputVariableToStepId: new Dictionary<string, string>());

        diag.Should().StartWith("Unresolved template variable(s): {{x.output}}, {{y.param.z}}.");
    }
}

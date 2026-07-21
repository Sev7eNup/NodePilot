using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

public class ResolveVariablesTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    private static WorkflowNode MakeNode(string id, string? outputVariable = null) =>
        new()
        {
            Id = id,
            Type = "runScript",
            Data = new WorkflowNodeData { OutputVariable = outputVariable }
        };

    [Fact]
    public void ResolveVariables_OutputPlaceholder_ReplacesWithStepOutput()
    {
        var config = Parse("""{ "message": "{{step1.output}}" }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true, Output = "hello world" }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        resolved.GetProperty("message").GetString().Should().Be("hello world");
    }

    [Fact]
    public void ResolveVariables_ErrorPlaceholder_ReplacesWithStepError()
    {
        var config = Parse("""{ "errMsg": "{{step1.error}}" }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = false, ErrorOutput = "something broke" }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        resolved.GetProperty("errMsg").GetString().Should().Be("something broke");
    }

    [Fact]
    public void ResolveVariables_OutputVariable_UsesCustomName()
    {
        var config = Parse("""{ "val": "{{diskCheck.output}}" }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true, Output = "90% free" }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1", "diskCheck") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        resolved.GetProperty("val").GetString().Should().Be("90% free");
    }

    [Fact]
    public void ResolveVariables_UnknownVariable_LeavesPlaceholderUnresolved()
    {
        var config = Parse("""{ "val": "{{missing.output}}" }""");
        var results = new Dictionary<string, ActivityResult>();
        var nodes = new List<WorkflowNode>();

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        resolved.GetProperty("val").GetString().Should().Be("{{missing.output}}");
    }

    [Fact]
    public void ResolveVariables_NoPlaceholders_ReturnsOriginalConfig()
    {
        var config = Parse("""{ "script": "Get-Process" }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true, Output = "data" }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        resolved.GetProperty("script").GetString().Should().Be("Get-Process");
    }

    [Fact]
    public void ResolveVariables_SpecialCharsInOutput_EscapesForJson()
    {
        var config = Parse("""{ "val": "{{step1.output}}" }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true, Output = "line1\nline2\ttab \"quoted\" back\\" }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        // The resolved JSON should be valid and the string value should contain the original special chars
        resolved.GetProperty("val").GetString().Should().Be("line1\nline2\ttab \"quoted\" back\\");
    }

    [Fact]
    public void ResolveVariables_CaseInsensitiveLookup()
    {
        var config = Parse("""{ "val": "{{STEP1.output}}" }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true, Output = "found it" }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        resolved.GetProperty("val").GetString().Should().Be("found it");
    }

    /// <summary>
    /// Regression test for a JsonDocument pool-leak fix: Parse is wrapped in `using var doc` and
    /// the method returns `doc.RootElement.Clone()`. Without Clone(), the returned JsonElement
    /// would point into a disposed document and any access would throw ObjectDisposedException.
    /// This test explicitly stores the result, lets the resolver-method's local doc go
    /// out of scope, then accesses nested properties to prove the cloned element is
    /// self-contained — the leak fix is correct.
    /// </summary>
    [Fact]
    public void ResolveVariables_ResultElement_IsDetachedFromInternalDocument()
    {
        var config = Parse("""{ "outer": { "msg": "{{step1.output}}", "n": 42 } }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true, Output = "alive" }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        // Drop our reference to any locals that might be holding the parse buffer.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Nested access after GC must still work — the returned element is a deep-copy
        // (Clone) and lives independently of any pooled buffer the resolver borrowed.
        var act = () =>
        {
            var outer = resolved.GetProperty("outer");
            outer.GetProperty("msg").GetString().Should().Be("alive");
            outer.GetProperty("n").GetInt32().Should().Be(42);
        };
        act.Should().NotThrow("the cloned JsonElement must outlive the resolver's internal JsonDocument");
    }

    /// <summary>
    /// Smoke test that exercises the resolver tightly so the JsonDocument dispose-and-clone
    /// path (see the pool-leak fix above) is hit thousands of times without functional
    /// regression. Pre-fix, this would have leaked an ArrayPool buffer per call; post-fix,
    /// every loop iteration returns its buffer and the test runs flat in memory.
    /// </summary>
    [Fact]
    public void ResolveVariables_ManyIterations_StaysFunctionallyCorrect()
    {
        var config = Parse("""{ "msg": "{{step1.output}}", "err": "{{step1.error}}" }""");
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        for (var i = 0; i < 1000; i++)
        {
            var results = new Dictionary<string, ActivityResult>
            {
                ["step1"] = new()
                {
                    Success = true,
                    Output = $"out-{i}",
                    ErrorOutput = $"err-{i}",
                }
            };
            var resolved = VariableResolver.ResolveVariables(config, results, nodes);

            // Spot-check three iterations to catch silent corruption without spamming
            // assertions on every loop.
            if (i is 0 or 500 or 999)
            {
                resolved.GetProperty("msg").GetString().Should().Be($"out-{i}");
                resolved.GetProperty("err").GetString().Should().Be($"err-{i}");
            }
        }
    }

    // ---- H-1: ResolveVariablesExcept skips named top-level fields ----

    [Fact]
    public void ResolveVariablesExcept_LeavesProtectedFieldVerbatim_AndResolvesOthers()
    {
        // The SQL Activity opts `query` out of substitution. Other fields (here:
        // `description`) still go through the normal resolver pass, so the same
        // workflow can use templates everywhere EXCEPT inside the raw SQL text.
        var config = Parse("""
            {
              "query": "SELECT * FROM T WHERE id = {{manual.id}}",
              "description": "ran for {{manual.id}}"
            }
            """);
        var results = new Dictionary<string, ActivityResult>();
        var protectedFields = new HashSet<string>(StringComparer.Ordinal) { "query" };

        var resolved = VariableResolver.ResolveVariablesExcept(
            config,
            results,
            outputVariableToStepId: null,
            globalVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            doNotResolveFields: protectedFields);

        resolved.GetProperty("query").GetString()
            .Should().Be("SELECT * FROM T WHERE id = {{manual.id}}");
        // The other field still passes through the resolver — `{{manual.id}}` has no
        // matching variable here, so it stays literal (resolver leaves unknown vars in place).
        resolved.GetProperty("description").GetString()
            .Should().Be("ran for {{manual.id}}");
    }

    [Fact]
    public void ResolveVariablesExcept_StillResolvesGlobalsInUnprotectedFields()
    {
        // Cross-check: globals applied to the resolved fields, but the protected field's
        // identical `{{globals.NAME}}` placeholder must survive untouched.
        var config = Parse("""
            {
              "query": "SELECT '{{globals.TAG}}'",
              "label": "{{globals.TAG}}"
            }
            """);
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TAG"] = "live",
        };
        var protectedFields = new HashSet<string>(StringComparer.Ordinal) { "query" };

        var resolved = VariableResolver.ResolveVariablesExcept(
            config,
            new Dictionary<string, ActivityResult>(),
            outputVariableToStepId: null,
            globalVariables: globals,
            doNotResolveFields: protectedFields);

        resolved.GetProperty("query").GetString().Should().Be("SELECT '{{globals.TAG}}'");
        resolved.GetProperty("label").GetString().Should().Be("live");
    }

    [Fact]
    public void ResolveVariablesExcept_EmptyProtectedSet_BehavesLikeNormalResolve()
    {
        var config = Parse("""{ "msg": "{{globals.GREETING}}" }""");
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GREETING"] = "hi",
        };

        var resolved = VariableResolver.ResolveVariablesExcept(
            config,
            new Dictionary<string, ActivityResult>(),
            outputVariableToStepId: null,
            globalVariables: globals,
            doNotResolveFields: new HashSet<string>());

        resolved.GetProperty("msg").GetString().Should().Be("hi");
    }

    // ---- .success property tail (fixture-audit fallout 2026-05-17) ----
    // Before this commit, {{step.success}} was silently passed through as literal
    // because the regex didn't match it. Authors typing {{init.success}} in
    // returnData got the string "{{init.success}}" persisted to OutputParameters
    // — the resolver never touched it, so the "unresolved variable" diagnostic (T-7.1)
    // never caught it either. These tests pin the new contract.

    [Fact]
    public void ResolveVariables_SuccessPlaceholder_TrueWhenStepSucceeded()
    {
        var config = Parse("""{ "ok": "{{step1.success}}" }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true, Output = "..." }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        resolved.GetProperty("ok").GetString().Should().Be("true");
    }

    [Fact]
    public void ResolveVariables_SuccessPlaceholder_FalseWhenStepFailed()
    {
        var config = Parse("""{ "ok": "{{step1.success}}" }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = false, ErrorOutput = "boom" }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        resolved.GetProperty("ok").GetString().Should().Be("false");
    }

    [Fact]
    public void ResolveVariables_SuccessPlaceholder_ResolvesViaOutputVariableAlias()
    {
        // Authors usually reference the configured alias, not the raw stepId.
        var config = Parse("""{ "ok": "{{diskCheck.success}}" }""");
        var results = new Dictionary<string, ActivityResult>
        {
            ["step-abc"] = new() { Success = true }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step-abc", "diskCheck") };

        var resolved = VariableResolver.ResolveVariables(config, results, nodes);

        resolved.GetProperty("ok").GetString().Should().Be("true");
    }

    [Fact]
    public void ResolveStringValue_SuccessPlaceholder_TrueWhenStepSucceeded()
    {
        // The single-string resolver feeds non-JSON fields (target-machine, credential).
        // Same .success contract must hold there to avoid mode-specific drift.
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = true }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveStringValue("{{step1.success}}", results, nodes);

        resolved.Should().Be("true");
    }

    [Fact]
    public void ResolveStringValue_SuccessPlaceholder_FalseWhenStepFailed()
    {
        var results = new Dictionary<string, ActivityResult>
        {
            ["step1"] = new() { Success = false, ErrorOutput = "x" }
        };
        var nodes = new List<WorkflowNode> { MakeNode("step1") };

        var resolved = VariableResolver.ResolveStringValue("{{step1.success}}", results, nodes);

        resolved.Should().Be("false");
    }

    [Fact]
    public void ResolveVariables_SuccessPlaceholder_LeavesUnknownStepUnresolved()
    {
        // .success on a step that hasn't run / doesn't exist must still leave the
        // placeholder literally so the "unresolved variable" diagnostic (T-7.1) can catch it
        // downstream — the WHOLE point of adding .success to the regex was to bring it under
        // that diagnostic's coverage.
        var config = Parse("""{ "ok": "{{missing.success}}" }""");
        var resolved = VariableResolver.ResolveVariables(
            config,
            new Dictionary<string, ActivityResult>(),
            new List<WorkflowNode>());

        resolved.GetProperty("ok").GetString().Should().Be("{{missing.success}}");
    }

    [Fact]
    public void BuildStepVariables_PopulatesSuccessKey_ForRunScriptResolver()
    {
        // runScript inlines its own resolver against the flat variables dict produced by
        // BuildStepVariables. If we forget to seed `.success`, scripts that write
        // `if ({{prev.success}}) { ... }` would silently keep the literal text — same
        // ghost-resolution bug we're fixing for the JSON-config path.
        var prev = new Dictionary<string, ActivityResult>
        {
            ["step-1"] = new() { Success = true },
            ["step-2"] = new() { Success = false },
        };

        var vars = VariableResolver.BuildStepVariables(
            inputParameters: null,
            globalVariables: new Dictionary<string, string>(),
            previousResults: prev,
            outputNameByStepId: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["step-1"] = "alpha",
            });

        // Both alias and raw-id keys must be present, mirroring the .output/.error contract.
        vars.Should().ContainKey("alpha.success").WhoseValue.Should().Be("true");
        vars.Should().ContainKey("step-1.success").WhoseValue.Should().Be("true");
        vars.Should().ContainKey("step-2.success").WhoseValue.Should().Be("false");
    }
}

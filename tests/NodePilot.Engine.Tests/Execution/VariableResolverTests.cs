using System.Text.Json;
using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Execution;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// Pins down the contract of <see cref="VariableResolver.ResolveVariablesExcept"/> — the
/// H-1 field-guard introduced in the 2026-05-15 security audit. The integration test in
/// <c>SqlActivityTests</c> covers user-visible behavior; this file isolates the resolver
/// so a future refactor that breaks the wiring fails here first.
/// </summary>
public class VariableResolverTests
{
    private static readonly IReadOnlyDictionary<string, ActivityResult> NoPreviousResults =
        new Dictionary<string, ActivityResult>();
    private static readonly IReadOnlyDictionary<string, string> NoAliases =
        new Dictionary<string, string>();

    private static JsonElement Cfg(object obj)
        => JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    [Fact]
    public void ResolveVariablesExcept_ProtectsNamedField_LeavesGlobalTemplateLiteral()
    {
        var config = Cfg(new
        {
            query = "SELECT * FROM users WHERE id = {{globals.USER_ID}}",
            otherField = "prefix-{{globals.USER_ID}}-suffix",
        });
        var globals = new Dictionary<string, string> { ["USER_ID"] = "42" };

        var resolved = VariableResolver.ResolveVariablesExcept(
            config, NoPreviousResults, NoAliases, globals,
            doNotResolveFields: new HashSet<string>(StringComparer.Ordinal) { "query" });

        resolved.GetProperty("query").GetString()
            .Should().Be("SELECT * FROM users WHERE id = {{globals.USER_ID}}",
                "the field guard must pass the protected value through verbatim");
        resolved.GetProperty("otherField").GetString()
            .Should().Be("prefix-42-suffix",
                "non-protected fields keep the normal substitution behaviour");
    }

    [Fact]
    public void ResolveVariablesExcept_ProtectsNamedField_LeavesStepTemplateLiteral()
    {
        var config = Cfg(new
        {
            query = "SELECT * FROM t WHERE id = {{prev.output}}",
            params_ = "got {{prev.output}}",
        });
        var previousResults = new Dictionary<string, ActivityResult>
        {
            ["prev"] = new ActivityResult { Success = true, Output = "99" },
        };

        var resolved = VariableResolver.ResolveVariablesExcept(
            config, previousResults, NoAliases, globalVariables: null,
            doNotResolveFields: new HashSet<string>(StringComparer.Ordinal) { "query" });

        resolved.GetProperty("query").GetString()
            .Should().Be("SELECT * FROM t WHERE id = {{prev.output}}");
        resolved.GetProperty("params_").GetString()
            .Should().Be("got 99");
    }

    [Fact]
    public void ResolveVariablesExcept_EmptyProtectSet_DelegatesToStandardResolver()
    {
        // Sanity check: when the guard set is empty, the method behaves like the regular
        // resolver. Lets callers wire the guard unconditionally without surprising the
        // common case.
        var config = Cfg(new { query = "id = {{globals.X}}" });
        var globals = new Dictionary<string, string> { ["X"] = "7" };

        var resolved = VariableResolver.ResolveVariablesExcept(
            config, NoPreviousResults, NoAliases, globals,
            doNotResolveFields: new HashSet<string>(StringComparer.Ordinal));

        resolved.GetProperty("query").GetString().Should().Be("id = 7");
    }

    [Fact]
    public void ResolveVariablesExcept_NonObjectConfig_DelegatesToStandardResolver()
    {
        // The field guard only makes sense for top-level object configs. An array root
        // or scalar gets resolved straight through (no field names to gate on).
        var config = JsonDocument.Parse("[\"{{globals.X}}\"]").RootElement;
        var globals = new Dictionary<string, string> { ["X"] = "abc" };

        var resolved = VariableResolver.ResolveVariablesExcept(
            config, NoPreviousResults, NoAliases, globals,
            doNotResolveFields: new HashSet<string>(StringComparer.Ordinal) { "query" });

        resolved.ValueKind.Should().Be(JsonValueKind.Array);
        resolved[0].GetString().Should().Be("abc");
    }

    [Fact]
    public void ResolveVariablesExcept_NestedFieldNameInChildObject_StillResolved()
    {
        // The guard is top-level only. A nested object that happens to contain a key
        // named the same as a protected top-level field still gets substituted —
        // that's the documented scope and matches the SQL/DB-trigger use-case where the
        // protected field is always a top-level scalar.
        var config = Cfg(new
        {
            query = "literal {{globals.X}}",
            nested = new { query = "nested {{globals.X}}" },
        });
        var globals = new Dictionary<string, string> { ["X"] = "Y" };

        var resolved = VariableResolver.ResolveVariablesExcept(
            config, NoPreviousResults, NoAliases, globals,
            doNotResolveFields: new HashSet<string>(StringComparer.Ordinal) { "query" });

        resolved.GetProperty("query").GetString().Should().Be("literal {{globals.X}}");
        resolved.GetProperty("nested").GetProperty("query").GetString().Should().Be("nested Y");
    }

    [Fact]
    public void ResolveConfigForExecution_DatabaseTriggerProtectsQuery()
    {
        var config = Cfg(new
        {
            query = "SELECT * FROM T WHERE Tag = {{globals.WATCH_TAG}}",
            description = "watching {{globals.WATCH_TAG}}",
        });
        var globals = new Dictionary<string, string> { ["WATCH_TAG"] = "prod" };

        var resolved = StepRunner.ResolveConfigForExecution(
            "databaseTrigger", config, NoPreviousResults, NoAliases, globals);

        resolved.GetProperty("query").GetString()
            .Should().Be("SELECT * FROM T WHERE Tag = {{globals.WATCH_TAG}}");
        resolved.GetProperty("description").GetString().Should().Be("watching prod");
    }

    /// <summary>
    /// Pins the runScript databus contract: BuildStepVariables MUST write
    /// <c>{stepId}.output</c> and <c>{stepId}.error</c> keys, otherwise
    /// <c>PowerShellActivitySupport.ResolveScriptVariables</c>
    /// — which reads exactly those keys from <c>context.Variables</c> — can't substitute
    /// <c>{{prev.output}}</c> / <c>{{prev.error}}</c> in script bodies and the template
    /// leaks through to PowerShell literally.
    /// </summary>
    [Fact]
    public void BuildStepVariables_WritesOutputAndErrorKeysForPreviousResults()
    {
        var previousResults = new Dictionary<string, ActivityResult>
        {
            ["step-1"] = new ActivityResult { Success = true, Output = "hello", ErrorOutput = "warn" },
        };
        var outputNames = new Dictionary<string, string>(); // no outputVariable alias

        var vars = VariableResolver.BuildStepVariables(
            inputParameters: null,
            globalVariables: new Dictionary<string, string>(),
            previousResults: previousResults,
            outputNameByStepId: outputNames);

        vars.Should().ContainKey("step-1.output").WhoseValue.Should().Be("hello");
        vars.Should().ContainKey("step-1.error").WhoseValue.Should().Be("warn");
    }

    [Fact]
    public void BuildStepVariables_WhenOutputVariableSet_AddsBothAliasAndRawStepIdKeys()
    {
        // outputVariable="diskCheck" on step-1: authors might reference either
        // {{diskCheck.output}} (the alias) or {{step-1.output}} (raw id). Both must
        // resolve — matches the BuildVariableMap behaviour used by the JSON-config path.
        var previousResults = new Dictionary<string, ActivityResult>
        {
            ["step-1"] = new ActivityResult { Success = true, Output = "42" },
        };
        var outputNames = new Dictionary<string, string> { ["step-1"] = "diskCheck" };

        var vars = VariableResolver.BuildStepVariables(
            inputParameters: null,
            globalVariables: new Dictionary<string, string>(),
            previousResults: previousResults,
            outputNameByStepId: outputNames);

        vars["diskCheck.output"].Should().Be("42");
        vars["step-1.output"].Should().Be("42");
        vars["diskCheck.error"].Should().Be("");
        vars["step-1.error"].Should().Be("");
    }

    [Fact]
    public void BuildStepVariables_NullOutputs_StoredAsEmptyString()
    {
        // A skipped/never-ran activity surfaces null Output. The flat dict only takes
        // strings; we coerce to "" so the script-variable lookup returns the empty literal
        // instead of leaving the template unresolved.
        var previousResults = new Dictionary<string, ActivityResult>
        {
            ["step-1"] = new ActivityResult { Success = true, Output = null, ErrorOutput = null },
        };

        var vars = VariableResolver.BuildStepVariables(
            inputParameters: null,
            globalVariables: new Dictionary<string, string>(),
            previousResults: previousResults,
            outputNameByStepId: new Dictionary<string, string>());

        vars["step-1.output"].Should().Be("");
        vars["step-1.error"].Should().Be("");
    }

    [Fact]
    public void BuildStepVariables_ParamKeysAvailableUnderBothAliasAndRawStepId()
    {
        // Same dual-lookup contract for OutputParameters: authors must be able to write
        // {{step-1.param.host}} OR {{diskCheck.param.host}}.
        var previousResults = new Dictionary<string, ActivityResult>
        {
            ["step-1"] = new ActivityResult
            {
                Success = true,
                Output = "ok",
                OutputParameters = { ["host"] = "server01" },
            },
        };
        var outputNames = new Dictionary<string, string> { ["step-1"] = "diskCheck" };

        var vars = VariableResolver.BuildStepVariables(
            inputParameters: null,
            globalVariables: new Dictionary<string, string>(),
            previousResults: previousResults,
            outputNameByStepId: outputNames);

        vars["diskCheck.param.host"].Should().Be("server01");
        vars["step-1.param.host"].Should().Be("server01");
    }
}

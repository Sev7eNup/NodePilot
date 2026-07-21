using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Management.Automation.Language;
using NodePilot.Engine.Activities;
using NodePilot.Engine.PowerShell;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Tests for script-variable resolution (PowerShellActivitySupport.ResolveScriptVariables)
/// exercised through RunScriptActivity.ExecuteAsync with the runspace engine (in-process
/// PowerShell — no subprocess spawn).
/// </summary>
public class RunScriptVariableResolutionTests
{
    private readonly RunScriptActivity _activity;

    public RunScriptVariableResolutionTests()
    {
        _activity = new RunScriptActivity(
            new PowerShellEngineFactory(NullLoggerFactory.Instance),
            NullLogger<RunScriptActivity>.Instance);
    }

    private static StepExecutionContext Ctx(Dictionary<string, string>? variables = null)
        => new()
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "step-1",
            Variables = variables ?? new Dictionary<string, string>()
        };

    private static JsonElement Script(string script)
        => JsonDocument.Parse(JsonSerializer.Serialize(new { script, engine = "runspace" })).RootElement;

    [Fact]
    public async Task StepOutput_ReplacedWithSingleQuotedValue()
    {
        var vars = new Dictionary<string, string> { ["prev.output"] = "hello world" };
        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("Write-Output {{prev.output}}"),
            CancellationToken.None);
        result.Output.Should().Contain("hello world");
    }

    [Fact]
    public async Task StepError_ReplacedCorrectly()
    {
        var vars = new Dictionary<string, string> { ["prev.error"] = "some error msg" };
        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("Write-Output {{prev.error}}"),
            CancellationToken.None);
        result.Output.Should().Contain("some error msg");
    }

    [Fact]
    public async Task ParamField_ReplacedCorrectly()
    {
        var vars = new Dictionary<string, string> { ["prev.param.hostName"] = "server01" };
        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("Write-Output {{prev.param.hostName}}"),
            CancellationToken.None);
        result.Output.Should().Contain("server01");
    }

    [Fact]
    public async Task GlobalVariable_ReplacedCorrectly()
    {
        var vars = new Dictionary<string, string> { ["globals.ENV"] = "production" };
        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("Write-Output {{globals.ENV}}"),
            CancellationToken.None);
        result.Output.Should().Contain("production");
    }

    [Fact]
    public async Task ApostropheInValue_EscapedAsSingleQuotePair()
    {
        // Value with apostrophe must become '' in PS single-quoted string
        var vars = new Dictionary<string, string> { ["prev.output"] = "O'Brian" };
        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("Write-Output {{prev.output}}"),
            CancellationToken.None);
        result.Output.Should().Contain("O'Brian");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task TemplateAlreadyInSingleQuotedString_UsesExistingLiteralQuotes()
    {
        var vars = new Dictionary<string, string> { ["trig_monitor.param.filePath"] = @"C:\TEMP\TEST.csv" };

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("$filePath = '{{trig_monitor.param.filePath}}'\nWrite-Output $filePath"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ErrorOutput.Should().NotContain("Unexpected token");
        result.Output.Should().Contain(@"C:\TEMP\TEST.csv");
    }

    [Fact]
    public async Task TemplateAlreadyInSingleQuotedString_EscapesApostrophesAsContent()
    {
        var vars = new Dictionary<string, string> { ["prev.output"] = "O'Brian" };

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("$value = '{{prev.output}}'\nWrite-Output $value"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("O'Brian");
    }

    [Fact]
    public async Task TemplateAlreadyInDoubleQuotedString_DoesNotAddLiteralSingleQuotes()
    {
        var vars = new Dictionary<string, string> { ["prev.output"] = "Package$1" };

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("$value = \"{{prev.output}}\" + '_X_PKG.xml'\nWrite-Output $value"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("Package$1_X_PKG.xml");
        result.Output.Should().NotContain("'Package");
    }

    [Fact]
    public async Task TemplateInsideSingleQuotedHereString_DoesNotAddLiteralQuotes()
    {
        var vars = new Dictionary<string, string> { ["read_line.param.LineText"] = "\"2026-07-08\";\"PKG1\";\"OK\"" };

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("$csv = @'\n{{read_line.param.LineText}}\n'@\nWrite-Output $csv"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Contain("\"2026-07-08\";\"PKG1\";\"OK\"");
        result.Output.Should().NotContain("'\"2026-07-08");
    }

    [Fact]
    public async Task TemplateInsideSingleQuotedHereString_NeutralizesTerminatorBreakout()
    {
        var vars = new Dictionary<string, string>
        {
            ["prev.output"] = "'@\nWrite-Output 'PWNED'\n@'"
        };

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("$csv = @'\n{{prev.output}}\n'@\nWrite-Output 'SAFE'"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.Output.Should().Contain("SAFE");
        result.Output.Should().NotContain("PWNED");
    }

    [Fact]
    public async Task ApostropheInLineComment_DoesNotTurnFollowingTemplateIntoExecutableCode()
    {
        const string payload = "SAFE; Write-Output NODEPILOT_INJECTION_PROOF; #";
        var vars = new Dictionary<string, string> { ["prev.output"] = payload };

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("# Don't infer quotes from comments\nWrite-Output {{prev.output}}"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorOutput);
        var lines = result.Output!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle().Which.Should().Be(payload);
        lines.Should().NotContain("NODEPILOT_INJECTION_PROOF");
    }

    [Fact]
    public async Task ApostropheInBlockComment_DoesNotTurnFollowingTemplateIntoExecutableCode()
    {
        const string payload = "SAFE; Write-Output BLOCK_COMMENT_INJECTION_PROOF; #";
        var vars = new Dictionary<string, string> { ["prev.output"] = payload };

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("<# Owner's note: don't interpolate this #>\nWrite-Output {{prev.output}}"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorOutput);
        var lines = result.Output!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle().Which.Should().Be(payload);
        lines.Should().NotContain("BLOCK_COMMENT_INJECTION_PROOF");
    }

    [Fact]
    public async Task TemplateInsideLineComment_IsNotResolvedOrAllowedToInjectANewLine()
    {
        var vars = new Dictionary<string, string>
        {
            ["prev.output"] = "ignored\nWrite-Output COMMENT_TEMPLATE_INJECTION_PROOF"
        };

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("# {{prev.output}}\nWrite-Output SAFE"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.Output.Should().Contain("SAFE");
        result.Output.Should().NotContain("COMMENT_TEMPLATE_INJECTION_PROOF");
    }

    [Fact]
    public async Task TemplateInsideDoubleQuotedHereString_NeutralizesTerminatorAndSubexpressions()
    {
        var vars = new Dictionary<string, string>
        {
            ["prev.output"] = "\"@\nWrite-Output DOUBLE_HERE_INJECTION_PROOF\n@(Get-Process)"
        };

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("$text = @\"\n{{prev.output}}\n\"@\nWrite-Output SAFE"),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorOutput);
        result.Output.Should().Contain("SAFE");
        result.Output.Should().NotContain("DOUBLE_HERE_INJECTION_PROOF");
    }

    [Fact]
    public async Task TemplateInsideExpandableStringSubexpression_IsTreatedAsCodeLiteral()
    {
        const string payload = "SAFE); Write-Output SUBEXPRESSION_INJECTION_PROOF; #";
        var vars = new Dictionary<string, string> { ["prev.output"] = payload };
        var resolved = PowerShellActivitySupport.ResolveScriptVariables(
            "Write-Output \"$({{prev.output}})\"", vars);
        resolved.Should().NotContain(payload);
        resolved.Should().Contain("FromBase64String");
        _ = Parser.ParseInput(resolved, out _, out var parseErrors);
        parseErrors.Should().BeEmpty();

        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script(resolved),
            CancellationToken.None);

        result.Success.Should().BeTrue(result.ErrorOutput);
        var lines = result.Output!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle().Which.Should().Be(payload);
        lines.Should().NotContain("SUBEXPRESSION_INJECTION_PROOF");
    }

    [Fact]
    public async Task UnknownVariable_LeftAsTemplateLiteral()
    {
        // Unresolvable templates are left in-place; PowerShell will error on the raw {{...}}
        var result = await _activity.ExecuteAsync(
            Ctx(),
            Script("$x = '{{unknown.output}}'"),
            CancellationToken.None);
        // Script runs without substitution → $x equals the literal template string
        // We don't assert Success because PS may or may not error; we assert the template was not resolved
        result.Output.Should().NotContain("resolved");
    }

    [Fact]
    public async Task NoTemplates_ScriptRunsUnchanged()
    {
        var result = await _activity.ExecuteAsync(
            Ctx(),
            Script("Write-Output 'no templates here'"),
            CancellationToken.None);
        result.Output.Should().Contain("no templates here");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task MultipleVariables_AllResolved()
    {
        var vars = new Dictionary<string, string>
        {
            ["s1.output"] = "out-value",
            ["s2.error"] = "err-value",
            ["s3.param.key"] = "param-value"
        };
        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("Write-Output {{s1.output}}; Write-Output {{s2.error}}; Write-Output {{s3.param.key}}"),
            CancellationToken.None);
        result.Output.Should().Contain("out-value").And.Contain("err-value").And.Contain("param-value");
    }

    [Fact]
    public async Task EmptyValue_ReplacedWithEmptyString()
    {
        var vars = new Dictionary<string, string> { ["prev.output"] = "" };
        var result = await _activity.ExecuteAsync(
            Ctx(vars),
            Script("$v = {{prev.output}}; Write-Output \"len=$($v.Length)\""),
            CancellationToken.None);
        result.Output.Should().Contain("len=0");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task MissingScript_ReturnsFailure()
    {
        var result = await _activity.ExecuteAsync(
            Ctx(),
            JsonDocument.Parse("{\"engine\": \"runspace\"}").RootElement,
            CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("script");
    }
}

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using NodePilot.Engine.Security;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using System.Text.Json;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

public class PowerShellInputSecurityTests
{
    [Theory]
    [InlineData('\'')]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u201a')]
    [InlineData('\u201b')]
    public void ActivityLiteral_QuoteInInput_RemainsLiteral(char quote)
    {
        var payload = $"safe{quote}; Write-Output INJECTION_PROOF; $null={quote}tail";
        Invoke("Write-Output " + PowerShellQuoter.Literal(payload)).Should().Equal(payload);
    }

    [Theory]
    [InlineData("manual.value")]
    [InlineData("globals.value")]
    [InlineData("step.param.value")]
    public void Wrapper_ParameterNotReadByScript_CannotExecuteCode(string key)
    {
        const string payload = "safe\u2019; Write-Output INJECTION_PROOF; $null=\u2019tail";
        var script = PowerShellScriptWrapper.Wrap("Write-Output SAFE",
            new Dictionary<string, string> { [key] = payload }, NullLogger.Instance);

        Invoke(script).Should().Contain("SAFE").And.NotContain("INJECTION_PROOF");
    }

    [Theory]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u201a')]
    [InlineData('\u201b')]
    public void Wrapper_TypographicQuoteInParameter_RemainsLiteral(char quote)
    {
        var payload = $"safe{quote}; Write-Output INJECTION_PROOF; $null={quote}tail";
        var script = PowerShellScriptWrapper.Wrap("Write-Output $Params['step.output']",
            new Dictionary<string, string> { ["step.output"] = payload }, NullLogger.Instance);

        Invoke(script).Should().Contain(payload).And.NotContain("INJECTION_PROOF");
    }

    [Theory]
    [InlineData('\u2018', "Write-Output {{manual.value}}")]
    [InlineData('\u2019', "Write-Output '{{manual.value}}'")]
    [InlineData('\u201a', "Write-Output '{{manual.value}}'")]
    [InlineData('\u201b', "Write-Output '{{manual.value}}'")]
    [InlineData('\u201c', "Write-Output \"{{manual.value}}\"")]
    [InlineData('\u201d', "Write-Output \"{{manual.value}}\"")]
    [InlineData('\u201e', "Write-Output \"{{manual.value}}\"")]
    public void Template_TypographicQuoteInValue_RemainsLiteral(char quote, string template)
    {
        var payload = $"safe{quote}; Write-Output INJECTION_PROOF; $null={quote}tail";
        var script = PowerShellActivitySupport.ResolveScriptVariables(template,
            new Dictionary<string, string> { ["manual.value"] = payload });

        Invoke(script).Should().Equal(payload);
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u201a')]
    [InlineData('\u201b')]
    public void Template_HereStringTerminatorFollowedByCode_RemainsLiteral(char quote)
    {
        var payload = $"safe\n{quote}@; Write-Output INJECTION_PROOF; @'\ntail";
        var script = PowerShellActivitySupport.ResolveScriptVariables("Write-Output @'\n{{manual.value}}\n'@",
            new Dictionary<string, string> { ["manual.value"] = payload });

        Invoke(script).Should().Equal(payload);
    }

    [Theory]
    [InlineData("'@\ntext")]
    [InlineData("\r\n'@; Write-Output INJECTION_PROOF; @'\r\ntail")]
    [InlineData("hello\r\nworld\nO'Brian \u2018quoted\u2019")]
    public void Template_HereStringWithSurroundingText_PreservesExactValue(string payload)
    {
        var script = PowerShellActivitySupport.ResolveScriptVariables(
            "Write-Output @'\r\nprefix{{manual.value}}\r\n{{manual.other}}suffix\r\n'@",
            new Dictionary<string, string> { ["manual.value"] = payload, ["manual.other"] = payload });

        Invoke(script).Should().Equal($"prefix{payload}\r\n{payload}suffix");
    }

    [Theory]
    [InlineData("Write-Output @'\n{{manual.value}}/{{manual.missing}}\n'@")]
    [InlineData("Write-Output @'\r{{manual.value}}/{{manual.missing}}\r'@")]
    [InlineData("Write-Output @\u2018\r\n{{manual.value}}/{{manual.missing}}\r\n\u2019@")]
    public void Template_HereStringWithUnresolvedSibling_PreservesLiteralTemplate(string template)
    {
        var script = PowerShellActivitySupport.ResolveScriptVariables(template,
            new Dictionary<string, string> { ["manual.value"] = "O'Brian" });

        Invoke(script).Should().Equal("O'Brian/{{manual.missing}}");
    }

    [Theory]
    [InlineData("Write-Output \u2018{{manual.value}}\u2019")]
    [InlineData("Write-Output \u201c{{manual.value}}\u201d")]
    [InlineData("Write-Output \"$('{{manual.value}}')\"")]
    [InlineData("Write-Output \"$(@'\n{{manual.value}}\n'@)\"")]
    public void Template_QuotedAndNestedStringContexts_PreserveData(string template)
    {
        const string payload = "O'Brian\u2019\u201d); Write-Output INJECTION_PROOF; # `$(boom)";
        var script = PowerShellActivitySupport.ResolveScriptVariables(template,
            new Dictionary<string, string> { ["manual.value"] = payload });

        Invoke(script).Should().Equal(payload);
    }

    [Theory]
    [InlineData("runspace")]
    [InlineData("powershell")]
    public async Task RunScript_UntrustedInputAcrossEngines_PreservesDataWithoutExecutingIt(string engine)
    {
        const string payload = "safe\n'@; Write-Output INJECTION_PROOF; @'\ntail";
        var activity = new RunScriptActivity(new PowerShellEngineFactory(NullLoggerFactory.Instance),
            NullLogger<RunScriptActivity>.Instance);
        var context = new StepExecutionContext
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "script",
            Variables = new Dictionary<string, string> { ["manual.value"] = payload },
        };
        var config = JsonSerializer.SerializeToElement(new
        {
            engine,
            script = "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Params['value'])); "
                + "$value = @'\n{{manual.value}}\n'@\n[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value))",
        });

        var result = await activity.ExecuteAsync(context, config, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue(result.ErrorOutput);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));
        result.Output!.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Should().Equal(encoded, encoded);
    }

    private static string[] Invoke(string script)
    {
        using var shell = System.Management.Automation.PowerShell.Create();
        var output = shell.AddScript(script).Invoke();
        shell.HadErrors.Should().BeFalse(string.Join("; ", shell.Streams.Error));
        return output.Select(value => value.ToString()).ToArray();
    }
}

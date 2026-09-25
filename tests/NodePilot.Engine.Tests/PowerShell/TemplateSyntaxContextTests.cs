using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Where a <c>{{...}}</c> template may stand in a runScript. Accepted positions must evaluate to
/// the value; positions where the substituted value would change how the script parses must be
/// rejected before the script runs.
/// </summary>
public class TemplateSyntaxContextTests
{
    private static readonly Dictionary<string, string> Variables = new()
    {
        ["t.param.n"] = "50",
        ["t.param.x"] = "2",
        ["t.param.y"] = "3",
        ["t.output"] = "O'Brian",
        ["u.output"] = "second",
    };

    private static string Resolve(string script) => PowerShellActivitySupport.ResolveScriptVariables(script, Variables);

    private static string[] Invoke(string script)
    {
        using var shell = System.Management.Automation.PowerShell.Create();
        var output = shell.AddScript(script).Invoke();
        shell.HadErrors.Should().BeFalse(string.Join("; ", shell.Streams.Error));
        return output.Select(value => value.ToString()).ToArray();
    }

    [Fact]
    public void CastBeforeTemplate_IsResolvedToACastOfTheQuotedValue()
    {
        var script = Resolve("$tail = [int]{{t.param.n}}\n$tail + 1");

        script.Should().Contain("[int]'50'");
        Invoke(script).Should().Equal("51");
    }

    [Fact]
    public void CastInsideSubexpressionOfADoubleQuotedString_IsResolved()
        => Invoke(Resolve("\"n=$([int]{{t.param.n}})\"")).Should().Equal("n=50");

    [Fact]
    public void SeveralCastTemplatesOnOneLine_AreResolved()
        => Invoke(Resolve("$a = [int]{{t.param.x}} + [int]{{t.param.y}}\n$a")).Should().Equal("5");

    [Fact]
    public void TemplateAsOperandAfterAnOperator_IsResolved()
        => Invoke(Resolve("$n = 70\nif ($n -gt {{t.param.n}}) { 'bigger' }")).Should().Equal("bigger");

    [Theory]
    [InlineData("@'\n{{t.output}}\n'@")]
    [InlineData("@\"\n{{t.output}}\n\"@")]
    public void TemplateInAHereString_IsResolved(string template)
        => Invoke(Resolve(template)).Should().Equal("O'Brian");

    [Fact]
    public void TemplateInAComment_StaysVerbatim()
    {
        var script = Resolve("# {{t.output}}\n'ok'");

        script.Should().Contain("# {{t.output}}");
        Invoke(script).Should().Equal("ok");
    }

    [Theory]
    [InlineData("Write-Output {{t.param.n}}\\app.txt", "50\\app.txt")]
    [InlineData("Write-Output {{t.output}}\\sub", "O'Brian\\sub")]
    [InlineData("Write-Output {{t.param.x}}-{{t.param.y}}.log", "2-3.log")]
    [InlineData("Write-Output {{t.param.x}}\\{{t.output}}\\x", "2\\O'Brian\\x")]
    [InlineData("Write-Output -InputObject:{{t.param.n}}\\x", "50\\x")]
    [InlineData("& { param($p) $p } -p {{t.param.n}}.txt", "50.txt")]
    [InlineData("Write-Output {{t.output}}.log", "O'Brian.log")]
    public void TemplateStartingAWord_KeepsTheWholeWordOneArgument(string template, string expected)
        => Invoke(Resolve(template)).Should().Equal(expected);

    [Fact]
    public void TemplateStartingAWord_ValueWithCodeCharactersStaysText()
    {
        var variables = new Dictionary<string, string> { ["t.output"] = "a b; Write-Output injected $(1) \"q\" `n" };

        var output = Invoke(PowerShellActivitySupport.ResolveScriptVariables("Write-Output {{t.output}}\\sub", variables));

        output.Should().Equal("a b; Write-Output injected $(1) \"q\" `n\\sub");
    }

    [Theory]
    [InlineData("$n = {{t.output}}.Length\n$n", "7")]
    [InlineData("$u = {{t.output}}.ToUpper()\n$u", "O'BRIAN")]
    [InlineData("Write-Output ({{t.output}} + '!')", "O'Brian!")]
    [InlineData("Write-Output {{t.output}}.ToUpper()", "O'BRIAN")]
    [InlineData("Write-Output {{t.output}} {{t.param.n}}", "O'Brian", "50")]
    public void TemplateFollowedByAMemberAccessOrOperator_StaysAQuotedValue(string template, params string[] expected)
        => Invoke(Resolve(template)).Should().Equal(expected);

    [Fact]
    public void TemplateStartingAWordWithQuotes_IsRejected()
    {
        var act = () => Resolve("Write-Output {{t.output}}\\'x'");

        act.Should().Throw<InvalidOperationException>().WithMessage("*contains quotes*");
    }

    [Theory]
    [InlineData("Write-Output C:\\t\\{{t.param.n}}.txt", "C:\\t\\50.txt")]
    [InlineData("Write-Output C:\\t\\{{t.output}}.txt", "C:\\t\\O'Brian.txt")]
    [InlineData("Write-Output https://host/api/{{t.param.x}}/{{t.param.y}}", "https://host/api/2/3")]
    public void TemplateInsideABareword_BecomesAQuotedPartOfThatWord(string template, string expected)
        => Invoke(Resolve(template)).Should().Equal(expected);

    [Fact]
    public void TemplateInsideABareword_ValueWithSpacesAndSeparatorsStaysOneArgument()
    {
        var variables = new Dictionary<string, string> { ["t.output"] = "a b; Write-Output injected" };

        var output = Invoke(PowerShellActivitySupport.ResolveScriptVariables("Write-Output C:\\t\\{{t.output}}.txt", variables));

        output.Should().Equal("C:\\t\\a b; Write-Output injected.txt");
    }

    [Theory]
    [InlineData("$dir = 'C:\\d i r'\nWrite-Output $dir\\f-{{t.param.n}}.txt", "C:\\d i r\\f-50.txt")]
    [InlineData("$dir = 'C:\\d'\nWrite-Output $dir\\{{t.output}}\\{{t.param.x}}.log", "C:\\d\\O'Brian\\2.log")]
    [InlineData("$dir = 'C:\\d'\nWrite-Output ${dir}\\{{t.param.n}}", "C:\\d\\50")]
    [InlineData("Write-Output C:\\$env:NP_NO_SUCH_VAR\\{{t.param.n}}", "C:\\\\50")]
    public void TemplateInsideABarewordWithAVariable_BecomesAQuotedPartOfThatWord(string template, string expected)
        => Invoke(Resolve(template)).Should().Equal(expected);

    [Fact]
    public void TemplateInsideABarewordWithAVariable_ValueWithCodeCharactersStaysText()
    {
        var variables = new Dictionary<string, string> { ["t.output"] = "a b; $(Write-Output injected) `n" };

        var output = Invoke(PowerShellActivitySupport.ResolveScriptVariables("$d = 'C:'\nWrite-Output $d\\{{t.output}}.txt", variables));

        output.Should().Equal("C:\\a b; $(Write-Output injected) `n.txt");
    }

    [Theory]
    [InlineData("Write-Output C:\\'a{{t.output}}b'")]
    [InlineData("Write-Output C:\\\"a{{t.output}}b\"")]
    [InlineData("Write-Output C:\\$(Get-Date)\\{{t.output}}")]
    [InlineData("Write-Output C:\\a`b{{t.output}}")]
    public void TemplateInsideABarewordWithItsOwnQuotingOrSubexpression_IsRejected(string template)
    {
        // The template could sit inside a quoted part or subexpression of the word, where a quoted
        // value would close that part instead of opening its own.
        var act = () => Resolve(template);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsafe or ambiguous syntax context*");
    }

    [Theory]
    [InlineData("$s = \"abc {{t.output}}")]
    [InlineData("$s = 'abc {{t.output}}")]
    public void TemplateInAnUnclosedString_IsRejected(string template)
    {
        var act = () => Resolve(template);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsafe or ambiguous syntax context*");
    }

    [Theory]
    [InlineData("$v = {{t.output}}{{u.output}}")]
    [InlineData("$x{{t.output}}")]
    public void TemplateWhoseValueWouldChangeTheSurroundingParse_IsRejected(string template)
    {
        // Two adjacent quoted literals fuse into one string ('a''b' is a'b), and a literal glued to
        // a variable is a syntax error: in both cases the substitution would not mean the value.
        var act = () => Resolve(template);

        act.Should().Throw<InvalidOperationException>().WithMessage("*unsafe or ambiguous syntax context*");
    }
}

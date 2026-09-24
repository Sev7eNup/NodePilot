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

    [Fact]
    public void TemplateFollowedByAPathSuffix_IsAccepted()
    {
        var act = () => Resolve("Get-Item -Path {{t.output}}\\sub");

        act.Should().NotThrow();
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

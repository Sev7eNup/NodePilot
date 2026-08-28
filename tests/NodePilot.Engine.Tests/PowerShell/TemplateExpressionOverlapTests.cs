using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Regression cover for overlapping template extents in
/// <see cref="PowerShellActivitySupport.ResolveScriptVariables"/>.
///
/// <para>The globals and step-output patterns are not disjoint: a global variable may be named
/// <c>output</c>, <c>error</c> or <c>success</c> (<c>GlobalVariablesController.NameRegex</c>
/// accepts all three), and <c>{{globals.output}}</c> then matches both patterns over the very
/// same span: once as global "output" and once as step "globals" with tail "output". The resolver
/// must replace that shared span only once to preserve the text that follows it.</para>
///
/// <para>These assertions are deliberately made on the resolved script text rather than on the
/// output of a real PowerShell run. The corrupted script — <c>Write-Output 'v'ite-Output
/// $marker</c> — still executes without error and still prints a superstring of the expected
/// value, so an end-to-end assertion silently passes while the script is mangled.</para>
/// </summary>
public class TemplateExpressionOverlapTests
{
    [Theory]
    [InlineData("output")]
    [InlineData("error")]
    [InlineData("success")]
    public void GlobalNamedLikeAStepTail_IsReplacedExactlyOnce(string globalName)
    {
        var vars = new Dictionary<string, string> { [$"globals.{globalName}"] = "payload-value" };
        var script = $"$marker = 'BEFORE'\nWrite-Output {{{{globals.{globalName}}}}}\nWrite-Output $marker";

        var resolved = PowerShellActivitySupport.ResolveScriptVariables(script, vars);

        resolved.Should().Be(
            "$marker = 'BEFORE'\nWrite-Output 'payload-value'\nWrite-Output $marker",
            "the template is replaced once and every surrounding character survives");
    }

    [Fact]
    public void GlobalNamedOutput_NextToARealStepOutput_BothResolveIndependently()
    {
        var vars = new Dictionary<string, string>
        {
            ["globals.output"] = "from-global",
            ["prev.output"] = "from-step",
        };
        var script = "Write-Output {{globals.output}}\nWrite-Output {{prev.output}}";

        var resolved = PowerShellActivitySupport.ResolveScriptVariables(script, vars);

        resolved.Should().Be("Write-Output 'from-global'\nWrite-Output 'from-step'",
            "the overlap filter must not swallow the neighbouring step template");
    }

    /// <summary>
    /// The global wins the overlap, matching the precedence <c>VariableResolver</c> applies on
    /// its own path by replacing globals before step outputs. A step literally named
    /// <c>globals</c> cannot shadow the reserved namespace.
    /// </summary>
    [Fact]
    public void OverlapResolvesAsGlobal_NotAsAStepNamedGlobals()
    {
        var vars = new Dictionary<string, string>
        {
            ["globals.output"] = "global-wins",
            ["globals.output.unused"] = "ignored",
        };

        var resolved = PowerShellActivitySupport.ResolveScriptVariables(
            "Write-Output {{globals.output}}", vars);

        resolved.Should().Be("Write-Output 'global-wins'");
    }

    [Fact]
    public void UnresolvedOverlappingTemplate_IsLeftVerbatim()
    {
        var resolved = PowerShellActivitySupport.ResolveScriptVariables(
            "Write-Output {{globals.output}}", new Dictionary<string, string>());

        resolved.Should().Be("Write-Output {{globals.output}}",
            "an unknown global stays literal so the unresolved-template diagnostic can flag it");
    }
}

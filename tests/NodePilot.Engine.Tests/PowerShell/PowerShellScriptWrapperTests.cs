using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

public class PowerShellScriptWrapperTests
{
    [Fact]
    public void Wrap_IncludesUserScriptAndCaptureBlock()
    {
        var wrapped = PowerShellScriptWrapper.Wrap("$x = 1", new Dictionary<string, string>(), NullLogger.Instance);

        wrapped.Should().Contain("# === USER SCRIPT ===");
        wrapped.Should().Contain("$x = 1");
        wrapped.Should().Contain("# === NODEPILOT OUTPUT CAPTURE ===");
        wrapped.Should().Contain(PowerShellScriptWrapper.ParamsMarker);
    }

    [Fact]
    public void Wrap_UsesHashtableBaseCountToAvoidCountKeyCollision()
    {
        var wrapped = PowerShellScriptWrapper.Wrap("$count = 0", new Dictionary<string, string>(), NullLogger.Instance);

        wrapped.Should().Contain("if ($__npOut.psbase.Count -gt 0) {");
        wrapped.Should().NotContain("if ($__npOut.Count -gt 0) {");
    }

    [Fact]
    public void Wrap_InjectsShortParameterAliasesAndEscapesSingleQuotes()
    {
        var wrapped = PowerShellScriptWrapper.Wrap(
            "Write-Output $a",
            new Dictionary<string, string> { ["manual.a"] = "it's" },
            NullLogger.Instance);

        wrapped.Should().Contain("$Params['a'] = 'it''s'");
        wrapped.Should().Contain("$a = 'it''s'");
    }

    [Fact]
    public void Wrap_GuardsShortAliasesAgainstPowerShellBuiltIns()
    {
        var wrapped = PowerShellScriptWrapper.Wrap(
            "Write-Output $Params['error']",
            new Dictionary<string, string> { ["previous.error"] = "boom" },
            NullLogger.Instance);

        wrapped.Should().Contain("$Params['error'] = 'boom'");
        wrapped.Should().Contain("if (-not $__npBuiltinVars.Contains('error')) { $error = 'boom' }");
    }

    [Fact]
    public void Wrap_SkipsInvalidParameterNames()
    {
        var wrapped = PowerShellScriptWrapper.Wrap(
            "Write-Output 'ok'",
            new Dictionary<string, string> { ["manual.bad-key"] = "1" },
            NullLogger.Instance);

        wrapped.Should().NotContain("$Params['bad-key']");
        wrapped.Should().NotContain("$bad_key = '1'");
    }
}

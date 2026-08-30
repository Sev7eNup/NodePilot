using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

public class PowerShellScriptWrapperTests
{
    private static string Wrap(string script, Dictionary<string, string>? parameters = null) =>
        PowerShellScriptWrapper.Wrap(script, parameters ?? new Dictionary<string, string>(), NullLogger.Instance);

    [Fact]
    public void Wrap_IncludesUserScriptAndCaptureBlock()
    {
        var wrapped = Wrap("$x = 1");

        wrapped.Should().Contain("# === USER SCRIPT ===");
        wrapped.Should().Contain("$x = 1");
        wrapped.Should().Contain("# === NODEPILOT OUTPUT CAPTURE ===");
        wrapped.Should().Contain(PowerShellScriptWrapper.ParamsMarker);
    }

    [Fact]
    public void Wrap_UsesHashtableBaseCountToAvoidCountKeyCollision()
    {
        var wrapped = Wrap("$count = 0");

        wrapped.Should().Contain("if ($__npOut.psbase.Count -gt 0) {");
        wrapped.Should().NotContain("if ($__npOut.Count -gt 0) {");
    }

    [Fact]
    public void Wrap_InjectsShortParameterAliasesAndEscapesSingleQuotes()
    {
        var wrapped = Wrap("Write-Output $a", new Dictionary<string, string> { ["manual.a"] = "it's" });

        wrapped.Should().Contain("$Params['a'] = 'it''s'");
        wrapped.Should().Contain("$a = 'it''s'");
    }

    [Fact]
    public void Wrap_GuardsShortAliasesAgainstPowerShellBuiltIns()
    {
        var wrapped = Wrap("Write-Output $Params['error']",
            new Dictionary<string, string> { ["error"] = "boom" });

        wrapped.Should().Contain("$Params['error'] = 'boom'");
        wrapped.Should().Contain("if (-not $__npReserved.Contains('error')) { $error = 'boom' }");
    }

    [Fact]
    public void Wrap_SkipsInvalidParameterNames()
    {
        var wrapped = Wrap("Write-Output 'ok'", new Dictionary<string, string> { ["manual.bad-key"] = "1" });

        wrapped.Should().NotContain("$Params['bad-key']");
        wrapped.Should().NotContain("$bad_key = '1'");
    }

    [Fact]
    public void Wrap_ResetsExitCodeAndErrorBeforeTheUserScript()
    {
        var wrapped = Wrap("$x = 1");

        wrapped.Should().Contain("$global:LASTEXITCODE = $null");
        wrapped.Should().Contain("if ($null -ne $global:Error) { $global:Error.Clear() }");

        // Position is load-bearing: a reset after the script would erase the very exit code the
        // step is supposed to report.
        wrapped.IndexOf("$global:LASTEXITCODE = $null", StringComparison.Ordinal)
            .Should().BeLessThan(wrapped.IndexOf("# === USER SCRIPT ===", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrap_RunsTheUserScriptInAChildScopeOfTheInjectionScope()
    {
        var wrapped = Wrap("$x = 1", new Dictionary<string, string> { ["a"] = "1" });

        // The injected alias must be emitted before the inner scope opens; that separation is
        // what keeps a read-only upstream parameter out of the captured output.
        var aliasIdx = wrapped.IndexOf("$a = '1'", StringComparison.Ordinal);
        var innerIdx = wrapped.IndexOf("$__npBuiltinVars", StringComparison.Ordinal);
        var scriptIdx = wrapped.IndexOf("# === USER SCRIPT ===", StringComparison.Ordinal);

        aliasIdx.Should().BeGreaterThan(0);
        aliasIdx.Should().BeLessThan(innerIdx);
        innerIdx.Should().BeLessThan(scriptIdx);
    }

    [Fact]
    public void Wrap_ExcludesReservedAndInternalNamesFromTheCaptureSweep()
    {
        var wrapped = Wrap("$x = 1");

        wrapped.Should().Contain("-not $__npReserved.Contains($_.Name)");
        wrapped.Should().Contain("$_.Name -notlike '__np*'");
    }

    [Theory]
    [InlineData("VerbosePreference")]
    [InlineData("ErrorActionPreference")]
    [InlineData("Matches")]
    [InlineData("foreach")]
    public void Wrap_SeedsReservedNamesSoUpstreamParametersCannotBindThem(string reserved)
    {
        var wrapped = Wrap("Write-Output 'ok'",
            new Dictionary<string, string> { [reserved] = "x" });

        wrapped.Should().Contain($"$__npReserved.Add('{reserved}') | Out-Null");
        // The alias line is still emitted, but guarded — the runtime check is what withholds it.
        wrapped.Should().Contain($"$Params['{reserved}'] = 'x'");
        wrapped.Should().Contain($"if (-not $__npReserved.Contains('{reserved}'))");
    }

    [Theory]
    [InlineData("__npReserved")]
    [InlineData("__npOut")]
    [InlineData("__npBuiltinVars")]
    public void Wrap_NeverAliasesItsOwnInternalPrefix(string internalName)
    {
        // The key grammar is [A-Za-z0-9_]+, so a wrapper-internal name is a legal upstream key.
        // Binding it would overwrite a HashSet with a string and take the wrapper down.
        var wrapped = Wrap("Write-Output 'ok'",
            new Dictionary<string, string> { [internalName] = "hijack" });

        wrapped.Should().Contain($"$Params['{internalName}'] = 'hijack'");
        wrapped.Should().NotContain($"${internalName} = 'hijack'");
    }

    [Fact]
    public void Wrap_WithAllowlist_DoesNotUseTheReservedSweep()
    {
        var wrapped = PowerShellScriptWrapper.Wrap(
            "$result = 1", new Dictionary<string, string>(), NullLogger.Instance, ["result"]);

        wrapped.Should().Contain("$__npOutAllow.Contains($_.Name)");
        wrapped.Should().NotContain("-not $__npReserved.Contains($_.Name)");
    }
}

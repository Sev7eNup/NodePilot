using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// A program the script started without a new window writes into the same stream as the wrapper,
/// so its lines can land before, between or after the marker lines. The markers must still yield
/// their values, and the foreign lines must stay ordinary output.
/// </summary>
public class ExtractMarkersInterleavedOutputTests
{
    private const string Params = PowerShellScriptWrapper.ParamsMarker;
    private const string ExitCode = PowerShellScriptWrapper.ExitCodeMarker;

    private static (string Clean, Dictionary<string, string> Parameters) Extract(params string[] lines)
    {
        var (clean, _, parameters) = PowerShellActivitySupport.ExtractMarkers(
            string.Join("\r\n", lines), "step-1", NullLogger.Instance);
        return (clean, parameters);
    }

    [Fact]
    public void ForeignLinesAfterTheMarkers_StayOutput_AndDoNotBecomeTheExitCode()
    {
        var (clean, parameters) = Extract("own", Params, "{\"x\":\"a\"}", ExitCode, "0", "Reply 1", "Reply 2");

        parameters.Should().Contain("x", "a").And.Contain("exitCode", "0");
        clean.Should().Contain("own").And.Contain("Reply 1").And.Contain("Reply 2");
        clean.Should().NotContain("###NODEPILOT_");
    }

    [Fact]
    public void ForeignLinesBetweenMarkerAndValue_AreSkipped()
    {
        var (clean, parameters) = Extract(Params, "Reply 1", "{\"x\":\"a\"}", "Reply 2", ExitCode, "Reply 3", "5");

        parameters.Should().Contain("x", "a").And.Contain("exitCode", "5");
        clean.Should().Contain("Reply 1").And.Contain("Reply 2").And.Contain("Reply 3");
    }

    [Fact]
    public void OnlyTheLastMarkerCounts()
    {
        // An echo of a marker earlier in the output (a transcript, a user's own Write-Output) is
        // not the wrapper's block.
        var (_, parameters) = Extract(Params, "{\"x\":\"old\"}", "more", Params, "{\"x\":\"new\"}", ExitCode, "0");

        parameters.Should().Contain("x", "new");
    }

    [Fact]
    public void MarkerWithoutAValueLine_YieldsNothing()
    {
        var (clean, parameters) = Extract("own", ExitCode, "not a number");

        parameters.Should().NotContainKey("exitCode");
        clean.Should().Contain("not a number");
    }
}

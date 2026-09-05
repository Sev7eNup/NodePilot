using System.Diagnostics;
using System.Management.Automation.Runspaces;
using FluentAssertions;
using NodePilot.Core.Telemetry;
using NodePilot.Engine.Tests.Helpers;
using NodePilot.Remote;
using Xunit;

namespace NodePilot.Engine.Tests.Remote;

/// <summary>
/// The remote span leaves the process unredacted when trace export is on, so it carries the
/// failure class only. The script's error stream stays on the result, which StepRunner redacts
/// before it reaches the step span, the row and the log.
/// </summary>
public class WinRmSessionSpanRedactionTests
{
    [Fact]
    public async Task ExecuteScriptAsync_OnScriptError_SpanCarriesTheFailureClassNotTheErrorStream()
    {
        const string target = "span-redaction-test";
        using var traces = new TraceCollector(TelemetryConstants.Sources.Remote);
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        var session = new WinRmSession(runspace, targetHostname: target);

        var result = await session.ExecuteScriptAsync(
            "Write-Error 'login failed: password=hunter2'",
            timeoutSeconds: 30,
            ct: CancellationToken.None);
        await session.DisposeAsync();

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("hunter2", "the raw result feeds the data bus and is redacted downstream");

        var span = traces.Where("winrm.execute")
            .Should().ContainSingle(a => Equals(a.GetTagItem("nodepilot.remote.target"), target)).Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.StatusDescription.Should().Be("script_failed");
        span.Events.Should().BeEmpty();
    }
}

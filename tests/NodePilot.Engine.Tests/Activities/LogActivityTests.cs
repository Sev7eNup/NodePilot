using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Activities;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

public class LogActivityTests
{
    private readonly Mock<ILogger<LogActivity>> _logger = new();

    // LogActivity now takes an OutputRedactor so `message: "pw={{step.param.pw}}"` gets
    // scrubbed before it hits the log pipeline. We pass a fresh redactor with default
    // patterns — the test messages don't match any secret-bearing pattern so behavior
    // is unchanged for every existing assertion.
    private LogActivity Create() => new(_logger.Object, new OutputRedactor());

    private static JsonElement Cfg(object obj) =>
        JsonDocument.Parse(JsonSerializer.Serialize(obj)).RootElement;

    private static StepExecutionContext Ctx(
        string workflowName = "TestFlow", string stepLabel = "step-label") =>
        new()
        {
            WorkflowExecutionId = Guid.NewGuid(),
            StepId = "log-1",
            StepLabel = stepLabel,
            WorkflowName = workflowName,
        };

    private void VerifyLoggedAt(LogLevel level, string expected)
    {
        _logger.Verify(
            l => l.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(expected)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_InfoLevel_CallsLogInformation()
    {
        var result = await Create().ExecuteAsync(Ctx(),
            Cfg(new { level = "info", message = "hello" }), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Output.Should().Be("hello");
        result.OutputParameters["level"].Should().Be("info");
        result.OutputParameters["message"].Should().Be("hello");
        VerifyLoggedAt(LogLevel.Information, "hello");
    }

    [Fact]
    public async Task ExecuteAsync_WarningLevel_CallsLogWarning()
    {
        await Create().ExecuteAsync(Ctx(),
            Cfg(new { level = "warning", message = "heads up" }), CancellationToken.None);

        VerifyLoggedAt(LogLevel.Warning, "heads up");
    }

    [Fact]
    public async Task ExecuteAsync_ErrorLevel_CallsLogError()
    {
        await Create().ExecuteAsync(Ctx(),
            Cfg(new { level = "error", message = "boom" }), CancellationToken.None);

        VerifyLoggedAt(LogLevel.Error, "boom");
    }

    [Fact]
    public async Task ExecuteAsync_UnknownLevel_DefaultsToInformation()
    {
        await Create().ExecuteAsync(Ctx(),
            Cfg(new { level = "trace", message = "whatever" }), CancellationToken.None);

        VerifyLoggedAt(LogLevel.Information, "whatever");
    }

    [Fact]
    public async Task ExecuteAsync_MissingMessage_ReturnsFailure()
    {
        var result = await Create().ExecuteAsync(Ctx(),
            Cfg(new { level = "info" }), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("message");
    }

    [Fact]
    public async Task ExecuteAsync_EnrichmentScope_IsOpened()
    {
        await Create().ExecuteAsync(Ctx(),
            Cfg(new { level = "info", message = "scoped" }), CancellationToken.None);

        _logger.Verify(l => l.BeginScope(It.Is<Dictionary<string, object>>(d =>
            d.ContainsKey("workflow_execution_id") &&
            d.ContainsKey("step_id") &&
            d.ContainsKey("activity") &&
            d.ContainsKey("user_log_level"))), Times.Once);
    }

    /// <summary>
    /// SupportLog marker: user-authored log activities are by definition relevant to support
    /// staff, and the second Serilog sink filters on <c>SupportLog=true</c>. This pins that
    /// behavior against an accidental removal of the scope property.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OpensScope_WithSupportLogTrue()
    {
        await Create().ExecuteAsync(Ctx(),
            Cfg(new { level = "info", message = "operator visible" }), CancellationToken.None);

        _logger.Verify(l => l.BeginScope(It.Is<Dictionary<string, object>>(d =>
            d.ContainsKey("SupportLog") && (bool)d["SupportLog"])), Times.Once,
            "User-Log-Activity muss unconditional SupportLog=true im Scope setzen, sonst landet die Zeile nicht im Support-Log");
    }

    /// <summary>
    /// Event-type discriminator: the scope property <c>support.event_type=USER_LOG</c> is what
    /// lands in the EventType column of the DB projection. Without it, SupportEventDbSink can't
    /// classify the event, and the web viewer's "show only USER_LOG" filter stops working.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_OpensScope_WithSupportEventTypeUserLog()
    {
        await Create().ExecuteAsync(Ctx(),
            Cfg(new { level = "info", message = "operator visible" }), CancellationToken.None);

        _logger.Verify(l => l.BeginScope(It.Is<Dictionary<string, object>>(d =>
            d.ContainsKey("support.event_type") && (string)d["support.event_type"] == "USER_LOG")), Times.Once,
            "User-Log-Activity muss support.event_type=USER_LOG im Scope setzen");
    }

    /// <summary>
    /// The user-log line in the support log (the plain-text formatter doesn't render scope
    /// properties) must carry the workflow name and step label directly in the message —
    /// otherwise the operator just sees "USER-LOG: ..." with no way to tell which workflow it came from.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LogMessage_IncludesWorkflowNameAndStepLabel()
    {
        await Create().ExecuteAsync(
            Ctx(workflowName: "Daily Report", stepLabel: "fetchData"),
            Cfg(new { level = "info", message = "ok" }),
            CancellationToken.None);

        VerifyLoggedAt(LogLevel.Information, "workflow=Daily Report");
        VerifyLoggedAt(LogLevel.Information, "step=fetchData");
    }

    [Fact]
    public async Task ExecuteAsync_MessageContainsSecret_OutputAndLogAreRedacted()
    {
        // OutputRedactor is supposed to scrub secret-shaped substrings before they reach the
        // log pipeline OR the ActivityResult.Output that downstream steps can read. Without
        // this test, a regression in the LogActivity → OutputRedactor wiring would silently
        // re-introduce secret leakage into the audit trail.
        var result = await Create().ExecuteAsync(Ctx(),
            Cfg(new { level = "info", message = "deploy ok password=hunter2 done" }),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        // Original secret value must not appear anywhere in the user-visible output.
        result.Output.Should().NotContain("hunter2");
        result.Output.Should().Contain("***");
        result.OutputParameters["message"].Should().NotContain("hunter2");
        result.OutputParameters["message"].Should().Contain("***");
        // The redacted (not the raw) message must reach the logger pipeline too.
        VerifyLoggedAt(LogLevel.Information, "***");
        _logger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("hunter2")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "the raw secret value must never reach the logger");
    }
}

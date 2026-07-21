using System.Text.Json;
using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Api.Dtos;
using NodePilot.Cli.Output;
using NodePilot.Cli.Settings;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace NodePilot.Cli.Tests;

/// <summary>
/// Smoke-tests for the rendering layer: we don't snapshot full table output (too brittle),
/// but we DO verify that surprising input (Markup-special chars in workflow names, status
/// strings the renderer doesn't know about) doesn't blow up the formatter, and that the
/// output writer routes JSON/Yaml correctly when redirected from a TTY.
/// </summary>
public class OutputAndRenderingTests
{
    private static TestConsole NewBuffer()
    {
        var console = new TestConsole();
        // Default TestConsole is 80 cols wide and wraps in the middle of cells, which makes
        // .Should().Contain("Build [stg]") flaky. 240 cols is enough for every renderer here.
        console.Profile.Width = 240;
        return console;
    }

    [Fact]
    public void Renderers_Workflows_HandlesBracketsAndUnknownStatus()
    {
        var rows = new[]
        {
            SampleWorkflow("Build [stg]", lastStatus: "Succeeded"),
            SampleWorkflow("Report", lastStatus: "WeirdStatus"),
        };
        var console = (IAnsiConsole)NewBuffer();
        Renderers.Workflows(console, rows);
        var output = ((TestConsole)console).Output;
        output.Should().Contain("Build [stg]");
        output.Should().Contain("Report");
        output.Should().Contain("WeirdStatus");
    }

    [Fact]
    public void Renderers_WorkflowDetail_RendersAllRows()
    {
        var w = SampleWorkflow("Build", lastStatus: "Failed");
        var console = (IAnsiConsole)NewBuffer();
        Renderers.WorkflowDetail(console, w);
        var output = ((TestConsole)console).Output;
        output.Should().Contain("Build");
        output.Should().Contain("Failed");
    }

    [Fact]
    public void Renderers_Executions_RendersDurationAndStatus()
    {
        var rows = new[]
        {
            new ExecutionResponse(Guid.NewGuid(), Guid.NewGuid(), "Succeeded",
                StartedAt: new DateTime(2026,4,1,10,0,0,DateTimeKind.Utc),
                CompletedAt: new DateTime(2026,4,1,10,0,5,DateTimeKind.Utc),
                TriggeredBy: "alice", ErrorMessage: null),
            new ExecutionResponse(Guid.NewGuid(), Guid.NewGuid(), "Running",
                StartedAt: new DateTime(2026,4,1,10,0,0,DateTimeKind.Utc),
                CompletedAt: null, TriggeredBy: null, ErrorMessage: null),
        };
        var console = (IAnsiConsole)NewBuffer();
        Renderers.Executions(console, rows);
        var output = ((TestConsole)console).Output;
        output.Should().Contain("Succeeded");
        output.Should().Contain("Running");
        output.Should().Contain("alice");
    }

    [Fact]
    public void Renderers_Steps_RendersAllStatusesWithoutThrowing()
    {
        var rows = new[]
        {
            NewStep("Disk", "Succeeded"),
            NewStep("Net",  "Failed"),
            NewStep("Wait", "Skipped"),
        };
        var console = (IAnsiConsole)NewBuffer();
        Renderers.Steps(console, rows);
        var output = ((TestConsole)console).Output;
        output.Should().Contain("Disk");
        output.Should().Contain("Net");
        output.Should().Contain("Wait");
    }

    [Fact]
    public void Renderers_Audit_TruncatesLongDetails()
    {
        var details = new string('x', 200);
        var rows = new[]
        {
            new AuditEntryResponse(Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), "alice",
                "WORKFLOW_PUBLISHED", "Workflow", Guid.NewGuid(), details, "10.0.0.1"),
        };
        var page = new AuditPageResponse(rows, NextCursor: null);
        var console = (IAnsiConsole)NewBuffer();
        Renderers.Audit(console, page);
        var output = ((TestConsole)console).Output;
        output.Should().Contain("WORKFLOW_PUBLISHED");
        output.Should().Contain("alice");
        output.Should().Contain("10.0.0.1");
        output.Should().Contain("..."); // long detail is truncated to 80 chars + "..."
    }

    [Fact]
    public void Renderers_Audit_RendersNextCursorHint()
    {
        var rows = new[]
        {
            new AuditEntryResponse(Guid.NewGuid(), DateTime.UtcNow, null, null,
                "X", null, null, null, null),
        };
        var cursorId = Guid.NewGuid();
        var page = new AuditPageResponse(rows, new AuditCursor(DateTime.UtcNow, cursorId));
        var console = (IAnsiConsole)NewBuffer();
        Renderers.Audit(console, page);
        var output = ((TestConsole)console).Output;
        output.Should().Contain("next cursor");
        output.Should().Contain("--after-id");
    }

    [Fact]
    public void Renderers_Versions_MarksCurrent()
    {
        var rows = new[]
        {
            new WorkflowVersionInfo(2, "v2", DateTime.UtcNow, "admin", "fix typo", true),
            new WorkflowVersionInfo(1, "v1", DateTime.UtcNow.AddDays(-1), "admin", null, false),
        };
        var console = (IAnsiConsole)NewBuffer();
        Renderers.Versions(console, rows);
        var output = ((TestConsole)console).Output;
        output.Should().Contain("v2");
        output.Should().Contain("v1");
        output.Should().Contain("fix typo");
    }

    [Fact]
    public void OutputWriter_JsonFormat_WritesValidJsonToStdout()
    {
        using var stdout = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var writer = new OutputWriter(OutputFormat.Json, noColor: true);
            writer.WriteData(new { name = "Alpha", count = 3 }, (_, _) => Assert.Fail("table renderer should not run"));
            var emitted = stdout.ToString().Trim();
            using var doc = JsonDocument.Parse(emitted);
            doc.RootElement.GetProperty("name").GetString().Should().Be("Alpha");
            doc.RootElement.GetProperty("count").GetInt32().Should().Be(3);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void OutputWriter_YamlFormat_WritesYamlToStdout()
    {
        using var stdout = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var writer = new OutputWriter(OutputFormat.Yaml, noColor: true);
            writer.WriteData(new { name = "Alpha", count = 3 }, (_, _) => Assert.Fail("table renderer should not run"));
            stdout.ToString().Should().Contain("name: Alpha").And.Contain("count: 3");
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void OutputWriter_TableFormat_InvokesRendererCallback()
    {
        var writer = new OutputWriter(OutputFormat.Table, noColor: true);
        bool invoked = false;
        writer.WriteData(42, (_, _) => invoked = true);
        invoked.Should().BeTrue();
    }

    [Fact]
    public void ApiException_BuildsMessageFromTitleAndDetail()
    {
        var ex = new ApiException(System.Net.HttpStatusCode.BadRequest, "Bad", "missing field x", null);
        ex.Message.Should().Contain("400").And.Contain("Bad").And.Contain("missing field x");
    }

    [Fact]
    public void ApiException_FallsBackToRawBodyWhenNoDetail()
    {
        var ex = new ApiException(System.Net.HttpStatusCode.BadRequest, null, null, "raw error body");
        ex.Message.Should().Contain("400").And.Contain("raw error body");
    }

    private static WorkflowResponse SampleWorkflow(string name, string lastStatus) => new(
        Id: Guid.NewGuid(),
        Name: name,
        Description: null,
        DefinitionJson: "{}",
        Version: 1,
        IsEnabled: true,
        CreatedAt: DateTime.UtcNow,
        UpdatedAt: DateTime.UtcNow,
        CreatedBy: null,
        UpdatedBy: "admin",
        ActivityCount: 3,
        TriggerTypes: new() { "manualTrigger" },
        LastExecution: new LastExecutionInfo(Guid.NewGuid(), lastStatus, DateTime.UtcNow, DateTime.UtcNow, 100),
        SuccessCount: 1,
        TotalCount: 1,
        AvgDurationMs: 100,
        CheckedOutByUserId: null,
        CheckedOutByUserName: null,
        CheckedOutAt: null);

    private static StepExecutionResponse NewStep(string name, string status) => new(
        Id: Guid.NewGuid(), StepId: name.ToLowerInvariant(), StepName: name, StepType: "runScript",
        TargetMachine: null, Status: status,
        StartedAt: DateTime.UtcNow, CompletedAt: DateTime.UtcNow,
        Output: null, ErrorOutput: null,
        AttemptCount: 1, PausedAt: null,
        VariablesSnapshot: null, TraceOutput: null);
}

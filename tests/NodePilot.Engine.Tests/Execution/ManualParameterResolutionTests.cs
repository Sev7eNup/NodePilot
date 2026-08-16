using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

/// <summary>
/// <c>{{manual.NAME}}</c> — the run's trigger inputs — must resolve in activity config.
///
/// <para>It is the form the README's trigger table, the designer's variable picker, the ForEach
/// hint and the AI prompt catalog all tell authors to write, and the value really is in the run:
/// every trigger seeds its event data under that namespace and the trigger node surfaces the same
/// keys as its own <c>param.*</c> outputs. Only the resolver had no pattern for it — the tail
/// after the dot is a user-chosen name, not one of StepPattern's four fixed tails — so the
/// placeholder survived resolution AND slipped past the unresolved-template check, which scans
/// step patterns only. Measured against a 1.2.6 install: a log activity rendered the literal
/// "A={{manual.ziel}}" and the step finished green, while <c>{{trg.param.ziel}}</c> on the same
/// run resolved correctly.</para>
/// </summary>
[Collection("SerialEngineTests")]
public sealed class ManualParameterResolutionTests
{
    private const string TriggerWithParameter = """
        {"id":"trg","type":"activity","position":{"x":0,"y":0},
         "data":{"activityType":"manualTrigger","outputVariable":"trg",
                 "config":{"parameters":[{"name":"ziel","type":"string","required":false,"default":"leer"}]}}}
        """;

    [Fact]
    public async Task ManualParameter_ResolvesInActivityConfig()
    {
        var (captured, engine, db, conn) = Harness("log");
        try
        {
            var workflow = Workflow(db, """
                {"id":"s1","type":"activity","position":{"x":0,"y":0},
                 "data":{"activityType":"log","config":{"message":"A={{manual.ziel}}"}}}
                """);

            var execution = await engine.ExecuteAsync(
                workflow, "manual", CancellationToken.None,
                new Dictionary<string, string> { ["ziel"] = "WERT-DA" });

            execution.Status.Should().Be(ExecutionStatus.Succeeded);
            captured.Value!.Value.GetProperty("message").GetString()
                .Should().Be("A=WERT-DA", "the documented {{manual.X}} form must substitute");
        }
        finally { conn.Dispose(); }
    }

    /// <summary>
    /// The value must arrive byte-identically through both documented spellings, otherwise
    /// authors following the README get a different result from authors following the
    /// workflow styleguide.
    /// </summary>
    [Fact]
    public async Task ManualParameter_AndTriggerParamForm_AgreeOnTheValue()
    {
        var (captured, engine, db, conn) = Harness("log");
        try
        {
            var workflow = Workflow(db, """
                {"id":"s1","type":"activity","position":{"x":0,"y":0},
                 "data":{"activityType":"log","config":{"message":"A={{manual.ziel}} B={{trg.param.ziel}}"}}}
                """);

            var execution = await engine.ExecuteAsync(
                workflow, "manual", CancellationToken.None,
                new Dictionary<string, string> { ["ziel"] = "WERT-DA" });

            execution.Status.Should().Be(ExecutionStatus.Succeeded);
            captured.Value!.Value.GetProperty("message").GetString().Should().Be("A=WERT-DA B=WERT-DA");
        }
        finally { conn.Dispose(); }
    }

    /// <summary>
    /// An input the run does not carry must fail the step. Leaving it literal is the exact
    /// silent-success this change removes — the placeholder would otherwise be written into
    /// whatever the activity touches while the run reports green.
    /// </summary>
    [Fact]
    public async Task UnknownManualParameter_FailsTheStep_WithItsOwnDiagnostic()
    {
        var (_, engine, db, conn) = Harness("log");
        try
        {
            var workflow = Workflow(db, """
                {"id":"s1","type":"activity","position":{"x":0,"y":0},
                 "data":{"activityType":"log","config":{"message":"A={{manual.gibtsNicht}}"}}}
                """);

            var execution = await engine.ExecuteAsync(
                workflow, "manual", CancellationToken.None,
                new Dictionary<string, string> { ["ziel"] = "WERT-DA" });

            execution.Status.Should().Be(ExecutionStatus.Failed);
            var step = db.StepExecutions.First(s => s.WorkflowExecutionId == execution.Id && s.StepId == "s1");
            step.ErrorOutput.Should().Contain("Unknown trigger input(s)");
            step.ErrorOutput.Should().Contain("{{manual.gibtsNicht}}");
        }
        finally { conn.Dispose(); }
    }

    /// <summary>
    /// runScript resolves its own templates with PowerShell quoting, so the value has to reach
    /// the script body through that path too — the ForEach hint tells authors to read the loop
    /// item as <c>{{manual.item}}</c>, and a child workflow's body is usually a script.
    /// </summary>
    [Fact]
    public async Task ManualParameter_ReachesTheScriptBody()
    {
        var (captured, engine, db, conn) = Harness("runScript");
        try
        {
            var workflow = Workflow(db, """
                {"id":"s1","type":"activity","position":{"x":0,"y":0},
                 "data":{"activityType":"runScript","config":{"script":"Write-Output \"SIEHT={{manual.ziel}}\""}}}
                """);

            var execution = await engine.ExecuteAsync(
                workflow, "manual", CancellationToken.None,
                new Dictionary<string, string> { ["ziel"] = "WERT-DA" });

            execution.Status.Should().Be(ExecutionStatus.Succeeded);
            // The stand-in executor captures the config the engine handed it; runScript resolves
            // the body itself, so assert on what PowerShellActivitySupport produces.
            var script = captured.Value!.Value.GetProperty("script").GetString()!;
            var resolved = NodePilot.Engine.PowerShell.PowerShellActivitySupport.ResolveScriptVariables(
                script, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["manual.ziel"] = "WERT-DA" });
            resolved.Should().Contain("WERT-DA").And.NotContain("{{manual.");
        }
        finally { conn.Dispose(); }
    }

    // ------------------------------------------------------------------ fixture

    private static (Box<JsonElement?> Captured, WorkflowEngine Engine, NodePilotDbContext Db, Microsoft.Data.Sqlite.SqliteConnection Conn)
        Harness(string activityType)
    {
        var captured = new Box<JsonElement?>();

        var executor = new Mock<IActivityExecutor>();
        executor.Setup(e => e.ActivityType).Returns(activityType);
        executor.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .Callback<StepExecutionContext, JsonElement, CancellationToken>((_, cfg, __) => captured.Value = cfg.Clone())
            .ReturnsAsync(new ActivityResult { Success = true, Output = "ok" });

        var trigger = new Mock<IActivityExecutor>();
        trigger.Setup(e => e.ActivityType).Returns("manualTrigger");
        trigger.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StepExecutionContext ctx, JsonElement _, CancellationToken __) =>
            {
                // Mirrors ManualTrigger: the declared parameters are surfaced as the trigger
                // node's own param.* outputs, which is the other documented spelling.
                var result = new ActivityResult { Success = true, Output = "{}" };
                foreach (var (key, value) in ctx.Variables.Where(v => v.Key.StartsWith("manual.", StringComparison.OrdinalIgnoreCase)))
                    result.OutputParameters[key["manual.".Length..]] = value;
                return result;
            });

        var registry = new ActivityRegistry(new[] { executor.Object, trigger.Object });
        var (db, sp, conn) = TestDbContext.CreateWithScopedServices(registry);
        var engine = new WorkflowEngine(db, NullLogger<WorkflowEngine>.Instance, sp, Mock.Of<IExecutionNotifier>());
        return (captured, engine, db, conn);
    }

    private static Workflow Workflow(NodePilotDbContext db, string stepNodeJson)
    {
        var json = "{\"nodes\":[" + TriggerWithParameter + "," + stepNodeJson + "],"
                 + "\"edges\":[{\"id\":\"e\",\"source\":\"trg\",\"target\":\"s1\"}]}";
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "ManualParams", DefinitionJson = json };
        db.Workflows.Add(workflow);
        db.SaveChanges();
        return workflow;
    }

    private sealed class Box<T> { public T? Value { get; set; } }
}

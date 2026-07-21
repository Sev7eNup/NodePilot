using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests;

/// <summary>
/// Regression for the live-ops feed: the engine must emit an initial <c>Running</c> status
/// notification at execution start, not only terminal ones. Without it the live-ops graph
/// (which adds workflows on Running and removes them on terminal) could never show a
/// short-lived run — notably sub-workflow children that start and finish between snapshot polls.
/// </summary>
[Collection("SerialEngineTests")]
public class WorkflowEngineRunningNotificationTests
{
    private const string TriggerNodeJson =
        "{\"id\":\"trigger-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"manualTrigger\",\"config\":{}}}";

    private static IActivityExecutor MockExecutor(string type, ActivityResult result)
    {
        var m = new Mock<IActivityExecutor>();
        m.Setup(e => e.ActivityType).Returns(type);
        m.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return m.Object;
    }

    [Fact]
    public async Task ExecuteAsync_EmitsRunningStatusAtStart_ThenTerminal()
    {
        var registry = new ActivityRegistry(
        [
            MockExecutor("manualTrigger", new ActivityResult { Success = true, Output = "{}" }),
            MockExecutor("runScript", new ActivityResult { Success = true, Output = "OK" }),
        ]);
        var (db, sp, _) = TestDbContext.CreateWithScopedServices(registry);
        var notifier = new Mock<IExecutionNotifier>();
        var engine = new WorkflowEngine(db, registry, NullLogger<WorkflowEngine>.Instance, sp, notifier.Object);

        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            DefinitionJson = "{\"nodes\":[" + TriggerNodeJson +
                ",{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}]," +
                "\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"}]}",
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var execution = await engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        // Initial Running signal — exactly once, carrying the execution + workflow id.
        notifier.Verify(n => n.ExecutionStatusChangedAsync(
            execution.Id, workflow.Id, ExecutionStatus.Running, null, null), Times.Once);
        // Terminal signal still fires.
        notifier.Verify(n => n.ExecutionStatusChangedAsync(
            execution.Id, workflow.Id, ExecutionStatus.Succeeded, It.IsAny<string?>(), It.IsAny<DateTime?>()), Times.Once);
    }
}

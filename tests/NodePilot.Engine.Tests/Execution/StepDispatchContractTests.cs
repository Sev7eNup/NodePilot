using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Execution;

[Collection("SerialEngineTests")]
public sealed class StepDispatchContractTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("hostname")]
    [InlineData("id")]
    public async Task RegisteredTarget_AlwaysPersistsCanonicalMachineId(string lookup)
    {
        var registry = new ActivityRegistry([Executor("manualTrigger").Object, Executor("log").Object]);
        var (db, services, connection) = TestDbContext.CreateWithScopedServices(registry);
        await using var dbLifetime = db;
        await using var servicesLifetime = services;
        await using var connectionLifetime = connection;
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "node-name", Hostname = "node.example.test" };
        var target = lookup == "name" ? machine.Name : lookup == "hostname" ? machine.Hostname : machine.Id.ToString();
        var workflow = Workflow(null, target);
        db.AddRange(workflow, machine);
        await db.SaveChangesAsync();
        var engine = new WorkflowEngine(db, NullLogger<WorkflowEngine>.Instance, services, Mock.Of<IExecutionNotifier>());
        var result = await engine.ExecuteAsync(workflow, "manual", CancellationToken.None);
        result.Status.Should().Be(ExecutionStatus.Succeeded, result.ErrorMessage);
        (await db.StepExecutions.AsNoTracking().SingleAsync(s => s.StepId == "work"))
            .TargetMachine.Should().Be(machine.Id.ToString());
    }

    private static Mock<IActivityExecutor> Executor(string type)
    {
        var mock = new Mock<IActivityExecutor>();
        mock.Setup(e => e.ActivityType).Returns(type);
        mock.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "ok" });
        return mock;
    }

    private static Workflow Workflow(string? credential, string? target) => new()
    {
        Id = Guid.NewGuid(), Name = "Dispatch contract",
        DefinitionJson = JsonSerializer.Serialize(new
        {
            nodes = new[]
            {
                new { id = "trigger", type = "activity", data = new { activityType = "manualTrigger", config = new { }, credentialId = (string?)null, targetMachineId = (string?)null } },
                new { id = "work", type = "activity", data = new { activityType = "log", config = new { }, credentialId = credential, targetMachineId = target } }
            },
            edges = new[] { new { id = "edge", source = "trigger", target = "work" } }
        })
    };
}

using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Engine.Tests.Helpers;
using Xunit;

namespace NodePilot.Engine.Tests.Examples;

[Collection("SerialEngineTests")]
public class TempCleanupWorkflowTests
{
    [Theory]
    [InlineData("archive")]
    [InlineData("checksum")]
    [InlineData("inventory")]
    [InlineData(null)]
    public async Task Cleanup_OnlyDeletesAfterSuccessfulArchiveAndVerification(string? failedStep)
    {
        using var sample = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(RepositoryRoot(), "scripts", "example-temp-file-cleanup-workflow.json"),
            TestContext.Current.CancellationToken));
        var definition = sample.RootElement.GetProperty("workflows")[0].GetProperty("definition");
        var validation = WorkflowDefinitionStructuralValidator.Validate(definition);
        validation.IsValid.Should().BeTrue("the shipped example must be accepted when imported: {0}", validation);
        var calls = new ConcurrentQueue<string>();
        var executors = definition.GetProperty("nodes").EnumerateArray()
            .Where(n => n.GetProperty("type").GetString() == "activity")
            .Select(n => n.GetProperty("data").GetProperty("activityType").GetString()
                ?? throw new InvalidOperationException("An activity needs a type"))
            .Distinct()
            .Select(type => new RecordingActivity(type, calls, failedStep));
        var registry = new ActivityRegistry(executors);
        var (db, services, connection) = TestDbContext.CreateWithScopedServices(registry);
        await using (db)
        await using (services)
        await using (connection)
        {
            var workflow = new Workflow
            {
                Id = Guid.NewGuid(), Name = "Cleanup example", DefinitionJson = definition.GetRawText()
            };
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var engine = new WorkflowEngine(db, NullLogger<WorkflowEngine>.Instance,
                services, new Mock<IExecutionNotifier>().Object);

            var execution = await engine.ExecuteAsync(workflow, "test", TestContext.Current.CancellationToken,
                new Dictionary<string, string> { ["path"] = "C:\\unused-test-path", ["olderThanDays"] = "30" });

            if (failedStep is not null)
            {
                execution.Status.Should().Be(ExecutionStatus.Failed);
                calls.Should().NotContain("purge");
                calls.Should().NotContain("mail");
            }
            else
            {
                execution.Status.Should().Be(ExecutionStatus.Succeeded);
                var order = calls.ToList();
                order.Should().Contain(["archive", "checksum", "inventory", "purge", "mail"]);
                order.IndexOf("archive").Should().BeLessThan(order.IndexOf("checksum"));
                order.IndexOf("checksum").Should().BeLessThan(order.IndexOf("purge"));
                order.IndexOf("inventory").Should().BeLessThan(order.IndexOf("purge"));
                order.IndexOf("purge").Should().BeLessThan(order.IndexOf("mail"));
            }
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "NodePilot.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("NodePilot repository root was not found");
    }

    private sealed class RecordingActivity(string activityType, ConcurrentQueue<string> calls, string? failedStep)
        : IActivityExecutor
    {
        public string ActivityType => activityType;

        public Task<ActivityResult> ExecuteAsync(StepExecutionContext context, JsonElement config, CancellationToken ct)
        {
            calls.Enqueue(context.StepId);
            return Task.FromResult(new ActivityResult
            {
                Success = context.StepId != failedStep,
                ErrorOutput = context.StepId == failedStep ? "Simulated activity failure" : null,
                Output = "Recording executor; no file or network operations",
                OutputParameters = context.StepId switch
                {
                    "trg" => new() { ["path"] = "C:\\unused-test-path", ["olderThanDays"] = "30" },
                    "archive" => new() { ["destination"] = "C:\\unused-archive.zip" },
                    "checksum" => new() { ["hash"] = "test-hash" },
                    _ => new()
                }
            });
        }
    }
}

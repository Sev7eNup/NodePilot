using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Ai;
using NodePilot.Ai.Agents;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Agents;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

[Collection("AgentDeadlines")]
public sealed class AgentActivityRunnerTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("partial")]
    [InlineData("blocked")]
    [InlineData("timeout")]
    public async Task TechnicalSuccessPreservesSeparateTaskOutcomeAndUserJson(string outcome)
    {
        await using var db = TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "agent outcome", DefinitionJson = "{}" };
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Workflow = workflow };
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var calls = 0;
        var client = new Mock<ILlmClient>();
        client.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns((LlmRequest request, CancellationToken _) => {
                if (++calls == 1) return Task.FromResult(new LlmResponse("Draft", "test"));
                Assert.Empty(request.Tools ?? []);
                if (outcome == "timeout") throw new LlmException(LlmErrorKind.Timeout, "Timed out");
                return Task.FromResult(new LlmResponse(JsonSerializer.Serialize(new {
                    outcome, reason = "Explicit task assessment", report = new { answer = 42 },
                    coverage = new[] { new { requirement = "Answer", status = "fulfilled", basis = "Supplied data" } }
                }), "test"));
            });
        var factory = new Mock<ILlmClientFactory>(); factory.Setup(f => f.Create(It.IsAny<LlmConnection>())).Returns(client.Object);
        var options = new AgentOptions();
        var monitor = new Mock<IOptionsMonitor<AgentOptions>>(); monitor.SetupGet(m => m.CurrentValue).Returns(options);
        var llm = new Mock<IOptionsMonitor<LlmOptions>>(); llm.SetupGet(m => m.CurrentValue).Returns(new LlmOptions { Enabled = true });
        var redactor = new OutputRedactor(null);
        var journal = new AgentRunJournal(db, Mock.Of<IExecutionNotifier>(), redactor, NullLogger<AgentRunJournal>.Instance);
        using var gate = new AgentExecutionGate(Microsoft.Extensions.Options.Options.Create(options));
        var runner = new AgentActivityRunner(new AgentRuntime(factory.Object, llm.Object),
            new AgentToolHost(null!, null!, db, null!, null!, new AgentExternalReadPolicy(monitor.Object)), gate, journal, monitor.Object, redactor, NullLogger<AgentActivityRunner>.Instance);
        var config = new AgentActivityConfiguration { Task = "Answer", MaxModelCalls = 2, ResultFormat = "json",
            ResultSchema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"answer":{"type":"integer"}},"required":["answer"],"additionalProperties":false}""") };
        var result = await runner.ExecuteAsync(new StepExecutionContext { WorkflowExecutionId = execution.Id, StepId = "agent" },
            JsonSerializer.SerializeToElement(config, AgentConfiguration.JsonOptions), false, TestContext.Current.CancellationToken);
        if (outcome == "timeout")
        {
            Assert.False(result.Success);
            Assert.Equal("unassessed", result.OutputParameters["outcome"]);
            Assert.Equal("Draft", result.Output);
            var interrupted = await db.AgentRuns.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Failed", interrupted.Status);
            Assert.Equal("Draft", interrupted.Result);
            Assert.False(await db.AgentRunEvents.AnyAsync(e => e.Kind == "run_conclusion", TestContext.Current.CancellationToken));
            Assert.Equal(2, calls);
            return;
        }
        Assert.True(result.Success, result.ErrorOutput);
        Assert.Equal(outcome, result.OutputParameters["outcome"]);
        Assert.Equal("{\"answer\":42}", result.Output);
        Assert.Equal("Succeeded", (await db.AgentRuns.SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Contains(outcome, (await db.AgentRunEvents.SingleAsync(e => e.Kind == "run_conclusion", TestContext.Current.CancellationToken)).Content);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task DeadlinesAndCallerCancellation_EndJournalAndReleaseSlot(bool team, bool callerCancels, bool modelTimesOut)
    {
        await using var db = TestDbFactory.Create();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "agent deadline", DefinitionJson = "{}" };
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Workflow = workflow };
        db.Workflows.Add(workflow); db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var calls = 0;
        var client = new Mock<ILlmClient>();
        client.Setup(c => c.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (LlmRequest _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref calls) == 1 && team && modelTimesOut)
                    return new LlmResponse("", "test", ToolCalls: [new("delegate-1", "delegate", "{\"memberId\":\"researcher\",\"task\":\"Review\",\"reason\":\"Verify evidence\"}")]);
                await Task.Delay(Timeout.Infinite, token);
                return new LlmResponse("unreachable", "test");
            });
        var factory = new Mock<ILlmClientFactory>(); factory.Setup(f => f.Create(It.IsAny<LlmConnection>())).Returns(client.Object);
        var options = new AgentOptions { MaxConcurrentRuns = 1, ModelCallTimeoutSeconds = modelTimesOut ? 1 : 180 };
        var monitor = new Mock<IOptionsMonitor<AgentOptions>>(); monitor.SetupGet(m => m.CurrentValue).Returns(options);
        var llmOptions = new Mock<IOptionsMonitor<LlmOptions>>(); llmOptions.SetupGet(m => m.CurrentValue).Returns(new LlmOptions { Enabled = true });
        var redactor = new OutputRedactor(null);
        var journal = new AgentRunJournal(db, Mock.Of<IExecutionNotifier>(), redactor, NullLogger<AgentRunJournal>.Instance);
        using var gate = new AgentExecutionGate(Microsoft.Extensions.Options.Options.Create(options));
        var runner = new AgentActivityRunner(new AgentRuntime(factory.Object, llmOptions.Object),
            new AgentToolHost(null!, null!, db, null!, null!, new AgentExternalReadPolicy(monitor.Object)), gate, journal, monitor.Object, redactor, NullLogger<AgentActivityRunner>.Instance);
        var config = new AgentActivityConfiguration { Task = "Wait", TimeoutSeconds = callerCancels || modelTimesOut ? 60 : 1,
            Members = team ? [new() { Id = "lead", IsSupervisor = true }, new() { Id = "researcher", IsReviewer = true }] : [] };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (callerCancels) caller.CancelAfter(TimeSpan.FromSeconds(1));
        var task = runner.ExecuteAsync(new StepExecutionContext { WorkflowExecutionId = execution.Id, StepId = "agent" },
            JsonSerializer.SerializeToElement(config, AgentConfiguration.JsonOptions), team, caller.Token);
        if (callerCancels) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else Assert.False((await task).Success);
        Assert.Equal(team && modelTimesOut ? 2 : 1, calls);
        var run = await db.AgentRuns.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(callerCancels ? "Cancelled" : "Failed", run.Status);
        Assert.NotNull(run.CompletedAt);
        var modelErrors = await db.AgentRunEvents.Where(e => e.Kind == "model_failed").ToListAsync(TestContext.Current.CancellationToken);
        if (modelTimesOut) Assert.Equal(team ? "researcher" : "agent", Assert.Single(modelErrors).MemberId);
        else Assert.Empty(modelErrors);
        Assert.Equal("run_" + run.Status.ToLowerInvariant(), (await db.AgentRunEvents.OrderByDescending(e => e.Sequence).FirstAsync(TestContext.Current.CancellationToken)).Kind);
        using var gateDeadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        gateDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var available = await gate.AcquireAsync(gateDeadline.Token);
    }
}

// Keep the one-second deadline probes independent of unrelated heavy fixture setup.
[CollectionDefinition("AgentDeadlines", DisableParallelization = true)]
public sealed class AgentDeadlineCollection;

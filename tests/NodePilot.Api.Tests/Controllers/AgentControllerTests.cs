using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Agents;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Engine.Agents;
using NodePilot.Data.Security;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class AgentControllerTests
{
    [Fact]
    public async Task FailedFinalizationRetainsDurableRedactedDraftWithoutFinalAssessment()
    {
        await using var db = TestDbFactory.Create();
        var execution = await Seed(db);
        var journal = new AgentRunJournal(new AgentRunDatabase(db), Mock.Of<IExecutionNotifier>(), new OutputRedactor(null), NullLogger<AgentRunJournal>.Instance);
        await journal.StartAsync(new StepExecutionContext { WorkflowExecutionId = execution.Id, StepId = "agent" }, TestContext.Current.CancellationToken);
        await journal.AppendAsync(new AgentProgress("report_draft", "Earlier finding; password=do-not-expose", "lead"), TestContext.Current.CancellationToken);
        // The draft is durable before the final model call begins, including if the process dies.
        var before = await db.AgentRuns.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Earlier finding", before.Result);
        Assert.DoesNotContain("do-not-expose", before.Result);
        await journal.FinishAsync("Failed", null, "Final model call timed out", new AgentBudget(20, 20, 4), TestContext.Current.CancellationToken);
        var after = await db.AgentRuns.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Failed", after.Status);
        Assert.Equal(before.Result, after.Result);
        Assert.False(await db.AgentRunEvents.AnyAsync(e => e.Kind == "run_conclusion", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Succeeded", "partial")]
    [InlineData("Failed", "unassessed")]
    [InlineData("Running", "unassessed")]
    public async Task TaskOutcomeDoesNotReplaceTechnicalStatus(string status, string expectedOutcome)
    {
        await using var db = TestDbFactory.Create();
        var execution = await Seed(db);
        var run = new AgentRun { Id = Guid.NewGuid(), WorkflowExecutionId = execution.Id, StepId = "team", Status = status };
        db.AgentRuns.Add(run);
        db.AgentRunEvents.Add(new AgentRunEvent { AgentRunId = run.Id, Sequence = 1, Kind = "run_conclusion",
            Content = """{"outcome":"partial","reason":"Counterpart unavailable"}""" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var auth = new Mock<IResourceAuthorizationService>();
        auth.Setup(a => a.CanAccessWorkflowAsync(It.IsAny<ClaimsPrincipal>(), execution.Workflow.FolderId, ResourceOp.Read, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = new AgentsController(db, auth.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var response = Assert.IsType<OkObjectResult>((await controller.GetRuns(execution.Id, TestContext.Current.CancellationToken)).Result);
        var result = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AgentRunResponse>>(response.Value));
        Assert.Equal(status, result.Status);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(status == "Succeeded" ? "Counterpart unavailable" : null, result.OutcomeReason);
    }

    [Fact]
    public void McpReadApprovalsRequirePinnedContractsAndServerRevisions()
    {
        var dto = new NodePilot.Api.Dtos.Settings.AgentSettingsDto
        {
            ReadOnlyMcpTools = [new() { ServerId = Guid.NewGuid(), ToolName = "read", ServerUpdatedAt = DateTime.UtcNow.ToString("O"), ContractSha256 = new string('a', 64) }]
        };
        var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        Assert.True(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(dto, new(dto), errors, true));
        dto.ReadOnlyMcpTools = [dto.ReadOnlyMcpTools[0] with { ContractSha256 = new string('z', 64) }];
        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(dto, new(dto), errors, true));
        dto.ReadOnlyMcpTools = [dto.ReadOnlyMcpTools[0] with { ContractSha256 = new string('a', 64), ServerUpdatedAt = "" }];
        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(dto, new(dto), errors, true));
    }

    [Fact]
    public async Task McpRegistry_EncryptsSecrets_MasksResponses_AndRequiresReentryWhenDestinationChanges()
    {
        await using var db = TestDbFactory.Create();
        var protector = new AesGcmSecretProtector(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var controller = new AgentToolsController(db, protector, null!, NoopAuditWriter.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var id = Guid.NewGuid();
        var request = new SaveAgentMcpServerRequest { Name = "test", Transport = "streamableHttp", Endpoint = "https://example.invalid/mcp",
            Secrets = new() { ["Authorization"] = "Bearer never-return-me" } };
        var created = Assert.IsType<OkObjectResult>((await controller.SaveMcpServer(id, request, TestContext.Current.CancellationToken)).Result);
        var response = Assert.IsType<AgentMcpServerResponse>(created.Value);
        Assert.Equal(0, response.UpdatedAt.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.True(response.HasSecrets);
        Assert.DoesNotContain("never-return-me", System.Text.Json.JsonSerializer.Serialize(response));
        var stored = await db.AgentMcpServers.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Contains("never-return-me", protector.Unprotect(stored.ProtectedSecrets!));
        Assert.DoesNotContain("never-return-me", System.Text.Encoding.UTF8.GetString(stored.ProtectedSecrets!));
        request.Secrets = null; request.UpdatedAt = response.UpdatedAt; request.Endpoint = "https://other.invalid/mcp";
        Assert.IsType<BadRequestObjectResult>((await controller.SaveMcpServer(id, request, TestContext.Current.CancellationToken)).Result);
        request.Secrets = new();
        var changed = Assert.IsType<OkObjectResult>((await controller.SaveMcpServer(id, request, TestContext.Current.CancellationToken)).Result);
        Assert.False(Assert.IsType<AgentMcpServerResponse>(changed.Value).HasSecrets);
        Assert.IsType<ConflictObjectResult>((await controller.SaveMcpServer(id, request, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task McpSecretRotation_ReportsUndecryptableRows_WithoutLosingReadableCredentials()
    {
        await using var db = TestDbFactory.Create();
        var protector = new Mock<ISecretProtector>();
        protector.SetupGet(p => p.ProviderName).Returns("current");
        protector.Setup(p => p.Unprotect(It.Is<byte[]>(b => b.SequenceEqual(new byte[] { 1 })))).Returns("secret-json");
        protector.Setup(p => p.Unprotect(It.Is<byte[]>(b => b.SequenceEqual(new byte[] { 0 })))).Throws(new System.Security.Cryptography.CryptographicException());
        protector.Setup(p => p.Protect("secret-json")).Returns([2]);
        db.AgentMcpServers.AddRange(new AgentMcpServer { Id = Guid.NewGuid(), Name = "readable", ProtectedSecrets = [1] },
            new AgentMcpServer { Id = Guid.NewGuid(), Name = "unreadable", ProtectedSecrets = [0] });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var result = await DatabaseSecretRotation.ReencryptAgentMcpAsync(db, protector.Object, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Rewritten); Assert.Equal(1, result.Skipped);
        Assert.Equal("unreadable", Assert.Single(result.SkippedDetails).Name);
        var row = await db.AgentMcpServers.AsNoTracking().SingleAsync(s => s.Name == "readable", TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 2 }, row.ProtectedSecrets);
        Assert.Equal("current", row.SecretProvider);
    }

    [Fact]
    public async Task Journal_PersistsBeforeNotification_RedactsContent_AndUsesContiguousSequences()
    {
        await using var db = TestDbFactory.Create();
        var execution = await Seed(db);
        var notifier = new Mock<IExecutionNotifier>();
        var seen = new List<AgentEventNotification>();
        notifier.Setup(n => n.AgentEventAsync(It.IsAny<Guid>(), It.IsAny<AgentEventNotification>()))
            .Returns(async (Guid _, AgentEventNotification notification) =>
            {
                Assert.True(await db.AgentRunEvents.AsNoTracking().AnyAsync(e => e.AgentRunId == notification.AgentRunId && e.Sequence == notification.Sequence,
                    TestContext.Current.CancellationToken));
                seen.Add(notification);
                if (notification.Sequence == 2) throw new IOException("Disconnected subscriber");
            });
        var journal = new AgentRunJournal(new AgentRunDatabase(db), notifier.Object, new OutputRedactor(null), NullLogger<AgentRunJournal>.Instance);
        await journal.StartAsync(new StepExecutionContext { WorkflowExecutionId = execution.Id, StepId = "agent" }, TestContext.Current.CancellationToken);
        await journal.AppendAsync(new AgentProgress("tool_completed", "password=do-not-expose", "researcher", "files_read"), TestContext.Current.CancellationToken);
        var budget = new AgentBudget(20, 40, 0); budget.TakeModelCall(); budget.TakeToolCall();
        await journal.FinishAsync("Succeeded", "apiKey=never-expose", null, budget, TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 1, 2, 3 }, seen.Select(e => e.Sequence));
        Assert.DoesNotContain("do-not-expose", seen[1].Content);
        var persisted = await db.AgentRuns.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Succeeded", persisted.Status);
        Assert.DoesNotContain("never-expose", persisted.Result!);
        Assert.Equal(1, persisted.ToolCalls);
    }

    [Fact]
    public async Task RunEndpoints_UseOwningWorkflowReadPermission_AndPageAfterSequence()
    {
        await using var db = TestDbFactory.Create();
        var execution = await Seed(db);
        var run = new AgentRun { Id = Guid.NewGuid(), WorkflowExecutionId = execution.Id, StepId = "team" };
        db.AgentRuns.Add(run);
        for (var sequence = 1; sequence <= 6; sequence++) db.AgentRunEvents.Add(new AgentRunEvent { AgentRunId = run.Id, Sequence = sequence, Kind = "tool_completed", Content = "excerpt" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var auth = new Mock<IResourceAuthorizationService>();
        var controller = new AgentsController(db, auth.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<NotFoundResult>((await controller.GetRuns(execution.Id, TestContext.Current.CancellationToken)).Result);
        Assert.IsType<NotFoundResult>((await controller.GetEvents(run.Id, ct: TestContext.Current.CancellationToken)).Result);
        auth.Setup(a => a.CanAccessWorkflowAsync(It.IsAny<ClaimsPrincipal>(), execution.Workflow.FolderId, ResourceOp.Read, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var response = Assert.IsType<OkObjectResult>((await controller.GetEvents(run.Id, 2, 2, TestContext.Current.CancellationToken)).Result);
        var events = Assert.IsAssignableFrom<IReadOnlyList<AgentRunEventResponse>>(response.Value);
        Assert.Equal(new long[] { 3, 4 }, events.Select(e => e.Sequence));
        Assert.IsType<OkObjectResult>((await controller.GetRuns(execution.Id, TestContext.Current.CancellationToken)).Result);
    }

    [Fact]
    public async Task Recovery_EndsOrphanOnceWithoutReplayingActions_AndRetentionCascades()
    {
        await using var db = TestDbFactory.Create();
        var execution = await Seed(db);
        execution.Status = ExecutionStatus.Cancelled;
        var run = new AgentRun { Id = Guid.NewGuid(), WorkflowExecutionId = execution.Id, StepId = "agent" };
        db.AgentRuns.Add(run);
        db.AgentRunEvents.Add(new AgentRunEvent { AgentRunId = run.Id, Sequence = 1, Kind = "tool_started", Content = "action may already have run" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await AgentRecoveryService.ReconcileAsync(db, TestContext.Current.CancellationToken);
        await AgentRecoveryService.ReconcileAsync(db, TestContext.Current.CancellationToken);
        Assert.Equal("Cancelled", (await db.AgentRuns.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Status);
        var events = await db.AgentRunEvents.AsNoTracking().OrderBy(e => e.Sequence).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, events.Count);
        Assert.Equal("run_cancelled", events[1].Kind);
        db.ChangeTracker.Clear();
        await db.WorkflowExecutions.Where(e => e.Id == execution.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await db.AgentRuns.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await db.AgentRunEvents.ToListAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<WorkflowExecution> Seed(NodePilot.Data.NodePilotDbContext db)
    {
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Agents" };
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Workflow = workflow, Status = ExecutionStatus.Running };
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return execution;
    }
}

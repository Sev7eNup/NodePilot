using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Enums;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class MaintenanceWindowEnforcementTests
{
    private static ExecutionsController BuildController(
        NodePilotDbContext db, IMaintenanceWindowEvaluator evaluator, string role)
    {
        var provider = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton(Mock.Of<IWorkflowEngine>())
            .BuildServiceProvider();
        var dispatch = new ExecutionDispatchService(
            db, new NoopExecutionDispatchQueue(), provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null), new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            evaluator, NullLogger<ExecutionDispatchService>.Instance);

        var ctrl = new ExecutionsController(
            db, Mock.Of<IWorkflowEngine>(), dispatch, new OutputRedactor(null),
            NoopAuditWriter.Instance, new AlwaysAllowAuthorizationService(), evaluator);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };
        return ctrl;
    }

    private static async Task<Workflow> SeedWorkflowAsync(NodePilotDbContext db)
    {
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();
        return wf;
    }

    [Fact]
    public async Task Execute_BlockedByWindow_Returns423()
    {
        await using var db = TestDbFactory.Create();
        var wf = await SeedWorkflowAsync(db);
        var ctrl = BuildController(db, StubMaintenanceWindowEvaluator.Blocking("PatchWindow"), "Operator");

        var result = await ctrl.Execute(wf.Id, null, CancellationToken.None);

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status423Locked);
    }

    [Fact]
    public async Task Execute_BlockedForceAsAdmin_BypassesAndAccepts()
    {
        await using var db = TestDbFactory.Create();
        var wf = await SeedWorkflowAsync(db);
        var ctrl = BuildController(db, StubMaintenanceWindowEvaluator.Blocking("PatchWindow"), "Admin");

        var result = await ctrl.Execute(wf.Id, null, CancellationToken.None, force: true);

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        // A pending execution row was actually created (the run was admitted past the gate).
        (await db.WorkflowExecutions.CountAsync(e => e.WorkflowId == wf.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Execute_BlockedForceAsOperator_Forbidden()
    {
        await using var db = TestDbFactory.Create();
        var wf = await SeedWorkflowAsync(db);
        var ctrl = BuildController(db, StubMaintenanceWindowEvaluator.Blocking("PatchWindow"), "Operator");

        var result = await ctrl.Execute(wf.Id, null, CancellationToken.None, force: true);

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Execute_NotBlocked_Accepts()
    {
        await using var db = TestDbFactory.Create();
        var wf = await SeedWorkflowAsync(db);
        var ctrl = BuildController(db, StubMaintenanceWindowEvaluator.AllowAll, "Operator");

        var result = await ctrl.Execute(wf.Id, null, CancellationToken.None);

        (result.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status202Accepted);
    }

    [Fact]
    public async Task DispatchGate_BlockedVerdict_MarksCancelledAndSuppresses()
    {
        await using var db = TestDbFactory.Create();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true };
        var pending = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow, TriggeredBy = "scheduleTrigger",
        };
        db.Workflows.Add(wf);
        db.WorkflowExecutions.Add(pending);
        await db.SaveChangesAsync();

        var provider = new ServiceCollection()
            .AddSingleton(db).AddSingleton(Mock.Of<IWorkflowEngine>()).BuildServiceProvider();
        var queue = new CapturingQueue();
        var service = new ExecutionDispatchService(
            db, queue, provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null), new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            StubMaintenanceWindowEvaluator.Blocking("PatchWindow"), NullLogger<ExecutionDispatchService>.Instance);

        WorkflowDispatchSuppression? suppression = null;
        await service.EnqueueAsync(
            pending,
            new WorkflowDispatchIntent(wf.Id, "scheduleTrigger", null, RequireWorkflowEnabled: true,
                OnDispatchSuppressedAsync: (s, _) => { suppression = s; return Task.CompletedTask; }),
            CancellationToken.None);

        await queue.Work!(CancellationToken.None);

        var persisted = await db.WorkflowExecutions.FindAsync(pending.Id);
        persisted!.Status.Should().Be(ExecutionStatus.Cancelled);
        suppression.Should().NotBeNull();
        suppression!.Reason.Should().Be("maintenance_window_blocked");
    }

    [Fact]
    public async Task DispatchGate_RequireCheckFalse_NotBlockedEvenWhenWindowActive()
    {
        // Retry/resume/sub-workflows set RequireMaintenanceWindowCheck=false: recovery of an
        // already-known run is never re-gated, even while a window is active.
        await using var db = TestDbFactory.Create();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true };
        var pending = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow, TriggeredBy = "retry:abc",
        };
        db.Workflows.Add(wf);
        db.WorkflowExecutions.Add(pending);
        await db.SaveChangesAsync();

        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(e => e.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<bool>()))
            .ReturnsAsync(pending);
        var provider = new ServiceCollection()
            .AddSingleton(db).AddSingleton(engine.Object).BuildServiceProvider();
        var queue = new CapturingQueue();
        var service = new ExecutionDispatchService(
            db, queue, provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null), new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            StubMaintenanceWindowEvaluator.Blocking("PatchWindow"), NullLogger<ExecutionDispatchService>.Instance);

        WorkflowDispatchSuppression? suppression = null;
        await service.EnqueueAsync(
            pending,
            new WorkflowDispatchIntent(wf.Id, "retry:abc", null, RequireWorkflowEnabled: true,
                RequireMaintenanceWindowCheck: false,
                OnDispatchSuppressedAsync: (s, _) => { suppression = s; return Task.CompletedTask; }),
            CancellationToken.None);

        await queue.Work!(CancellationToken.None);

        // The gate was skipped: the engine ran and nothing was suppressed.
        engine.Verify(e => e.ExecuteAsync(
            It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
            It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
            It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<bool>()), Times.Once);
        suppression.Should().BeNull();
    }

    [Fact]
    public async Task DispatchGate_BlockedVerdict_RemovesIdempotencyKeyForRetryAfterWindow()
    {
        // Race: the window opened between the external caller's early check and this worker
        // pickup, so the idempotency key was already committed pointing at the now-cancelled row.
        // The gate must drop it, otherwise the same key replays the Cancelled ghost for 24h.
        await using var db = TestDbFactory.Create();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}", IsEnabled = true };
        var pending = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow, TriggeredBy = "api",
        };
        db.Workflows.Add(wf);
        db.WorkflowExecutions.Add(pending);
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(), Key = "race-key", WorkflowId = wf.Id, ExecutionId = pending.Id,
            FirstSeenAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(24),
        });
        await db.SaveChangesAsync();

        var provider = new ServiceCollection()
            .AddSingleton(db).AddSingleton(Mock.Of<IWorkflowEngine>()).BuildServiceProvider();
        var queue = new CapturingQueue();
        var service = new ExecutionDispatchService(
            db, queue, provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null), new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            StubMaintenanceWindowEvaluator.Blocking("PatchWindow"), NullLogger<ExecutionDispatchService>.Instance);

        await service.EnqueueAsync(
            pending,
            new WorkflowDispatchIntent(wf.Id, "api", null, RequireWorkflowEnabled: true),
            CancellationToken.None);
        await queue.Work!(CancellationToken.None);

        (await db.WorkflowExecutions.FindAsync(pending.Id))!.Status.Should().Be(ExecutionStatus.Cancelled);
        (await db.IdempotencyKeys.CountAsync()).Should().Be(0, "the key must be dropped so a retry after the window reopens runs");
    }

    private sealed class CapturingQueue : IExecutionDispatchQueue
    {
        public Func<CancellationToken, Task>? Work { get; private set; }
        public ValueTask EnqueueAsync(Func<CancellationToken, Task> workItem, CancellationToken ct,
            ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal)
        {
            Work = workItem;
            return ValueTask.CompletedTask;
        }
    }
}

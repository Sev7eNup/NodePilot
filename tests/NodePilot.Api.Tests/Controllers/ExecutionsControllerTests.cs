using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class ExecutionsControllerTests
{
    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static ExecutionDispatchService CreateDispatchService(
        NodePilotDbContext db,
        IWorkflowEngine engine,
        IExecutionDispatchQueue? dispatchQueue = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(engine);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:MaxAuthorizationStalenessMinutes"] = "15",
            }).Build());
        services.AddSingleton<IResourceAuthorizationService>(new AlwaysAllowAuthorizationService());
        var provider = services.BuildServiceProvider();
        return new ExecutionDispatchService(
            db,
            dispatchQueue ?? new NoopExecutionDispatchQueue(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.Api.Tests.TestSupport.StubMaintenanceWindowEvaluator.AllowAll,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionDispatchService>.Instance);
    }

    // Shared controller factory. Sets up an Admin-claim HttpContext so IsPrivileged / Scrub
    // don't NullReference (they read User.IsInRole). Individual tests can override the
    // principal by reassigning ControllerContext.HttpContext.User afterward.
    private static ExecutionsController NewController(
        NodePilotDbContext db,
        IWorkflowEngine engine,
        IExecutionDispatchQueue? dispatchQueue = null,
        IAuditWriter? audit = null)
    {
        var controller = new ExecutionsController(
            db, engine, CreateDispatchService(db, engine, dispatchQueue), new OutputRedactor(null),
            audit ?? NoopAuditWriter.Instance,
            new AlwaysAllowAuthorizationService(),
            NodePilot.Api.Tests.TestSupport.StubMaintenanceWindowEvaluator.AllowAll);
        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin") },
                "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    [Fact]
    public async Task GetAll_ReturnsExecutions()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var exec1 = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var exec2 = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        db.WorkflowExecutions.AddRange(exec1, exec2);
        await db.SaveChangesAsync();

        var mockEngine = new Mock<IWorkflowEngine>();
        var controller = NewController(db, mockEngine.Object);

        // Act
        var result = await controller.GetAll(null, activeOnly: false, terminalOnly: false, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var executions = ok.Value.Should().BeAssignableTo<List<ExecutionResponse>>().Subject;
        executions.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAll_WithWorkflowId_FiltersResults()
    {
        // Arrange
        var db = CreateContext();
        var wf1 = new Workflow { Id = Guid.NewGuid(), Name = "WF1", DefinitionJson = "{}" };
        var wf2 = new Workflow { Id = Guid.NewGuid(), Name = "WF2", DefinitionJson = "{}" };
        db.Workflows.AddRange(wf1, wf2);

        var exec1 = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = wf1.Id,
            Status = ExecutionStatus.Succeeded
        };
        var exec2 = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = wf2.Id,
            Status = ExecutionStatus.Running
        };
        db.WorkflowExecutions.AddRange(exec1, exec2);
        await db.SaveChangesAsync();

        var mockEngine = new Mock<IWorkflowEngine>();
        var controller = NewController(db, mockEngine.Object);

        // Act
        var result = await controller.GetAll(wf1.Id, activeOnly: false, terminalOnly: false, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var executions = ok.Value.Should().BeAssignableTo<List<ExecutionResponse>>().Subject;
        executions.Should().HaveCount(1);
        executions[0].WorkflowId.Should().Be(wf1.Id);
    }

    [Fact]
    public async Task GetSteps_ReturnsStepsForExecution()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded
        };
        db.WorkflowExecutions.Add(execution);

        var step1 = new StepExecution
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            StepId = "step-1",
            StepType = "RunScript",
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            Output = "Hello"
        };
        var step2 = new StepExecution
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            StepId = "step-2",
            StepType = "FileOperation",
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-3)
        };
        db.StepExecutions.AddRange(step1, step2);
        await db.SaveChangesAsync();

        var mockEngine = new Mock<IWorkflowEngine>();
        var controller = NewController(db, mockEngine.Object);

        // Act
        var result = await controller.GetSteps(execution.Id, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var steps = ok.Value.Should().BeAssignableTo<List<StepExecutionResponse>>().Subject;
        steps.Should().HaveCount(2);
        steps[0].StepId.Should().Be("step-1");
        steps[1].StepId.Should().Be("step-2");
    }

    [Fact]
    public async Task Execute_ValidWorkflow_Returns202Accepted()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var mockEngine = new Mock<IWorkflowEngine>();
        var queue = new CountingNoopExecutionDispatchQueue();
        var controller = NewController(db, mockEngine.Object, queue);

        // Act
        var result = await controller.Execute(workflow.Id, null, CancellationToken.None);

        // Assert
        var accepted = result.Result.Should().BeOfType<AcceptedResult>().Subject;
        var response = accepted.Value.Should().BeOfType<ExecutionResponse>().Subject;
        response.Id.Should().NotBeEmpty();
        response.WorkflowId.Should().Be(workflow.Id);
        response.Status.Should().Be("Pending");
        response.TriggeredBy.Should().Be("manual");
        // Interactive runs are priority-queued, but still consume the bounded worker pool.
        queue.EnqueueCount.Should().Be(1);
        queue.LastPriority.Should().Be(ExecutionDispatchPriority.Interactive);

        var pending = await db.WorkflowExecutions.FindAsync(response.Id);
        pending.Should().NotBeNull();
        pending!.Status.Should().Be(ExecutionStatus.Pending);
    }

    [Fact]
    public async Task Execute_ValidWorkflow_WritesExecutionStartedAudit()
    {
        // Audit-trail symmetry with EXECUTION_CANCELLED/RETRIED/RESUMED: a manual run-start
        // is its own audit event. Without this the audit timeline can answer "who cancelled"
        // but not "who started" without joining the Executions table.
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Audited", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var controller = NewController(db, new Mock<IWorkflowEngine>().Object, audit: audit);

        var result = await controller.Execute(workflow.Id,
            new ExecuteWorkflowRequest(new Dictionary<string, string> { ["env"] = "prod" }),
            CancellationToken.None);

        var accepted = result.Result.Should().BeOfType<AcceptedResult>().Subject;
        var response = accepted.Value.Should().BeOfType<ExecutionResponse>().Subject;

        var call = audit.Calls.Should().ContainSingle(c => c.Action == "EXECUTION_STARTED").Subject;
        call.ResourceType.Should().Be("Execution");
        call.ResourceId.Should().Be(response.Id);
        call.Details.Should().Contain("\"workflowName\":\"Audited\"");
        call.Details.Should().Contain("\"trigger\":\"manual\"");
        call.Details.Should().Contain("\"parameterCount\":1");
    }

    [Fact]
    public async Task Execute_DisabledWorkflow_DoesNotEmitExecutionStarted()
    {
        // Disabled workflows are rejected before dispatch. No EXECUTION_STARTED row should
        // be written — only successful starts produce the event.
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Off", DefinitionJson = "{}", IsEnabled = false };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var controller = NewController(db, new Mock<IWorkflowEngine>().Object, audit: audit);

        var result = await controller.Execute(workflow.Id, null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        audit.Calls.Should().NotContain(c => c.Action == "EXECUTION_STARTED");
    }

    [Fact]
    public async Task Execute_WorkflowNotFound_Returns404()
    {
        // Arrange
        var db = CreateContext();
        var mockEngine = new Mock<IWorkflowEngine>();
        var controller = NewController(db, mockEngine.Object);

        // Act
        var result = await controller.Execute(Guid.NewGuid(), null, CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Retry_TerminalExecution_ReturnsPersistedPendingExecution()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var original = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4),
            TriggeredBy = "manual",
            InputParametersJson = """{"host":"server-1"}""",
        };
        db.WorkflowExecutions.Add(original);
        await db.SaveChangesAsync();

        var mockEngine = new Mock<IWorkflowEngine>();
        var controller = NewController(db, mockEngine.Object);

        // Act
        var result = await controller.Retry(original.Id, CancellationToken.None);

        // Assert
        var accepted = result.Result.Should().BeOfType<AcceptedResult>().Subject;
        var response = accepted.Value.Should().BeOfType<ExecutionResponse>().Subject;
        response.Id.Should().NotBeEmpty();
        response.Id.Should().NotBe(original.Id);
        response.WorkflowId.Should().Be(workflow.Id);
        response.Status.Should().Be("Pending");
        response.TriggeredBy.Should().Be($"retry:{original.Id}");
        response.InputParametersJson.Should().Be("""{"host":"server-1"}""");

        var pending = await db.WorkflowExecutions.FindAsync(response.Id);
        pending.Should().NotBeNull();
        pending!.Status.Should().Be(ExecutionStatus.Pending);
    }

    [Fact]
    public async Task Cancel_ExistingExecution_Returns204()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Running
        };
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        var mockEngine = new Mock<IWorkflowEngine>();
        mockEngine.Setup(e => e.CancelAsync(execution.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = NewController(db, mockEngine.Object);

        // Act
        var result = await controller.Cancel(execution.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        mockEngine.Verify(e => e.CancelAsync(execution.Id, "user", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_PendingExecution_CancelsRowWithoutEngineToken()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending,
            TriggeredBy = "manual",
        };
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        var mockEngine = new Mock<IWorkflowEngine>();
        mockEngine.Setup(e => e.CancelAsync(execution.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var controller = NewController(db, mockEngine.Object);

        // Act
        var result = await controller.Cancel(execution.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var cancelled = await db.WorkflowExecutions.FindAsync(execution.Id);
        cancelled!.Status.Should().Be(ExecutionStatus.Cancelled);
        cancelled.CompletedAt.Should().NotBeNull();
        cancelled.ErrorMessage.Should().Contain("before dispatch");
    }

    [Fact]
    public async Task Cancel_NotFound_Returns404()
    {
        // Arrange
        var db = CreateContext();
        var mockEngine = new Mock<IWorkflowEngine>();
        var controller = NewController(db, mockEngine.Object);

        // Act
        var result = await controller.Cancel(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    // ---- ExternalTrigger ----
    // NOTE: The endpoint now enforces a minimum API-key length of 32 bytes (M-2 hardening,
    // a security-audit finding).
    // Tests that exercise the "correct key" path therefore use a long key; short-key tests
    // still exercise the explicit rejection path (either too short → 401, or mismatch → 401).

    // 32-byte test key — matches MinExternalApiKeyBytes.
    private const string LongKey = "test-api-key-needs-32-bytes-yep!";

    private static readonly NullLogger<ExternalTriggerController> TriggerLogger = NullLogger<ExternalTriggerController>.Instance;

    private sealed class ImmediateExecutionDispatchQueue : IExecutionDispatchQueue
    {
        public int EnqueueCount { get; private set; }

        public ValueTask EnqueueAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken ct,
            ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal)
        {
            EnqueueCount++;
            return new ValueTask(workItem(ct));
        }
    }

    private sealed class CountingNoopExecutionDispatchQueue : IExecutionDispatchQueue
    {
        public int EnqueueCount { get; private set; }
        public ExecutionDispatchPriority LastPriority { get; private set; }

        public ValueTask EnqueueAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken ct,
            ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal)
        {
            EnqueueCount++;
            LastPriority = priority;
            return ValueTask.CompletedTask;
        }
    }

    private static ExternalTriggerController CreateTriggerController(
        NodePilotDbContext db,
        IWorkflowEngine engine,
        string? presentedKey,
        IExecutionDispatchQueue? dispatchQueue = null,
        IAuditWriter? audit = null,
        NodePilot.Core.Interfaces.IMaintenanceWindowEvaluator? maintenance = null,
        NodePilot.Engine.Security.OutputRedactor? redactor = null)
    {
        var controller = new ExternalTriggerController(
            db, CreateDispatchService(db, engine, dispatchQueue), audit ?? NoopAuditWriter.Instance,
            maintenance ?? NodePilot.Api.Tests.TestSupport.StubMaintenanceWindowEvaluator.AllowAll,
            redactor ?? new NodePilot.Engine.Security.OutputRedactor());
        var httpCtx = new DefaultHttpContext();
        if (presentedKey is not null)
            httpCtx.Request.Headers["X-Api-Key"] = presentedKey;
        controller.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return controller;
    }

    private static IConfiguration ConfigWithKey(string? key)
    {
        var builder = new ConfigurationBuilder();
        if (key is not null)
            builder.AddInMemoryCollection(new Dictionary<string, string?> { ["ExternalTrigger:ApiKey"] = key });
        return builder.Build();
    }

    [Fact]
    public async Task ExternalTrigger_NoApiKeyConfigured_ReturnsUnauthorized()
    {
        // Previously this returned 503, which confirmed to an unauthenticated caller that
        // the endpoint existed but was unconfigured. The hardened endpoint now returns 401
        // indistinguishable from "wrong key" so callers cannot enumerate misconfigurations.
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: "anything");

        var result = await controller.ExternalTrigger("Any", null, ConfigWithKey(null), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_ConfiguredKeyTooShort_ReturnsUnauthorized()
    {
        // A short configured key (below MinExternalApiKeyBytes) is rejected at request time
        // so a fat-fingered value in appsettings.json does not become a weak secret.
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: "short-key");

        var result = await controller.ExternalTrigger("Any", null, ConfigWithKey("short-key"), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_MissingHeader_ReturnsUnauthorized()
    {
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: null);

        var result = await controller.ExternalTrigger("Any", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_WrongKey_ReturnsUnauthorized()
    {
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: "wrong-but-also-32-bytes-padding!");

        var result = await controller.ExternalTrigger("Any", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_WrongKeyLength_ReturnsUnauthorized()
    {
        // Regression: FixedTimeEquals returns false for length-mismatch without throwing.
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: "x");

        var result = await controller.ExternalTrigger("Any", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_CorrectKeyButWorkflowNotFound_Returns404()
    {
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey);

        var result = await controller.ExternalTrigger("missing", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_DisabledWorkflow_ReturnsNotFound()
    {
        // Security-audit finding M-29: external trigger collapses "not found" and "exists but disabled" into the same
        // 404. Previously a BadRequest for disabled let a holder of a valid API key enumerate
        // which named workflows exist even while disabled.
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Off", DefinitionJson = "{}", IsEnabled = false });
        await db.SaveChangesAsync();

        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey);

        var result = await controller.ExternalTrigger("Off", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_CorrectKey_EnqueuesPendingExecutionAndInvokesEngine()
    {
        var db = CreateContext();
        var publisher = new User
        {
            Id = Guid.NewGuid(), Username = "publisher", PasswordHash = "hash",
            Role = UserRole.Admin, IsActive = true,
        };
        var wf = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Enabled", DefinitionJson = "{}", IsEnabled = true,
            PublishedByUserId = publisher.Id,
        };
        db.AddRange(publisher, wf);
        await db.SaveChangesAsync();

        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(e => e.ExecuteAsync(
                It.IsAny<Workflow>(),
                "api",
                It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<int>(),
                It.IsAny<Guid?>()))
              .ReturnsAsync(new WorkflowExecution
              {
                  Id = Guid.NewGuid(),
                  WorkflowId = wf.Id,
                  Status = ExecutionStatus.Succeeded,
              });

        var queue = new ImmediateExecutionDispatchQueue();
        var controller = CreateTriggerController(db, engine.Object, presentedKey: LongKey, queue);

        var result = await controller.ExternalTrigger("Enabled", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<AcceptedResult>();
        queue.EnqueueCount.Should().Be(1);

        // Dispatcher fires engine.ExecuteAsync as an unrooted Task so the worker can
        // pick up the next queued execution immediately. The engine call lands on the
        // ThreadPool a few moments later — poll briefly before asserting.
        await WaitForInvocationAsync(engine, TimeSpan.FromSeconds(5));

        engine.Verify(e => e.ExecuteAsync(
            It.Is<Workflow>(w => w.Id == wf.Id),
            "api",
            It.IsAny<CancellationToken>(),
            It.Is<Dictionary<string, string>?>(p => p == null),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<Guid?>(),
            It.IsAny<Guid?>(),
            It.IsAny<int>(),
            It.Is<Guid?>(executionId => executionId.HasValue)), Times.Once);
    }

    private static async Task WaitForInvocationAsync(Mock<IWorkflowEngine> engine, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (engine.Invocations.Count > 0) return;
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ExternalTrigger_BlockedByMaintenanceWindow_Returns404AndDoesNotConsumeIdempotencyKey()
    {
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Enabled", DefinitionJson = "{}", IsEnabled = true };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var queue = new CountingNoopExecutionDispatchQueue();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue,
            maintenance: NodePilot.Api.Tests.TestSupport.StubMaintenanceWindowEvaluator.Blocking("PatchWindow"));
        controller.HttpContext.Request.Headers["Idempotency-Key"] = "blocked-request";

        var result = await controller.ExternalTrigger("Enabled", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        // Uniform 404 (anti-enumeration) + the critical invariant: the maintenance check runs
        // BEFORE the idempotency-key transaction, so a blocked fire neither persists the key nor
        // a Pending row — a legitimate retry after the window reopens then actually runs.
        result.Result.Should().BeOfType<NotFoundObjectResult>();
        queue.EnqueueCount.Should().Be(0);
        (await db.IdempotencyKeys.CountAsync()).Should().Be(0, "a blocked fire must not consume its idempotency key");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0, "a blocked fire must not create an execution row");
    }

    [Fact]
    public async Task ExternalTrigger_IdempotencyKey_ReplayReturnsPendingExecutionWithoutSecondEnqueue()
    {
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Enabled", DefinitionJson = "{}", IsEnabled = true };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var engine = new Mock<IWorkflowEngine>();
        var queue = new CountingNoopExecutionDispatchQueue();

        var first = CreateTriggerController(db, engine.Object, presentedKey: LongKey, queue);
        first.HttpContext.Request.Headers["Idempotency-Key"] = "same-request";
        var firstResult = await first.ExternalTrigger("Enabled", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        firstResult.Result.Should().BeOfType<AcceptedResult>();
        queue.EnqueueCount.Should().Be(1);
        (await db.IdempotencyKeys.CountAsync()).Should().Be(1);

        var second = CreateTriggerController(db, engine.Object, presentedKey: LongKey, queue);
        second.HttpContext.Request.Headers["Idempotency-Key"] = "same-request";
        var secondResult = await second.ExternalTrigger("Enabled", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        secondResult.Result.Should().BeOfType<OkObjectResult>();
        second.Response.Headers["Idempotent-Replayed"].ToString().Should().Be("true");
        queue.EnqueueCount.Should().Be(1);
        engine.Verify(e => e.ExecuteAsync(
            It.IsAny<Workflow>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<Guid?>(),
            It.IsAny<Guid?>(),
            It.IsAny<int>(),
            It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task ExternalTrigger_Replay_RedactsSensitiveExecutionFields()
    {
        // L-7 (security audit 2026-05-15): the API-key trigger surface carries no role, so it
        // must redact ReturnData / ErrorMessage / InputParametersJson exactly like
        // ExecutionsController does for callers below Admin/Operator — otherwise step-stdout
        // tokens or webhook-body secrets leak straight back to the API-key holder.
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Enabled", DefinitionJson = "{}", IsEnabled = true };
        db.Workflows.Add(wf);
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            ReturnData = "result token=SECRET-XYZ",
            ErrorMessage = "failure detail SECRET-XYZ",
            InputParametersJson = "{\"pw\":\"SECRET-XYZ\"}",
        };
        db.WorkflowExecutions.Add(exec);
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            Id = Guid.NewGuid(), Key = "replay-secret", WorkflowId = wf.Id,
            ExecutionId = exec.Id, FirstSeenAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await db.SaveChangesAsync();

        var redactorConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:Redaction:Enabled"] = "true",
            ["Logging:Redaction:Patterns:0"] = "SECRET-[A-Z]+",
        }).Build();
        var redactor = new NodePilot.Engine.Security.OutputRedactor(redactorConfig);

        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, redactor: redactor);
        controller.HttpContext.Request.Headers["Idempotency-Key"] = "replay-secret";

        var result = await controller.ExternalTrigger("Enabled", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeOfType<ExecutionResponse>().Subject;
        resp.ReturnData.Should().NotContain("SECRET-XYZ", "ReturnData must be redacted on the external-trigger surface");
        resp.ErrorMessage.Should().NotContain("SECRET-XYZ", "ErrorMessage must be redacted on the external-trigger surface");
        resp.InputParametersJson.Should().NotContain("SECRET-XYZ", "InputParametersJson must be redacted on the external-trigger surface");
    }

    [Fact]
    public async Task ExternalTrigger_CorrectKey_WritesExternalTriggerFiredAudit()
    {
        // Anonymous external invocations must leave an audit trail. Without it, an attacker
        // (or a buggy integration) holding the API key can fire workflows without trace.
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Audited", DefinitionJson = "{}", IsEnabled = true };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var queue = new CountingNoopExecutionDispatchQueue();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue, audit);

        var result = await controller.ExternalTrigger("Audited",
            new ExecuteWorkflowRequest(new Dictionary<string, string> { ["v"] = "1" }),
            ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        var accepted = result.Result.Should().BeOfType<AcceptedResult>().Subject;
        var response = accepted.Value.Should().BeOfType<ExecutionResponse>().Subject;

        var call = audit.Calls.Should().ContainSingle(c => c.Action == "EXTERNAL_TRIGGER_FIRED").Subject;
        call.ResourceType.Should().Be("Workflow");
        call.ResourceId.Should().Be(wf.Id);
        call.Details.Should().Contain("\"workflowName\":\"Audited\"");
        call.Details.Should().Contain($"\"executionId\":\"{response.Id}\"");
        call.Details.Should().Contain("\"idempotencyKeyUsed\":false");
        call.Details.Should().Contain("\"parameterCount\":1");
    }

    [Fact]
    public async Task ExternalTrigger_IdempotencyReplay_DoesNotEmitSecondAudit()
    {
        // Idempotency replays return the original execution — they must NOT emit a second
        // EXTERNAL_TRIGGER_FIRED. Otherwise a misbehaving caller retrying the same key
        // would inflate the audit log.
        var db = CreateContext();
        var wf = new Workflow { Id = Guid.NewGuid(), Name = "Enabled", DefinitionJson = "{}", IsEnabled = true };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var queue = new CountingNoopExecutionDispatchQueue();

        var first = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue, audit);
        first.HttpContext.Request.Headers["Idempotency-Key"] = "replay-key";
        await first.ExternalTrigger("Enabled", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        var second = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue, audit);
        second.HttpContext.Request.Headers["Idempotency-Key"] = "replay-key";
        var secondResult = await second.ExternalTrigger("Enabled", null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        secondResult.Result.Should().BeOfType<OkObjectResult>();
        audit.Calls.Where(c => c.Action == "EXTERNAL_TRIGGER_FIRED").Should().HaveCount(1,
            "the replay-branch returns before the audit call — only the first fire emits an audit row");
    }

    [Fact]
    public async Task GetAll_ActiveOnly_ReturnsOnlyRunningPendingPaused()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var running = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow.AddSeconds(-5) };
        var pending = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Pending, StartedAt = DateTime.UtcNow.AddSeconds(-3) };
        var succeeded = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Succeeded, StartedAt = DateTime.UtcNow.AddSeconds(-10), CompletedAt = DateTime.UtcNow.AddSeconds(-2) };
        var failed = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Failed, StartedAt = DateTime.UtcNow.AddSeconds(-15), CompletedAt = DateTime.UtcNow.AddSeconds(-8) };
        db.WorkflowExecutions.AddRange(running, pending, succeeded, failed);
        await db.SaveChangesAsync();

        var controller = NewController(db, new Mock<IWorkflowEngine>().Object);

        // Act
        var result = await controller.GetAll(workflow.Id, activeOnly: true, terminalOnly: false, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var executions = ok.Value.Should().BeAssignableTo<List<ExecutionResponse>>().Subject;
        executions.Should().HaveCount(2, "only Running and Pending should be returned");
        executions.Select(e => e.Status).Should().BeEquivalentTo(new[] { "Running", "Pending" });
        executions.Select(e => e.Id).Should().NotContain(succeeded.Id);
        executions.Select(e => e.Id).Should().NotContain(failed.Id);
    }

    [Fact]
    public async Task GetAll_TerminalOnly_ReturnsOnlySucceededFailedCancelled()
    {
        // History-tab filter: the live channel shows Running/Pending/Paused; History should
        // only show finished runs, so the same job doesn't show up in both tabs.
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var running = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow.AddSeconds(-5) };
        var pending = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Pending, StartedAt = DateTime.UtcNow.AddSeconds(-3) };
        var paused = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Paused, StartedAt = DateTime.UtcNow.AddSeconds(-4) };
        var succeeded = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Succeeded, StartedAt = DateTime.UtcNow.AddSeconds(-10), CompletedAt = DateTime.UtcNow.AddSeconds(-2) };
        var failed = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Failed, StartedAt = DateTime.UtcNow.AddSeconds(-15), CompletedAt = DateTime.UtcNow.AddSeconds(-8) };
        var cancelled = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Cancelled, StartedAt = DateTime.UtcNow.AddSeconds(-20), CompletedAt = DateTime.UtcNow.AddSeconds(-12) };
        db.WorkflowExecutions.AddRange(running, pending, paused, succeeded, failed, cancelled);
        await db.SaveChangesAsync();

        var controller = NewController(db, new Mock<IWorkflowEngine>().Object);

        var result = await controller.GetAll(workflow.Id, activeOnly: false, terminalOnly: true, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var executions = ok.Value.Should().BeAssignableTo<List<ExecutionResponse>>().Subject;
        executions.Should().HaveCount(3);
        executions.Select(e => e.Status).Should().BeEquivalentTo(new[] { "Succeeded", "Failed", "Cancelled" });
        executions.Select(e => e.Id).Should().NotContain(running.Id);
        executions.Select(e => e.Id).Should().NotContain(pending.Id);
        executions.Select(e => e.Id).Should().NotContain(paused.Id);
    }

    [Fact]
    public async Task GetAll_PopulatesTriageColumns_StartedByUserAndStepCountsAndFailedStep()
    {
        // The history-grid triage columns are the contract between GetAll and the UI: each
        // run must carry `StartedByUsername`, `StepsTotal/Completed`, and the first failed
        // step. A single finished Failed run with 3 steps — 1 Skipped and 1 Failed —
        // exercises the full path.
        var db = CreateContext();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "alice",
            PasswordHash = "x",
            Role = UserRole.Operator,
        };
        db.Users.Add(user);

        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);

        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
            CompletedAt = DateTime.UtcNow.AddMinutes(-2),
            StartedByUserId = user.Id,
            TriggeredBy = "manual",
            ErrorMessage = "step boom",
        };
        db.WorkflowExecutions.Add(exec);

        var s1 = new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "step-1",
            StepName = "First", StepType = "log", Status = ExecutionStatus.Succeeded,
            StartedAt = exec.StartedAt.AddSeconds(1),
        };
        var s2 = new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "step-2",
            StepName = "Bad One", StepType = "runScript", Status = ExecutionStatus.Failed,
            StartedAt = exec.StartedAt.AddSeconds(2),
        };
        var s3 = new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "step-3",
            StepName = "Down", StepType = "log", Status = ExecutionStatus.Skipped,
        };
        db.StepExecutions.AddRange(s1, s2, s3);
        await db.SaveChangesAsync();

        var controller = NewController(db, new Mock<IWorkflowEngine>().Object);

        var result = await controller.GetAll(workflow.Id, activeOnly: false, terminalOnly: false, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var executions = ok.Value.Should().BeAssignableTo<List<ExecutionResponse>>().Subject;
        var row = executions.Should().ContainSingle().Subject;
        row.StartedByUsername.Should().Be("alice");
        row.StepsTotal.Should().Be(3);
        row.StepsCompleted.Should().Be(2, "Skipped wird abgezogen — der Engine hat 2 Steps tatsächlich angefasst");
        row.FailedSteps.Should().NotBeNull().And.ContainSingle();
        row.FailedSteps![0].StepId.Should().Be("step-2");
        row.FailedSteps[0].StepName.Should().Be("Bad One");
        row.ParentExecutionId.Should().BeNull();
        row.ParentWorkflowName.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_MultipleFailedSteps_ReturnsAllInChronologicalOrder()
    {
        // Parallel branches can fail at the same time — the grid should show all failed
        // steps, not just the first one. Two failed steps with different StartedAt values
        // exercise the full path.
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-2),
        };
        db.WorkflowExecutions.Add(exec);

        var early = new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "branch-a",
            StepName = "Send Email", StepType = "emailNotification", Status = ExecutionStatus.Failed,
            StartedAt = exec.StartedAt.AddSeconds(1),
        };
        var late = new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "branch-b",
            StepName = "Update DB", StepType = "sql", Status = ExecutionStatus.Failed,
            StartedAt = exec.StartedAt.AddSeconds(3),
        };
        // A Succeeded step in between — must NOT show up in FailedSteps.
        var ok = new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "ok",
            StepName = "Health Check", StepType = "log", Status = ExecutionStatus.Succeeded,
            StartedAt = exec.StartedAt.AddSeconds(2),
        };
        db.StepExecutions.AddRange(early, late, ok);
        await db.SaveChangesAsync();

        var controller = NewController(db, new Mock<IWorkflowEngine>().Object);

        var result = await controller.GetAll(workflow.Id, activeOnly: false, terminalOnly: false, CancellationToken.None);

        var ok200 = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var row = ok200.Value.Should().BeAssignableTo<List<ExecutionResponse>>().Subject.Single();
        row.FailedSteps.Should().NotBeNull().And.HaveCount(2);
        row.FailedSteps![0].StepId.Should().Be("branch-a", "der frühere Failed-Step kommt zuerst");
        row.FailedSteps[0].StepName.Should().Be("Send Email");
        row.FailedSteps[1].StepId.Should().Be("branch-b");
        row.FailedSteps[1].StepName.Should().Be("Update DB");
    }

    [Fact]
    public async Task GetAll_TriggerRunWithoutUser_LeavesUsernameNull()
    {
        // Trigger-driven runs (scheduler/webhook/file/db/eventlog) have StartedByUserId=null.
        // The grid must then show "—" in the User column — which requires the server to
        // actually return null.
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow,
            TriggeredBy = "schedule",
            // StartedByUserId stays null
        };
        db.WorkflowExecutions.Add(exec);
        await db.SaveChangesAsync();

        var controller = NewController(db, new Mock<IWorkflowEngine>().Object);

        var result = await controller.GetAll(workflow.Id, activeOnly: false, terminalOnly: false, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var executions = ok.Value.Should().BeAssignableTo<List<ExecutionResponse>>().Subject;
        executions.Should().ContainSingle().Which.StartedByUsername.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_SubWorkflowRun_ResolvesParentWorkflowName()
    {
        // A child run triggered via startWorkflow references its parent execution. GetAll
        // must resolve the parent's workflow name from that reference so the grid can show
        // the "↳ from <parentName>" badge.
        var db = CreateContext();
        var parentWf = new Workflow { Id = Guid.NewGuid(), Name = "Daily Report", DefinitionJson = "{}" };
        var childWf = new Workflow { Id = Guid.NewGuid(), Name = "Send Email", DefinitionJson = "{}" };
        db.Workflows.AddRange(parentWf, childWf);

        var parentExec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = parentWf.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        var childExec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = childWf.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-4),
            CompletedAt = DateTime.UtcNow.AddMinutes(-3),
            ParentExecutionId = parentExec.Id,
            CallDepth = 1,
        };
        db.WorkflowExecutions.AddRange(parentExec, childExec);
        await db.SaveChangesAsync();

        var controller = NewController(db, new Mock<IWorkflowEngine>().Object);

        // Filter on the child workflow, otherwise both executions come back.
        var result = await controller.GetAll(childWf.Id, activeOnly: false, terminalOnly: false, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var executions = ok.Value.Should().BeAssignableTo<List<ExecutionResponse>>().Subject;
        var row = executions.Should().ContainSingle().Subject;
        row.ParentExecutionId.Should().Be(parentExec.Id);
        row.ParentWorkflowName.Should().Be("Daily Report");
    }

    [Fact]
    public async Task GetById_SubWorkflowRun_ResolvesParentFields()
    {
        // The detail endpoint must carry the same parent link as the list endpoint so the
        // Live-Ops drilldown can render a navigable parent chip.
        var db = CreateContext();
        var parentWf = new Workflow { Id = Guid.NewGuid(), Name = "Daily Report", DefinitionJson = "{}" };
        var childWf = new Workflow { Id = Guid.NewGuid(), Name = "Send Email", DefinitionJson = "{}" };
        db.Workflows.AddRange(parentWf, childWf);

        var parentExec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = parentWf.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var childExec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = childWf.Id,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddMinutes(-4),
            ParentExecutionId = parentExec.Id,
            CallDepth = 1,
        };
        db.WorkflowExecutions.AddRange(parentExec, childExec);
        await db.SaveChangesAsync();

        var controller = NewController(db, new Mock<IWorkflowEngine>().Object);

        var childResult = await controller.GetById(childExec.Id, CancellationToken.None);
        var child = childResult.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<ExecutionResponse>().Subject;
        child.ParentExecutionId.Should().Be(parentExec.Id);
        child.ParentWorkflowName.Should().Be("Daily Report");

        var parentResult = await controller.GetById(parentExec.Id, CancellationToken.None);
        var parent = parentResult.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<ExecutionResponse>().Subject;
        parent.ParentExecutionId.Should().BeNull();
        parent.ParentWorkflowName.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_SucceededRun_HasNoFailedStep()
    {
        // Negative branch of the failed-step lookup: a Succeeded run must return neither
        // FailedStepName nor FailedStepId. Guards against a bug that fills the column with
        // the last Succeeded step instead.
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            CompletedAt = DateTime.UtcNow,
        };
        db.WorkflowExecutions.Add(exec);
        db.StepExecutions.Add(new StepExecution
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = exec.Id, StepId = "ok",
            StepType = "log", Status = ExecutionStatus.Succeeded,
            StartedAt = exec.StartedAt,
        });
        await db.SaveChangesAsync();

        var controller = NewController(db, new Mock<IWorkflowEngine>().Object);

        var result = await controller.GetAll(workflow.Id, activeOnly: false, terminalOnly: false, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var row = ok.Value.Should().BeAssignableTo<List<ExecutionResponse>>().Subject.Single();
        row.FailedSteps.Should().BeNull();
        row.StepsTotal.Should().Be(1);
        row.StepsCompleted.Should().Be(1);
    }
}

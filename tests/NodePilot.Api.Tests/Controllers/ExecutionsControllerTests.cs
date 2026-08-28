using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Cryptography;
using System.Text;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.Clients;
using NodePilot.Data;
using NodePilot.Engine.Security;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class ExecutionsControllerTests
{
    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static ExecutionDispatchService CreateDispatchService(
        NodePilotDbContext db,
        IWorkflowEngine engine,
        ExecutionDispatchSignal? dispatchSignal = null)
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
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OutputRedactor(null),
            new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ExecutionDispatchService>.Instance,
            signal: dispatchSignal);
    }

    // Shared controller factory. Sets up an Admin-claim HttpContext so IsPrivileged / Scrub
    // don't NullReference (they read User.IsInRole). Individual tests can override the
    // principal by reassigning ControllerContext.HttpContext.User afterward.
    private static ExecutionsController NewController(
        NodePilotDbContext db,
        IWorkflowEngine engine,
        ExecutionDispatchSignal? dispatchSignal = null,
        IAuditWriter? audit = null)
    {
        var controller = new ExecutionsController(
            db, engine, CreateDispatchService(db, engine, dispatchSignal), new OutputRedactor(null),
            audit ?? NoopAuditWriter.Instance,
            new AlwaysAllowAuthorizationService(),
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll);
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
        var executions = ok.Value.Should().BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject.Items;
        executions.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAll_ReturnsRequestedPageAndTrueTotal()
    {
        await using var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Paged", DefinitionJson = "{}", IsEnabled = true,
        };
        db.Workflows.Add(workflow);
        db.WorkflowExecutions.AddRange(Enumerable.Range(0, 205).Select(index => new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow.AddSeconds(-index),
        }));
        await db.SaveChangesAsync();

        var controller = NewController(db, Mock.Of<IWorkflowEngine>());

        var result = await controller.GetAll(
            workflow.Id, activeOnly: false, terminalOnly: true,
            CancellationToken.None, page: 2, pageSize: 100);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should()
            .BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject;
        response.Items.Should().HaveCount(100);
        response.Page.Should().Be(2);
        response.PageSize.Should().Be(100);
        response.Total.Should().Be(205);
        response.TotalPages.Should().Be(3);

        var extremeResult = await controller.GetAll(
            workflow.Id, activeOnly: false, terminalOnly: true,
            CancellationToken.None, page: int.MaxValue, pageSize: 200);
        var extremeOk = extremeResult.Result.Should().BeOfType<OkObjectResult>().Subject;
        var extremePage = extremeOk.Value.Should()
            .BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject;
        extremePage.Items.Should().BeEmpty("large page numbers must not overflow Skip into a negative value");
        extremePage.Total.Should().Be(205);
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
        var executions = ok.Value.Should().BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject.Items;
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
        var queue = new CountingExecutionDispatchSignal();
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
        // Interactive priority is persisted with the dispatch intent.
        queue.EnqueueCount.Should().Be(1);
        (await db.ExecutionDispatchOutbox.SingleAsync(item => item.ExecutionId == response.Id))
            .Priority.Should().Be(ExecutionDispatchPriority.Interactive);

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
    public async Task Retry_RedactedInput_Returns400WithoutDispatch()
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        var original = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow,
            InputParametersJson = """{"password":"***"}""",
        };
        db.AddRange(workflow, original);
        await db.SaveChangesAsync();
        var queue = new CountingExecutionDispatchSignal();

        var result = await NewController(db, Mock.Of<IWorkflowEngine>(), queue)
            .Retry(original.Id, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        queue.EnqueueCount.Should().Be(0);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Retry_TruncatedInput_Returns400WithoutDispatch()
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        var original = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5), CompletedAt = DateTime.UtcNow,
            InputParametersJson = "{\"payload\":\"incomplete... [truncated]",
        };
        db.AddRange(workflow, original);
        await db.SaveChangesAsync();
        var queue = new CountingExecutionDispatchSignal();

        var result = await NewController(db, Mock.Of<IWorkflowEngine>(), queue)
            .Retry(original.Id, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        queue.EnqueueCount.Should().Be(0);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
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
        db.ChangeTracker.Clear();
        var cancelled = await db.WorkflowExecutions.FindAsync(execution.Id);
        cancelled!.Status.Should().Be(ExecutionStatus.Cancelled);
        cancelled.CompletedAt.Should().NotBeNull();
        cancelled.ErrorMessage.Should().Contain("before dispatch");
    }

    [Fact]
    public async Task Cancel_ConcurrentTerminalWrite_DoesNotOverwriteSucceededState()
    {
        await using var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id,
            Status = ExecutionStatus.Pending, TriggeredBy = "manual",
        };
        db.AddRange(workflow, execution);
        await db.SaveChangesAsync();
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.CancelAsync(
                execution.Id, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await db.WorkflowExecutions
                    .Where(candidate => candidate.Id == execution.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(candidate => candidate.Status, ExecutionStatus.Succeeded)
                        .SetProperty(candidate => candidate.CompletedAt, DateTime.UtcNow));
                return false;
            });
        var controller = NewController(db, engine.Object);

        var result = await controller.Cancel(execution.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        (await db.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Succeeded);
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
    private const string OtherLongKey = "other-api-key-needs-32-bytes-ok!";
    private const string ManualApiDefinition =
        """{"nodes":[{"id":"manual","type":"activity","data":{"activityType":"manualTrigger","config":{}}}],"edges":[]}""";
    private const string DisabledManualApiDefinition =
        """{"nodes":[{"id":"manual","type":"activity","data":{"activityType":"manualTrigger","disabled":true,"config":{}}}],"edges":[]}""";

    private static readonly NullLogger<ExternalTriggerController> TriggerLogger = NullLogger<ExternalTriggerController>.Instance;

    private sealed class CountingExecutionDispatchSignal : ExecutionDispatchSignal
    {
        public int EnqueueCount { get; private set; }

        public override void Pulse()
        {
            EnqueueCount++;
            base.Pulse();
        }
    }

    private static ExternalTriggerController CreateTriggerController(
        NodePilotDbContext db,
        IWorkflowEngine engine,
        string? presentedKey,
        ExecutionDispatchSignal? dispatchSignal = null,
        IAuditWriter? audit = null,
        NodePilot.Core.Interfaces.IMaintenanceWindowEvaluator? maintenance = null,
        NodePilot.Engine.Security.OutputRedactor? redactor = null)
    {
        var controller = new ExternalTriggerController(
            db, CreateDispatchService(db, engine, dispatchSignal), audit ?? NoopAuditWriter.Instance,
            maintenance ?? NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll,
            redactor ?? new NodePilot.Engine.Security.OutputRedactor());
        var httpCtx = new DefaultHttpContext();
        if (presentedKey is not null)
            httpCtx.Request.Headers["X-Api-Key"] = presentedKey;
        controller.ControllerContext = new ControllerContext { HttpContext = httpCtx };
        return controller;
    }

    private static IConfiguration ConfigWithKey(string? key, params Guid[] allowedWorkflowIds)
    {
        var builder = new ConfigurationBuilder();
        if (key is not null)
        {
            var values = new Dictionary<string, string?> { ["ExternalTrigger:ApiKey"] = key };
            for (var i = 0; i < allowedWorkflowIds.Length; i++)
                values[$"ExternalTrigger:AllowedWorkflowIds:{i}"] = allowedWorkflowIds[i].ToString();
            builder.AddInMemoryCollection(values);
        }
        return builder.Build();
    }

    private static IConfiguration ConfigWithHashedKeys(
        params (string IntegrationId, string Key, Guid[] AllowedWorkflowIds)[] entries)
    {
        var values = new Dictionary<string, string?>();
        foreach (var entry in entries)
        {
            values[$"ExternalTrigger:Keys:{entry.IntegrationId}:KeyHash"] = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(entry.Key)));
            for (var i = 0; i < entry.AllowedWorkflowIds.Length; i++)
            {
                values[$"ExternalTrigger:Keys:{entry.IntegrationId}:AllowedWorkflowIds:{i}"] =
                    entry.AllowedWorkflowIds[i].ToString();
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static IConfiguration ConfigWithJsonOverride(
        IReadOnlyDictionary<string, string?> baseValues,
        string overrideJson)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(overrideJson));
        return new ConfigurationBuilder()
            .AddInMemoryCollection(baseValues)
            .AddJsonStream(stream)
            .Build();
    }

    private static Workflow ExternalWorkflow(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, DefinitionJson = ManualApiDefinition, IsEnabled = true,
    };

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
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Off", DefinitionJson = ManualApiDefinition, IsEnabled = false,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey);

        var result = await controller.ExternalTrigger(
            "Off", null, ConfigWithKey(LongKey, workflow.Id), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_LegacyKeyWithoutWorkflowScope_ReturnsNotFoundWithoutEnqueue()
    {
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Manual", DefinitionJson = ManualApiDefinition, IsEnabled = true,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var queue = new CountingExecutionDispatchSignal();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);

        var result = await controller.ExternalTrigger(
            workflow.Name, null, ConfigWithKey(LongKey), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        queue.EnqueueCount.Should().Be(0);
    }

    [Fact]
    public async Task ExternalTrigger_HashedKey_CanStartOnlyItsAllowedWorkflow()
    {
        var db = CreateContext();
        var allowed = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Allowed", DefinitionJson = ManualApiDefinition, IsEnabled = true,
        };
        var denied = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Denied", DefinitionJson = ManualApiDefinition, IsEnabled = true,
        };
        db.Workflows.AddRange(allowed, denied);
        await db.SaveChangesAsync();

        var config = ConfigWithHashedKeys(
            ("integration-a", LongKey, [allowed.Id]),
            ("integration-b", OtherLongKey, [denied.Id]));
        var queue = new CountingExecutionDispatchSignal();

        var deniedController = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var deniedResult = await deniedController.ExternalTrigger(
            denied.Name, null, config, TriggerLogger, CancellationToken.None);
        deniedResult.Result.Should().BeOfType<NotFoundObjectResult>();
        queue.EnqueueCount.Should().Be(0);

        var allowedController = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var allowedResult = await allowedController.ExternalTrigger(
            allowed.Name, null, config, TriggerLogger, CancellationToken.None);
        allowedResult.Result.Should().BeOfType<AcceptedResult>();
        queue.EnqueueCount.Should().Be(1);
    }

    [Fact]
    public async Task ExternalTrigger_HashedScope_HigherProviderReplacesLowerArrayAtomically()
    {
        var db = CreateContext();
        var retained = ExternalWorkflow("Retained");
        var revoked = ExternalWorkflow("Revoked");
        db.Workflows.AddRange(retained, revoked);
        await db.SaveChangesAsync();

        var encodedKeyHash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(LongKey)));
        var baseValues = new Dictionary<string, string?>
        {
            ["ExternalTrigger:Keys:ci:KeyHash"] = encodedKeyHash,
            ["ExternalTrigger:Keys:ci:AllowedWorkflowIds:0"] = retained.Id.ToString(),
            ["ExternalTrigger:Keys:ci:AllowedWorkflowIds:1"] = revoked.Id.ToString(),
        };
        var shorterOverride = $$"""
        {
          "ExternalTrigger": {
            "Keys": {
              "ci": {
                "KeyHash": "{{encodedKeyHash}}",
                "AllowedWorkflowIds": ["{{retained.Id}}"]
              }
            }
          }
        }
        """;
        var config = ConfigWithJsonOverride(baseValues, shorterOverride);
        var queue = new CountingExecutionDispatchSignal();

        var revokedController = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var revokedResult = await revokedController.ExternalTrigger(
            revoked.Name, null, config, TriggerLogger, CancellationToken.None);
        revokedResult.Result.Should().BeOfType<NotFoundObjectResult>(
            "lower-provider index 1 must not survive a one-element higher-provider list");

        var retainedController = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var retainedResult = await retainedController.ExternalTrigger(
            retained.Name, null, config, TriggerLogger, CancellationToken.None);
        retainedResult.Result.Should().BeOfType<AcceptedResult>();

        var emptyOverride = $$"""
        {
          "ExternalTrigger": {
            "Keys": {
              "ci": { "KeyHash": "{{encodedKeyHash}}", "AllowedWorkflowIds": [] }
            }
          }
        }
        """;
        var denyAllConfig = ConfigWithJsonOverride(baseValues, emptyOverride);
        var denyAllController = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var denyAllResult = await denyAllController.ExternalTrigger(
            retained.Name, null, denyAllConfig, TriggerLogger, CancellationToken.None);
        denyAllResult.Result.Should().BeOfType<NotFoundObjectResult>(
            "an explicit empty JSON array is a higher-provider deny-all tombstone");
        queue.EnqueueCount.Should().Be(1);
    }

    [Fact]
    public async Task ExternalTrigger_HashedKeys_HigherProviderEmptyMapRevokesLowerProviderKeys()
    {
        var db = CreateContext();
        var workflow = ExternalWorkflow("EmergencyRevocation");
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var baseValues = new Dictionary<string, string?>
        {
            ["ExternalTrigger:Keys:ci:KeyHash"] = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(LongKey))),
            ["ExternalTrigger:Keys:ci:AllowedWorkflowIds:0"] = workflow.Id.ToString(),
        };
        var config = ConfigWithJsonOverride(
            baseValues,
            """{ "ExternalTrigger": { "Keys": {} } }""");
        var queue = new CountingExecutionDispatchSignal();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);

        var result = await controller.ExternalTrigger(
            workflow.Name, null, config, TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>(
            "an empty higher-provider key map must tombstone every lower-provider integration");
        queue.EnqueueCount.Should().Be(0);
    }

    [Fact]
    public async Task ExternalTrigger_HashedKeys_ReplacementHashDoesNotInheritLowerProviderScope()
    {
        var db = CreateContext();
        var workflow = ExternalWorkflow("RotatedKey");
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var baseValues = new Dictionary<string, string?>
        {
            ["ExternalTrigger:Keys:ci:KeyHash"] = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(LongKey))),
            ["ExternalTrigger:Keys:ci:AllowedWorkflowIds:0"] = workflow.Id.ToString(),
        };
        var replacementHash = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(OtherLongKey)));
        var config = ConfigWithJsonOverride(
            baseValues,
            $$"""{ "ExternalTrigger": { "Keys": { "ci": { "KeyHash": "{{replacementHash}}" } } } }""");
        var queue = new CountingExecutionDispatchSignal();

        var replacementController = CreateTriggerController(
            db, Mock.Of<IWorkflowEngine>(), OtherLongKey, queue);
        var replacementResult = await replacementController.ExternalTrigger(
            workflow.Name, null, config, TriggerLogger, CancellationToken.None);
        replacementResult.Result.Should().BeOfType<NotFoundObjectResult>(
            "an omitted scope is deny-all and must not fall back to the old provider's allow-list");

        var oldController = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var oldResult = await oldController.ExternalTrigger(
            workflow.Name, null, config, TriggerLogger, CancellationToken.None);
        oldResult.Result.Should().BeOfType<UnauthorizedObjectResult>(
            "the lower-provider hash must not survive a replacement map");
        queue.EnqueueCount.Should().Be(0);
    }

    [Fact]
    public async Task ExternalTrigger_LegacyScope_HigherProviderReplacesLowerArrayAtomically()
    {
        var db = CreateContext();
        var retained = ExternalWorkflow("LegacyRetained");
        var revoked = ExternalWorkflow("LegacyRevoked");
        db.Workflows.AddRange(retained, revoked);
        await db.SaveChangesAsync();

        var baseValues = new Dictionary<string, string?>
        {
            ["ExternalTrigger:ApiKey"] = LongKey,
            ["ExternalTrigger:AllowedWorkflowIds:0"] = retained.Id.ToString(),
            ["ExternalTrigger:AllowedWorkflowIds:1"] = revoked.Id.ToString(),
        };
        var shorterOverride = $$"""
        { "ExternalTrigger": { "AllowedWorkflowIds": ["{{retained.Id}}"] } }
        """;
        var config = ConfigWithJsonOverride(baseValues, shorterOverride);
        var queue = new CountingExecutionDispatchSignal();

        var revokedController = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var revokedResult = await revokedController.ExternalTrigger(
            revoked.Name, null, config, TriggerLogger, CancellationToken.None);
        revokedResult.Result.Should().BeOfType<NotFoundObjectResult>();

        var retainedController = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var retainedResult = await retainedController.ExternalTrigger(
            retained.Name, null, config, TriggerLogger, CancellationToken.None);
        retainedResult.Result.Should().BeOfType<AcceptedResult>();

        var denyAllConfig = ConfigWithJsonOverride(
            baseValues, """{ "ExternalTrigger": { "AllowedWorkflowIds": [] } }""");
        var denyAllController = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var denyAllResult = await denyAllController.ExternalTrigger(
            retained.Name, null, denyAllConfig, TriggerLogger, CancellationToken.None);
        denyAllResult.Result.Should().BeOfType<NotFoundObjectResult>();
        queue.EnqueueCount.Should().Be(1);
    }

    [Fact]
    public async Task ExternalTrigger_DisabledManualTrigger_ReturnsNotFoundWithoutEnqueue()
    {
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "DisabledManual", DefinitionJson = DisabledManualApiDefinition, IsEnabled = true,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var queue = new CountingExecutionDispatchSignal();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        var result = await controller.ExternalTrigger(
            workflow.Name, null, ConfigWithHashedKeys(("integration-a", LongKey, [workflow.Id])),
            TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<NotFoundObjectResult>();
        queue.EnqueueCount.Should().Be(0);
    }

    [Fact]
    public async Task ExternalTrigger_MalformedWorkflowIdInMatchingKeyScope_FailsClosed()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalTrigger:Keys:broken:KeyHash"] = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(LongKey))),
            ["ExternalTrigger:Keys:broken:AllowedWorkflowIds:0"] = "not-a-guid",
        }).Build();
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey);

        var result = await controller.ExternalTrigger(
            "anything", null, config, TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_MalformedWorkflowIdInAnyConfiguredScope_FailsClosed()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalTrigger:Keys:valid:KeyHash"] = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(LongKey))),
            ["ExternalTrigger:Keys:valid:AllowedWorkflowIds:0"] = Guid.NewGuid().ToString(),
            ["ExternalTrigger:Keys:broken:KeyHash"] = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(OtherLongKey))),
            ["ExternalTrigger:Keys:broken:AllowedWorkflowIds:0"] = "not-a-guid",
        }).Build();
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey);

        var result = await controller.ExternalTrigger(
            "anything", null, config, TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_MalformedLegacyScope_FailsClosedForHashedKey()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalTrigger:Keys:valid:KeyHash"] = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(LongKey))),
            ["ExternalTrigger:Keys:valid:AllowedWorkflowIds:0"] = Guid.NewGuid().ToString(),
            ["ExternalTrigger:ApiKey"] = OtherLongKey,
            ["ExternalTrigger:AllowedWorkflowIds:0"] = "not-a-guid",
        }).Build();
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey);

        var result = await controller.ExternalTrigger(
            "anything", null, config, TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_MalformedHashInAnyConfiguredEntry_FailsClosed()
    {
        var id = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExternalTrigger:Keys:valid:KeyHash"] = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(LongKey))),
            ["ExternalTrigger:Keys:valid:AllowedWorkflowIds:0"] = id.ToString(),
            ["ExternalTrigger:Keys:broken:KeyHash"] = "not-base64",
            ["ExternalTrigger:Keys:broken:AllowedWorkflowIds:0"] = id.ToString(),
        }).Build();
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey);

        var result = await controller.ExternalTrigger(
            "anything", null, config, TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_DuplicateMatchingHashes_FailClosed()
    {
        var id = Guid.NewGuid();
        var config = ConfigWithHashedKeys(
            ("duplicate-a", LongKey, [id]),
            ("duplicate-b", LongKey, [id]));
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey);

        var result = await controller.ExternalTrigger(
            "anything", null, config, TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_SameKeyInLegacyAndHashedEntry_FailsClosed()
    {
        var id = Guid.NewGuid();
        var values = new Dictionary<string, string?>
        {
            ["ExternalTrigger:ApiKey"] = LongKey,
            ["ExternalTrigger:AllowedWorkflowIds:0"] = id.ToString(),
            ["ExternalTrigger:Keys:duplicate:KeyHash"] = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(LongKey))),
            ["ExternalTrigger:Keys:duplicate:AllowedWorkflowIds:0"] = id.ToString(),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var db = CreateContext();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey);

        var result = await controller.ExternalTrigger(
            "anything", null, config, TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task ExternalTrigger_CorrectKey_PersistsPendingExecutionAndDispatchIntent()
    {
        var db = CreateContext();
        var publisher = new User
        {
            Id = Guid.NewGuid(), Username = "publisher", PasswordHash = "hash",
            Role = UserRole.Admin, IsActive = true,
        };
        var wf = ExternalWorkflow("Enabled");
        wf.PublishedByUserId = publisher.Id;
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

        var queue = new CountingExecutionDispatchSignal();
        var controller = CreateTriggerController(db, engine.Object, presentedKey: LongKey, queue);

        var result = await controller.ExternalTrigger(
            "Enabled", null, ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<AcceptedResult>();
        queue.EnqueueCount.Should().Be(1);

        // Admission is durable; engine invocation belongs to the hosted worker.
        var pending = await db.WorkflowExecutions.SingleAsync();
        pending.Status.Should().Be(ExecutionStatus.Pending);
        (await db.ExecutionDispatchOutbox.AnyAsync(item => item.ExecutionId == pending.Id))
            .Should().BeTrue();
        engine.Invocations.Should().BeEmpty("the durable worker owns engine invocation");
    }

    [Fact]
    public async Task ExternalTrigger_BlockedByMaintenanceWindow_Returns404AndDoesNotConsumeIdempotencyKey()
    {
        var db = CreateContext();
        var wf = ExternalWorkflow("Enabled");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var queue = new CountingExecutionDispatchSignal();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue,
            maintenance: NodePilot.TestCommons.StubMaintenanceWindowEvaluator.Blocking("PatchWindow"));
        controller.HttpContext.Request.Headers["Idempotency-Key"] = "blocked-request";

        var result = await controller.ExternalTrigger(
            "Enabled", null, ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

        // Uniform 404 (anti-enumeration) + the critical invariant: the maintenance check runs
        // BEFORE the idempotency-key transaction, so a blocked fire neither persists the key nor
        // a Pending row — a legitimate retry after the window reopens then actually runs.
        result.Result.Should().BeOfType<NotFoundObjectResult>();
        queue.EnqueueCount.Should().Be(0);
        (await db.IdempotencyKeys.CountAsync()).Should().Be(0, "a blocked fire must not consume its idempotency key");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0, "a blocked fire must not create an execution row");
    }

    [Fact]
    public async Task ExternalTrigger_TooManyParameters_ReturnsBadRequestWithoutEnqueue()
    {
        // M-32: the parameter map is bound before the API key is compared and every entry is
        // copied into the execution's variable dictionary, so an unbounded map is engine work.
        var db = CreateContext();
        var wf = ExternalWorkflow("Enabled");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var queue = new CountingExecutionDispatchSignal();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue);
        var parameters = Enumerable
            .Range(0, ExternalTriggerController.MaxTriggerParameterCount + 1)
            .ToDictionary(i => $"p{i}", _ => "v");

        var result = await controller.ExternalTrigger(
            "Enabled", new ExecuteWorkflowRequest(parameters),
            ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        queue.EnqueueCount.Should().Be(0);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExternalTrigger_OversizedParameterValue_ReturnsBadRequestWithoutEnqueue()
    {
        var db = CreateContext();
        var wf = ExternalWorkflow("Enabled");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var queue = new CountingExecutionDispatchSignal();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue);
        var parameters = new Dictionary<string, string>
        {
            ["payload"] = new('x', ExternalTriggerController.MaxTriggerParameterValueLength + 1),
        };

        var result = await controller.ExternalTrigger(
            "Enabled", new ExecuteWorkflowRequest(parameters),
            ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        queue.EnqueueCount.Should().Be(0);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ExternalTrigger_ParametersWithinCaps_StillFires()
    {
        // Guards the caps against being set so tight they break ordinary runbooks.
        var db = CreateContext();
        var wf = ExternalWorkflow("Enabled");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var queue = new CountingExecutionDispatchSignal();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue);
        var parameters = new Dictionary<string, string> { ["version"] = "2.1.0", ["env"] = "prod" };

        var result = await controller.ExternalTrigger(
            "Enabled", new ExecuteWorkflowRequest(parameters),
            ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

        result.Result.Should().NotBeOfType<BadRequestObjectResult>();
        queue.EnqueueCount.Should().Be(1);
    }

    [Fact]
    public async Task ExternalTrigger_IdempotencyKey_ReplayReturnsPendingExecutionWithoutSecondEnqueue()
    {
        var db = CreateContext();
        var wf = ExternalWorkflow("Enabled");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var engine = new Mock<IWorkflowEngine>();
        var queue = new CountingExecutionDispatchSignal();

        var first = CreateTriggerController(db, engine.Object, presentedKey: LongKey, queue);
        first.HttpContext.Request.Headers["Idempotency-Key"] = "same-request";
        var firstResult = await first.ExternalTrigger(
            "Enabled", null, ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

        firstResult.Result.Should().BeOfType<AcceptedResult>();
        queue.EnqueueCount.Should().Be(1);
        (await db.IdempotencyKeys.CountAsync()).Should().Be(1);

        var second = CreateTriggerController(db, engine.Object, presentedKey: LongKey, queue);
        second.HttpContext.Request.Headers["Idempotency-Key"] = "same-request";
        var secondResult = await second.ExternalTrigger(
            "Enabled", null, ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

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
    public async Task ExternalTrigger_IdempotencyKey_RecoveredPending_AllowsFreshAttempt()
    {
        var db = CreateContext();
        var workflow = ExternalWorkflow("RecoveredPending");
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var queue = new CountingExecutionDispatchSignal();
        var config = ConfigWithKey(LongKey, workflow.Id);

        var first = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        first.HttpContext.Request.Headers["Idempotency-Key"] = "restart-retry";
        var firstResult = await first.ExternalTrigger(
            workflow.Name, null, config, TriggerLogger, CancellationToken.None);
        var original = firstResult.Result.Should().BeOfType<AcceptedResult>().Subject.Value
            .Should().BeOfType<ExecutionResponse>().Subject;

        var abandoned = (await db.WorkflowExecutions.FindAsync(original.Id))!;
        abandoned.Status = ExecutionStatus.Cancelled;
        abandoned.CancelledBy = "reconciler-pending";
        abandoned.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var replay = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        replay.HttpContext.Request.Headers["Idempotency-Key"] = "restart-retry";
        var replayResult = await replay.ExternalTrigger(
            workflow.Name, null, config, TriggerLogger, CancellationToken.None);

        var accepted = replayResult.Result.Should().BeOfType<AcceptedResult>().Subject.Value
            .Should().BeOfType<ExecutionResponse>().Subject;
        accepted.Id.Should().NotBe(original.Id);
        queue.EnqueueCount.Should().Be(2);
        (await db.IdempotencyKeys.SingleAsync()).ExecutionId.Should().Be(accepted.Id);
    }

    [Fact]
    public async Task ExternalTrigger_IdempotencyKey_IsSeparatedByAuthenticatedKeyPrincipal()
    {
        var db = CreateContext();
        var workflow = ExternalWorkflow("PrincipalScoped");
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var config = ConfigWithHashedKeys(
            ("integration-a", LongKey, [workflow.Id]),
            ("integration-b", OtherLongKey, [workflow.Id]));
        var queue = new CountingExecutionDispatchSignal();

        var firstA = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        firstA.HttpContext.Request.Headers["Idempotency-Key"] = "shared-client-token";
        var firstAResult = await firstA.ExternalTrigger(
            workflow.Name, null, config, TriggerLogger, CancellationToken.None);
        var firstAResponse = firstAResult.Result.Should().BeOfType<AcceptedResult>().Subject.Value
            .Should().BeOfType<ExecutionResponse>().Subject;

        var firstB = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), OtherLongKey, queue);
        firstB.HttpContext.Request.Headers["Idempotency-Key"] = "shared-client-token";
        var firstBResult = await firstB.ExternalTrigger(
            workflow.Name, null, config, TriggerLogger, CancellationToken.None);
        var firstBResponse = firstBResult.Result.Should().BeOfType<AcceptedResult>().Subject.Value
            .Should().BeOfType<ExecutionResponse>().Subject;

        firstBResponse.Id.Should().NotBe(firstAResponse.Id,
            "a different authenticated key principal owns a separate idempotency domain");
        queue.EnqueueCount.Should().Be(2);
        var storedKeys = await db.IdempotencyKeys.OrderBy(k => k.Key).Select(k => k.Key).ToListAsync();
        storedKeys.Should().HaveCount(2).And.OnlyHaveUniqueItems();
        storedKeys.Should().OnlyContain(key => key.StartsWith("ext:v1:", StringComparison.Ordinal));
        storedKeys.Should().NotContain("shared-client-token", "the raw header is never persisted");

        var replayA = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        replayA.HttpContext.Request.Headers["Idempotency-Key"] = "shared-client-token";
        var replayAResult = await replayA.ExternalTrigger(
            workflow.Name, null, config, TriggerLogger, CancellationToken.None);
        var replayAResponse = replayAResult.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ExecutionResponse>().Subject;
        replayAResponse.Id.Should().Be(firstAResponse.Id);
        queue.EnqueueCount.Should().Be(2);
    }

    [Fact]
    public async Task ExternalTrigger_IdempotencyPrincipal_CanonicalizesIntegrationIdCasing()
    {
        var db = CreateContext();
        var workflow = ExternalWorkflow("CanonicalPrincipal");
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var queue = new CountingExecutionDispatchSignal();

        var upperConfig = ConfigWithHashedKeys(("CI-Agent", LongKey, [workflow.Id]));
        var first = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        first.HttpContext.Request.Headers["Idempotency-Key"] = "case-stable";
        var firstResult = await first.ExternalTrigger(
            workflow.Name, null, upperConfig, TriggerLogger, CancellationToken.None);
        var firstResponse = firstResult.Result.Should().BeOfType<AcceptedResult>().Subject.Value
            .Should().BeOfType<ExecutionResponse>().Subject;

        var lowerConfig = ConfigWithHashedKeys(("ci-agent", LongKey, [workflow.Id]));
        var replay = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), LongKey, queue);
        replay.HttpContext.Request.Headers["Idempotency-Key"] = "case-stable";
        var replayResult = await replay.ExternalTrigger(
            workflow.Name, null, lowerConfig, TriggerLogger, CancellationToken.None);
        var replayResponse = replayResult.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ExecutionResponse>().Subject;

        replayResponse.Id.Should().Be(firstResponse.Id);
        queue.EnqueueCount.Should().Be(1);
    }

    [Fact]
    public void ExternalTrigger_IdempotencyStorageKey_UsesUnambiguousLengthPrefixedEncoding()
    {
        var first = ExternalTriggerController.BuildIdempotencyStorageKey("a\0b", "c");
        var second = ExternalTriggerController.BuildIdempotencyStorageKey("a", "b\0c");

        first.Should().NotBe(second);
        first.Should().StartWith("ext:v1:").And.HaveLength(71);
        second.Should().StartWith("ext:v1:").And.HaveLength(71);
    }

    [Fact]
    public async Task ExternalTrigger_Replay_RedactsSensitiveExecutionFields()
    {
        // L-7 (security audit 2026-05-15): the API-key trigger surface carries no role, so it
        // must redact ReturnData / ErrorMessage / InputParametersJson exactly like
        // ExecutionsController does for callers below Admin/Operator — otherwise step-stdout
        // tokens or webhook-body secrets leak straight back to the API-key holder.
        var db = CreateContext();
        var wf = ExternalWorkflow("Enabled");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var redactorConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:Redaction:Enabled"] = "true",
            ["Logging:Redaction:Patterns:0"] = "SECRET-[A-Z]+",
        }).Build();
        var redactor = new NodePilot.Engine.Security.OutputRedactor(redactorConfig);
        var triggerConfig = ConfigWithKey(LongKey, wf.Id);
        var queue = new CountingExecutionDispatchSignal();

        var initial = CreateTriggerController(
            db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue, redactor: redactor);
        initial.HttpContext.Request.Headers["Idempotency-Key"] = "replay-secret";
        var initialResult = await initial.ExternalTrigger(
            "Enabled", null, triggerConfig, TriggerLogger, CancellationToken.None);
        var initialExecution = initialResult.Result.Should().BeOfType<AcceptedResult>().Subject.Value
            .Should().BeOfType<ExecutionResponse>().Subject;
        var exec = (await db.WorkflowExecutions.FindAsync(initialExecution.Id))!;
        exec.Status = ExecutionStatus.Succeeded;
        exec.ReturnData = "result token=SECRET-XYZ";
        exec.ErrorMessage = "failure detail SECRET-XYZ";
        exec.InputParametersJson = "{\"pw\":\"SECRET-XYZ\"}";
        await db.SaveChangesAsync();

        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, redactor: redactor);
        controller.HttpContext.Request.Headers["Idempotency-Key"] = "replay-secret";

        var result = await controller.ExternalTrigger(
            "Enabled", null, triggerConfig, TriggerLogger, CancellationToken.None);

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
        var wf = ExternalWorkflow("Audited");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var queue = new CountingExecutionDispatchSignal();
        var controller = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue, audit);

        var result = await controller.ExternalTrigger("Audited",
            new ExecuteWorkflowRequest(new Dictionary<string, string> { ["v"] = "1" }),
            ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

        var accepted = result.Result.Should().BeOfType<AcceptedResult>().Subject;
        var response = accepted.Value.Should().BeOfType<ExecutionResponse>().Subject;

        var call = audit.Calls.Should().ContainSingle(c => c.Action == "EXTERNAL_TRIGGER_FIRED").Subject;
        call.ResourceType.Should().Be("Workflow");
        call.ResourceId.Should().Be(wf.Id);
        call.Details.Should().Contain("\"workflowName\":\"Audited\"");
        call.Details.Should().Contain("\"integrationId\":\"legacy\"");
        call.Details.Should().NotContain(LongKey);
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
        var wf = ExternalWorkflow("Enabled");
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var queue = new CountingExecutionDispatchSignal();

        var first = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue, audit);
        first.HttpContext.Request.Headers["Idempotency-Key"] = "replay-key";
        await first.ExternalTrigger(
            "Enabled", null, ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

        var second = CreateTriggerController(db, Mock.Of<IWorkflowEngine>(), presentedKey: LongKey, queue, audit);
        second.HttpContext.Request.Headers["Idempotency-Key"] = "replay-key";
        var secondResult = await second.ExternalTrigger(
            "Enabled", null, ConfigWithKey(LongKey, wf.Id), TriggerLogger, CancellationToken.None);

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
        var executions = ok.Value.Should().BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject.Items;
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
        var executions = ok.Value.Should().BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject.Items;
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
        var executions = ok.Value.Should().BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject.Items;
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
        var row = ok200.Value.Should().BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject.Items.Single();
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
        var executions = ok.Value.Should().BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject.Items;
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
        var executions = ok.Value.Should().BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject.Items;
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
        var row = ok.Value.Should().BeAssignableTo<PagedResponse<ExecutionResponse>>().Subject.Items.Single();
        row.FailedSteps.Should().BeNull();
        row.StepsTotal.Should().Be(1);
        row.StepsCompleted.Should().Be(1);
    }

    // ---- GetById step triage ---------------------------------------------------------------
    //
    // The Live-Ops drilldown fetches GetById, so the step columns have to be populated there
    // too — the list endpoint alone is not enough. Note these counts are only meaningful for a
    // TERMINAL run: Engine:DeferRunningStateWrite defaults to true, so an in-flight step has no
    // row at all and StepsTotal would read as "everything finished".

    private static StepExecution Step(Guid execId, string stepId, ExecutionStatus status,
        DateTime startedAt, string? stepName = null)
        => new()
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = execId, StepId = stepId,
            StepName = stepName, StepType = "runScript", Status = status, StartedAt = startedAt,
        };

    private async Task<(NodePilot.Data.NodePilotDbContext Db, WorkflowExecution Exec)> SeedTerminalRun(
        params (string StepId, ExecutionStatus Status, string? Name)[] steps)
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var start = DateTime.UtcNow.AddMinutes(-5);
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Failed,
            StartedAt = start, CompletedAt = DateTime.UtcNow,
        };
        db.WorkflowExecutions.Add(exec);
        for (var i = 0; i < steps.Length; i++)
            db.StepExecutions.Add(Step(exec.Id, steps[i].StepId, steps[i].Status, start.AddSeconds(i), steps[i].Name));
        await db.SaveChangesAsync();
        return (db, exec);
    }

    [Fact]
    public async Task GetById_PopulatesStepCountsAndFailedSteps()
    {
        var (db, exec) = await SeedTerminalRun(
            ("s1", ExecutionStatus.Succeeded, "Fetch"),
            ("s2", ExecutionStatus.Failed, "Check Disk"));

        var result = await NewController(db, new Mock<IWorkflowEngine>().Object)
            .GetById(exec.Id, CancellationToken.None);

        var row = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<ExecutionResponse>().Subject;
        row.StepsTotal.Should().Be(2);
        row.StepsCompleted.Should().Be(2);
        row.FailedSteps.Should().ContainSingle().Which.Should().BeEquivalentTo(new FailedStepRef("s2", "Check Disk"));
    }

    [Fact]
    public async Task GetById_StepsCompleted_ExcludesSkippedSteps()
    {
        // A Skipped step is a control-flow branch that never ran — it counts toward the total
        // but must not read as "completed".
        var (db, exec) = await SeedTerminalRun(
            ("s1", ExecutionStatus.Succeeded, "A"),
            ("s2", ExecutionStatus.Skipped, "B"),
            ("s3", ExecutionStatus.Skipped, "C"));

        var result = await NewController(db, new Mock<IWorkflowEngine>().Object)
            .GetById(exec.Id, CancellationToken.None);

        var row = result.Result.As<OkObjectResult>().Value.As<ExecutionResponse>();
        row.StepsTotal.Should().Be(3);
        row.StepsCompleted.Should().Be(1);
    }

    [Fact]
    public async Task GetById_FailedSteps_ParallelBranches_AreAllListedInStartOrder()
    {
        var (db, exec) = await SeedTerminalRun(
            ("s1", ExecutionStatus.Succeeded, "Root"),
            ("s2", ExecutionStatus.Failed, "Branch A"),
            ("s3", ExecutionStatus.Failed, "Branch B"));

        var result = await NewController(db, new Mock<IWorkflowEngine>().Object)
            .GetById(exec.Id, CancellationToken.None);

        var row = result.Result.As<OkObjectResult>().Value.As<ExecutionResponse>();
        row.FailedSteps.Should().HaveCount(2);
        row.FailedSteps!.Select(s => s.StepId).Should().ContainInOrder("s2", "s3");
    }

    [Fact]
    public async Task GetById_FailedSteps_SameStartedAt_IsDeterministicallyOrdered()
    {
        // Parallel branches can fail within the same tick; StartedAt alone is not a stable
        // sort key, so the query tie-breaks on Id. Two calls must agree.
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var at = DateTime.UtcNow.AddMinutes(-1);
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Failed,
            StartedAt = at, CompletedAt = DateTime.UtcNow,
        };
        db.WorkflowExecutions.Add(exec);
        foreach (var id in new[] { "p1", "p2", "p3" })
            db.StepExecutions.Add(Step(exec.Id, id, ExecutionStatus.Failed, at, id));
        await db.SaveChangesAsync();

        var controller = NewController(db, new Mock<IWorkflowEngine>().Object);
        var first = (await controller.GetById(exec.Id, CancellationToken.None))
            .Result.As<OkObjectResult>().Value.As<ExecutionResponse>();
        var second = (await controller.GetById(exec.Id, CancellationToken.None))
            .Result.As<OkObjectResult>().Value.As<ExecutionResponse>();

        first.FailedSteps.Should().HaveCount(3);
        first.FailedSteps!.Select(s => s.StepId).Should().Equal(second.FailedSteps!.Select(s => s.StepId));
    }

    [Fact]
    public async Task GetById_NoStepRows_ReturnsZeroCountsAndNullFailedSteps()
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "WF", DefinitionJson = "{}" };
        db.Workflows.Add(workflow);
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Status = ExecutionStatus.Pending,
            StartedAt = DateTime.UtcNow,
        };
        db.WorkflowExecutions.Add(exec);
        await db.SaveChangesAsync();

        var result = await NewController(db, new Mock<IWorkflowEngine>().Object)
            .GetById(exec.Id, CancellationToken.None);

        var row = result.Result.As<OkObjectResult>().Value.As<ExecutionResponse>();
        row.StepsTotal.Should().Be(0);
        row.StepsCompleted.Should().Be(0);
        row.FailedSteps.Should().BeNull();
    }

    [Fact]
    public async Task GetById_FailedStepWithoutLabel_KeepsNullNameForClientFallback()
    {
        var (db, exec) = await SeedTerminalRun(("s1", ExecutionStatus.Failed, null));

        var result = await NewController(db, new Mock<IWorkflowEngine>().Object)
            .GetById(exec.Id, CancellationToken.None);

        var row = result.Result.As<OkObjectResult>().Value.As<ExecutionResponse>();
        var failed = row.FailedSteps.Should().ContainSingle().Subject;
        failed.StepId.Should().Be("s1");
        failed.StepName.Should().BeNull();
    }
}

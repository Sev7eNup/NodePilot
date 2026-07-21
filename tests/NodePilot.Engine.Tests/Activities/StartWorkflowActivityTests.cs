using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Activities;
using Xunit;

namespace NodePilot.Engine.Tests.Activities;

/// <summary>
/// Extra coverage for <see cref="StartWorkflowActivity"/> not provided by the basic
/// validation tests in <c>ControlFlowActivityTests.cs</c>. We exercise the two end-state
/// paths (wait-for-completion vs fire-and-forget) and the reserved-prefix guard against
/// user parameters trying to hijack the engine's <c>__callDepth</c> bookkeeping.
///
/// The activity opens a fresh DI scope and resolves <see cref="IWorkflowEngine"/> from
/// it, so we wire that scope to a Moq'd engine and verify call shape + observable
/// outputs.
/// </summary>
public sealed class StartWorkflowActivityWaitModeTests : IDisposable
{
    private sealed class ImmediateExecutionDispatchQueue : IExecutionDispatchQueue
    {
        public async ValueTask EnqueueAsync(
            Func<CancellationToken, Task> workItem,
            CancellationToken ct,
            ExecutionDispatchPriority priority = ExecutionDispatchPriority.Normal)
            => await workItem(ct);
    }

    private readonly SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly ServiceProvider _scopeServices;
    private readonly Mock<IWorkflowEngine> _engineMock;

    public StartWorkflowActivityWaitModeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _engineMock = new Mock<IWorkflowEngine>();

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_connection));
        services.AddSingleton(_engineMock.Object);
        _scopeServices = services.BuildServiceProvider();

        _db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _scopeServices.Dispose();
        _connection.Dispose();
    }

    private StartWorkflowActivity CreateActivity(IExecutionDispatchQueue? dispatchQueue = null) =>
        dispatchQueue is null
            ? new StartWorkflowActivity(_scopeServices.GetRequiredService<IServiceScopeFactory>(), _db, new InMemorySubWorkflowGate())
            : new StartWorkflowActivity(_scopeServices.GetRequiredService<IServiceScopeFactory>(), _db, new InMemorySubWorkflowGate(), dispatchQueue);

    private async Task<Workflow> InsertWorkflowAsync(string name)
    {
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = name,
            DefinitionJson = "{\"nodes\":[],\"edges\":[]}",
            IsEnabled = true,
            Version = 1,
        };
        _db.Workflows.Add(wf);
        await _db.SaveChangesAsync();
        return wf;
    }

    private async Task<WorkflowExecution> InsertExecutionAsync(Guid workflowId)
    {
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
        };
        _db.WorkflowExecutions.Add(exec);
        await _db.SaveChangesAsync();
        return exec;
    }

    private static StepExecutionContext MakeContext(Guid executionId) => new()
    {
        WorkflowExecutionId = executionId,
        StepId = "parent-step",
    };

    private static JsonElement Cfg(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task ExecuteAsync_RejectsUserParameter_WithReservedDoubleUnderscorePrefix()
    {
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        await InsertWorkflowAsync("child");

        var result = await CreateActivity().ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"child","parameters":{"__callDepth":"99"}}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("__callDepth");
        result.ErrorOutput.Should().Contain("reserved");

        // Engine must not have been invoked when the guard rejects up-front.
        _engineMock.Verify(e => e.ExecuteAsync(It.IsAny<Workflow>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>(), It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<Guid?>(),
            It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ChildNameDifferentCase_ResolvesCaseInsensitively()
    {
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        var child = await InsertWorkflowAsync("Child-Job");
        child.IsEnabled = false; // resolution happens before the enabled check → "disabled"
        await _db.SaveChangesAsync(); // proves the CI lookup FOUND it (vs "not found")

        var result = await CreateActivity().ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"child-job"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("disabled", "the case-insensitive lookup must find 'Child-Job'");
        result.ErrorOutput.Should().NotContain("not found");
    }

    [Fact]
    public async Task ExecuteAsync_AmbiguousChildName_FailsWithDisambiguationHint()
    {
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        await InsertWorkflowAsync("Daily");
        await InsertWorkflowAsync("DAILY");

        var result = await CreateActivity().ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"daily"}"""),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("multiple workflows").And.Contain("GUID");
    }

    [Fact]
    public async Task ExecuteAsync_ExactCaseChildName_BeatsCaseInsensitiveDuplicate()
    {
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        var exact = await InsertWorkflowAsync("Daily");
        exact.IsEnabled = false;
        await InsertWorkflowAsync("DAILY");
        await _db.SaveChangesAsync();

        var result = await CreateActivity().ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"Daily"}"""),
            CancellationToken.None);

        // The exact-case row ("Daily", disabled) must win over the CI duplicate — no ambiguity error.
        result.Success.Should().BeFalse();
        result.ErrorOutput.Should().Contain("'Daily' is disabled");
    }

    [Fact]
    public async Task ExecuteAsync_WaitForCompletion_SurfacesChildReturnDataAndMetadataParams()
    {
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        var child = await InsertWorkflowAsync("child");

        // Pre-create the child execution row that the activity will look up after engine.ExecuteAsync.
        var childExecId = Guid.NewGuid();
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = childExecId,
            WorkflowId = child.Id,
            Status = ExecutionStatus.Succeeded,
            StartedAt = DateTime.UtcNow,
            CompletedAt = DateTime.UtcNow,
            ReturnData = """{"hostName":"WIN-01","exitCode":"0"}""",
        });
        await _db.SaveChangesAsync();

        _engineMock.Setup(e => e.ExecuteAsync(
                It.Is<Workflow>(w => w.Id == child.Id),
                It.IsAny<string>(),
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
                Id = childExecId,
                WorkflowId = child.Id,
                Status = ExecutionStatus.Succeeded,
            });

        var result = await CreateActivity().ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"child","waitForCompletion":true}"""),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("hostName").WhoseValue.Should().Be("WIN-01");
        result.OutputParameters.Should().ContainKey("exitCode").WhoseValue.Should().Be("0");
        result.OutputParameters.Should().ContainKey("__executionId").WhoseValue.Should().Be(childExecId.ToString());
        result.OutputParameters.Should().ContainKey("__status").WhoseValue.Should().Be("Succeeded");
        result.OutputParameters.Should().ContainKey("__workflowId").WhoseValue.Should().Be(child.Id.ToString());
        result.OutputParameters.Should().ContainKey("__workflowName").WhoseValue.Should().Be("child");

        // Engine must have been called with callDepth=1 (parent has no manual.__callDepth seeded).
        _engineMock.Verify(e => e.ExecuteAsync(
            It.Is<Workflow>(w => w.Id == child.Id),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<Guid?>(),
            parentExec.Id,
            1,
            It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FireAndForget_ReturnsImmediatelyWithWaitedFalseAndChildMetadata()
    {
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        var child = await InsertWorkflowAsync("child");

        // The fire-and-forget path doesn't await the engine call - the return parameters tell us
        // what was scheduled. The mock can resolve immediately or never; either is fine.
        _engineMock.Setup(e => e.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = child.Id });

        var result = await CreateActivity().ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"child","waitForCompletion":false}"""),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.OutputParameters.Should().ContainKey("workflowId").WhoseValue.Should().Be(child.Id.ToString());
        result.OutputParameters.Should().ContainKey("workflowName").WhoseValue.Should().Be("child");
        result.OutputParameters.Should().ContainKey("waited").WhoseValue.Should().Be("false");
    }

    [Fact]
    public async Task ExecuteAsync_FireAndForget_WithDispatchQueue_InvokesChildExecution()
    {
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        var child = await InsertWorkflowAsync("child");
        var queue = new ImmediateExecutionDispatchQueue();

        _engineMock.Setup(e => e.ExecuteAsync(
                It.IsAny<Workflow>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<Guid?>(),
                It.IsAny<Guid?>(),
                It.IsAny<int>(),
                It.IsAny<Guid?>()))
            .ReturnsAsync(new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = child.Id });

        var result = await CreateActivity(queue).ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"child","waitForCompletion":false}"""),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        _engineMock.Verify(e => e.ExecuteAsync(
            It.Is<Workflow>(w => w.Id == child.Id),
            It.Is<string>(s => s == "startWorkflow:parent-step"),
            It.IsAny<CancellationToken>(),
            It.IsAny<Dictionary<string, string>?>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<Guid?>(),
            parentExec.Id,
            1,
            It.IsAny<Guid?>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_FireAndForget_NoExplicitTimeout_PassesNullTimeoutToChild()
    {
        // Detached children impose NO wall-clock ceiling by default (long-running work is
        // legitimate; a fire-and-forget child blocks nothing). Correctness comes from the
        // step-always-resolves + guaranteed-finalization contract, not a timeout.
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        await InsertWorkflowAsync("child");
        int? capturedTimeout = 42;
        SetupEngineCapture((_, timeout) => capturedTimeout = timeout);

        await CreateActivity(new ImmediateExecutionDispatchQueue()).ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"child","waitForCompletion":false}"""),
            CancellationToken.None);

        capturedTimeout.Should().BeNull("an unset timeoutSeconds must not impose a ceiling on a detached child");
    }

    [Fact]
    public async Task ExecuteAsync_FireAndForget_ExplicitTimeout_IsHonoredOnDetachedChild()
    {
        // Previously the detached path ignored timeoutSeconds entirely — a node author who set
        // one got no ceiling. It must now be forwarded to the child engine run.
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        await InsertWorkflowAsync("child");
        int? capturedTimeout = null;
        SetupEngineCapture((_, timeout) => capturedTimeout = timeout);

        await CreateActivity(new ImmediateExecutionDispatchQueue()).ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"child","waitForCompletion":false,"timeoutSeconds":5}"""),
            CancellationToken.None);

        capturedTimeout.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteAsync_FireAndForget_EngineThrowsOutsideFinalization_IsLoggedNotSwallowed()
    {
        // The child finalizes its own terminal state inside the engine; reaching the detached
        // catch means the engine threw OUTSIDE finalization. That must be surfaced (logged),
        // never swallowed silently — otherwise a genuine fault becomes a mystery Running row.
        var parent = await InsertWorkflowAsync("parent");
        var parentExec = await InsertExecutionAsync(parent.Id);
        await InsertWorkflowAsync("child");
        _engineMock.Setup(e => e.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>()))
            .ThrowsAsync(new InvalidOperationException("engine boom outside finalization"));

        var logger = new CapturingLogger<StartWorkflowActivity>();
        var activity = new StartWorkflowActivity(
            _scopeServices.GetRequiredService<IServiceScopeFactory>(), _db,
            new InMemorySubWorkflowGate(), new ImmediateExecutionDispatchQueue(), null, logger);

        var result = await activity.ExecuteAsync(
            MakeContext(parentExec.Id),
            Cfg("""{"workflowNameOrId":"child","waitForCompletion":false}"""),
            CancellationToken.None);

        // Parent step still reports the fire-and-forget dispatch as successful...
        result.Success.Should().BeTrue();
        // ...but the child engine fault was LOGGED, not swallowed.
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    /// <summary>Captures the <c>timeoutSeconds</c> (5th positional arg) the detached child engine run is invoked with.
    /// IWorkflowEngine.ExecuteAsync has 11 params (the trailing one is <c>interactiveRun</c>), so both the
    /// callback and the return factory must be 11-arity for Moq.</summary>
    private void SetupEngineCapture(Action<Workflow, int?> capture) =>
        _engineMock.Setup(e => e.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<bool>()))
            .Callback((Workflow wf, string _, CancellationToken _, Dictionary<string, string>? _,
                       int? timeout, bool _, Guid? _, Guid? _, int _, Guid? _, bool _) => capture(wf, timeout))
            .ReturnsAsync((Workflow wf, string _, CancellationToken _, Dictionary<string, string>? _,
                           int? _, bool _, Guid? _, Guid? _, int _, Guid? _, bool _) =>
                new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf.Id, Status = ExecutionStatus.Succeeded });

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}

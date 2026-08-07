using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests;

/// <summary>
/// Durability regressions around step state. An activity is allowed to return only after its
/// terminal row is committed; otherwise the scheduler could launch successors or finalize a green
/// workflow while the failed row still says Running.
/// </summary>
[Collection("SerialEngineTests")]
public class WorkflowEngineUnpersistedFailureTests
{
    private const string TriggerNodeJson =
        "{\"id\":\"trigger-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"manualTrigger\",\"config\":{}}}";

    /// <summary>
    /// Simulates a failure exactly at the terminal write. The Running insert is deliberately allowed
    /// through, then the first Failed update throws. A fresh context can commit after recovery.
    /// </summary>
    private sealed class FailOnceOnFailedStepSaveInterceptor : SaveChangesInterceptor
    {
        private readonly Exception _exception;
        private readonly DatabaseAvailabilityTracker? _availability;
        private int _fired;
        internal TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FailOnceOnFailedStepSaveInterceptor(
            Exception exception,
            DatabaseAvailabilityTracker? availability = null)
        {
            _exception = exception;
            _availability = availability;
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            MaybeThrow(eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            MaybeThrow(eventData);
            return ValueTask.FromResult(result);
        }

        private void MaybeThrow(DbContextEventData eventData)
        {
            if (_fired > 0) return;
            var savingFailedStep = eventData.Context?.ChangeTracker.Entries<StepExecution>()
                .Any(e => e.Entity.Status == ExecutionStatus.Failed) == true;
            if (!savingFailedStep) return;
            if (Interlocked.Exchange(ref _fired, 1) != 0) return;
            _availability?.ReportUnreachable(DatabaseOutageReason.Unreachable);
            Fired.TrySetResult();
            throw _exception;
        }
    }

    private sealed class FailOnceOnRunningStepSaveInterceptor : SaveChangesInterceptor
    {
        private readonly DatabaseAvailabilityTracker _availability;
        private readonly bool _throwAfterCommit;
        private int _fired;
        internal Guid CapturedStepExecutionId { get; private set; }
        internal TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FailOnceOnRunningStepSaveInterceptor(
            DatabaseAvailabilityTracker availability,
            bool throwAfterCommit)
        {
            _availability = availability;
            _throwAfterCommit = throwAfterCommit;
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            if (!_throwAfterCommit) MaybeThrow(eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_throwAfterCommit) MaybeThrow(eventData);
            return ValueTask.FromResult(result);
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            if (_throwAfterCommit) MaybeThrow(eventData);
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (_throwAfterCommit) MaybeThrow(eventData);
            return ValueTask.FromResult(result);
        }

        private void MaybeThrow(DbContextEventData eventData)
        {
            var runningStep = eventData.Context?.ChangeTracker.Entries<StepExecution>()
                .Select(entry => entry.Entity)
                .FirstOrDefault(step => step.Status == ExecutionStatus.Running);
            if (runningStep is null || Interlocked.Exchange(ref _fired, 1) != 0) return;

            CapturedStepExecutionId = runningStep.Id;
            _availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
            Fired.TrySetResult();
            throw new IOException(_throwAfterCommit
                ? "simulated lost acknowledgement after Running commit"
                : "simulated outage before Running commit");
        }
    }

    private sealed class FailOnceOnTerminalExecutionUpdateInterceptor : DbCommandInterceptor
    {
        private readonly DatabaseAvailabilityTracker _availability;
        private int _fired;
        internal TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FailOnceOnTerminalExecutionUpdateInterceptor(DatabaseAvailabilityTracker availability)
            => _availability = availability;

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            MaybeThrow(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            MaybeThrow(command);
            return ValueTask.FromResult(result);
        }

        private void MaybeThrow(DbCommand command)
        {
            if (!command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !command.CommandText.Contains("WorkflowExecutions", StringComparison.Ordinal)
                || !command.CommandText.Contains("CompletedAt", StringComparison.Ordinal)
                || Interlocked.Exchange(ref _fired, 1) != 0)
            {
                return;
            }

            _availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
            Fired.TrySetResult();
            throw new IOException("simulated terminal workflow database outage");
        }
    }

    /// <summary>
    /// Fails the terminal <b>verdict read</b> exactly once — the COUNT of failed steps — and nothing
    /// else. Deliberately distinct from the CAS interceptors above: the writes were already
    /// barriered, the read was not, and that asymmetry is the defect under test.
    /// </summary>
    private sealed class FailOnceOnTerminalVerdictReadInterceptor : DbCommandInterceptor
    {
        private readonly DatabaseAvailabilityTracker _availability;
        private int _fired;
        private int _verdictReads;
        internal int VerdictReads => Volatile.Read(ref _verdictReads);
        internal TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal FailOnceOnTerminalVerdictReadInterceptor(DatabaseAvailabilityTracker availability)
            => _availability = availability;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            MaybeThrow(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            MaybeThrow(command);
            return ValueTask.FromResult(result);
        }

        private void MaybeThrow(DbCommand command)
        {
            var text = command.CommandText;
            if (!text.Contains("COUNT", StringComparison.OrdinalIgnoreCase)
                || !text.Contains("StepExecutions", StringComparison.Ordinal))
            {
                return;
            }

            Interlocked.Increment(ref _verdictReads);
            if (Interlocked.Exchange(ref _fired, 1) != 0) return;

            _availability.ReportUnreachable(DatabaseOutageReason.Unreachable);
            Fired.TrySetResult();
            throw new IOException("simulated outage at the terminal verdict read");
        }
    }

    private sealed class AlwaysFailTerminalExecutionUpdateInterceptor : DbCommandInterceptor
    {
        private const string ErrorMessage = "simulated non-outage terminal CAS bug";

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            ThrowForTerminalUpdate(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowForTerminalUpdate(command);
            return ValueTask.FromResult(result);
        }

        private static void ThrowForTerminalUpdate(DbCommand command)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("WorkflowExecutions", StringComparison.Ordinal)
                && command.CommandText.Contains("CompletedAt", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ErrorMessage);
            }
        }
    }


    /// <summary>
    /// Fails every save that carries a <see cref="ExecutionStatus.Cancelled"/> step with a
    /// NON-outage error (classifies as <c>DbFailureKind.None</c>, like a deadlock victim).
    /// </summary>
    private sealed class AlwaysFailCancelledStepSaveInterceptor : SaveChangesInterceptor
    {
        private int _fired;
        internal int Fired => Volatile.Read(ref _fired);

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            MaybeThrow(eventData);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            MaybeThrow(eventData);
            return ValueTask.FromResult(result);
        }

        private void MaybeThrow(DbContextEventData eventData)
        {
            var savingCancelled = eventData.Context?.ChangeTracker.Entries<StepExecution>()
                .Any(entry => entry.Entity.Status == ExecutionStatus.Cancelled) == true;
            if (!savingCancelled) return;
            Interlocked.Increment(ref _fired);
            throw new InvalidOperationException("simulated deadlock victim on the cancelled-step write");
        }
    }

    private static IActivityExecutor MockExecutor(string type, ActivityResult result)
    {
        var m = new Mock<IActivityExecutor>();
        m.Setup(e => e.ActivityType).Returns(type);
        m.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return m.Object;
    }

    [Fact]
    public async Task ExecuteAsync_WithDeferredWritesConfigured_CommitsRunningStepBeforeActivityStarts()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var sawCommittedRunningRow = false;
        var runScript = new Mock<IActivityExecutor>();
        runScript.Setup(e => e.ActivityType).Returns("runScript");
        runScript.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .Returns<StepExecutionContext, JsonElement, CancellationToken>(async (context, _, ct) =>
            {
                await using var observer = new NodePilotDbContext(
                    new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
                sawCommittedRunningRow = await observer.StepExecutions.AsNoTracking()
                    .AnyAsync(step => step.WorkflowExecutionId == context.WorkflowExecutionId
                                   && step.StepId == context.StepId
                                   && step.Status == ExecutionStatus.Running, ct);
                return new ActivityResult { Success = true, Output = "done" };
            });

        var registry = new ActivityRegistry(
        [
            MockExecutor("manualTrigger", new ActivityResult { Success = true, Output = "{}" }),
            runScript.Object,
        ]);

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(connection));
        services.AddScoped(_ => registry);
        await using var stepScopeProvider = services.BuildServiceProvider();

        await using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Engine:DeferRunningStateWrite"] = "true",
            })
            .Build();
        var logger = new CapturingLogger<WorkflowEngine>();
        var engine = new WorkflowEngine(
            db,
            logger,
            stepScopeProvider,
            Mock.Of<IExecutionNotifier>(),
            configuration);
        _ = new WorkflowEngine(
            db,
            logger,
            stepScopeProvider,
            Mock.Of<IExecutionNotifier>(),
            configuration);

        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Running durability barrier",
            DefinitionJson = "{\"nodes\":[" + TriggerNodeJson
                + ",{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}],"
                + "\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"}]}",
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var execution = await engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        sawCommittedRunningRow.Should().BeTrue(
            "an activity must never start before its durable Running row exists");
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("DeferRunningStateWrite=true is ignored", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task Constructor_WithoutExplicitTrueDeferredWritesSetting_DoesNotWarn(
        string? configuredValue)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(connection));
        services.AddScoped(_ => new ActivityRegistry([]));
        await using var serviceProvider = services.BuildServiceProvider();
        var logger = new CapturingLogger<WorkflowEngine>();
        var settings = new Dictionary<string, string?>();
        if (configuredValue is not null)
            settings["Engine:DeferRunningStateWrite"] = configuredValue;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        _ = new WorkflowEngine(
            db,
            logger,
            serviceProvider,
            Mock.Of<IExecutionNotifier>(),
            configuration);

        logger.Entries.Should().NotContain(entry =>
            entry.Message.Contains("DeferRunningStateWrite", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DatabaseOutageAtRunningStepWrite_WaitsBeforeActivityAndRecoversStableId(
        bool commitBeforeAcknowledgementIsLost)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        availability.MarkBootComplete();
        var runningFailure = new FailOnceOnRunningStepSaveInterceptor(
            availability,
            commitBeforeAcknowledgementIsLost);
        var activityCalls = 0;
        var trigger = new Mock<IActivityExecutor>();
        trigger.Setup(executor => executor.ActivityType).Returns("manualTrigger");
        trigger.Setup(executor => executor.ExecuteAsync(
                It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref activityCalls);
                return Task.FromResult(new ActivityResult { Success = true, Output = "{}" });
            });
        var registry = new ActivityRegistry([trigger.Object]);

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts =>
            opts.UseSqlite(connection).AddInterceptors(runningFailure));
        services.AddScoped(_ => registry);
        services.AddSingleton<IDatabaseAvailability>(availability);
        await using var serviceProvider = services.BuildServiceProvider();

        await using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var engine = new WorkflowEngine(
            db,
            NullLogger<WorkflowEngine>.Instance,
            serviceProvider,
            Mock.Of<IExecutionNotifier>());
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Running persistence recovery",
            DefinitionJson = "{\"nodes\":[" + TriggerNodeJson + "],\"edges\":[]}",
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var executionTask = engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);
        await runningFailure.Fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        executionTask.IsCompleted.Should().BeFalse(
            "the Running write is a durability barrier during a confirmed outage");
        activityCalls.Should().Be(0,
            "the activity must not start before its stable step id is durable");
        await using (var outageObserver = new NodePilotDbContext(
                         new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options))
        {
            (await outageObserver.StepExecutions.AsNoTracking().CountAsync()).Should().Be(
                commitBeforeAcknowledgementIsLost ? 1 : 0);
        }

        availability.ReportProbeSucceeded();
        var execution = await executionTask.WaitAsync(TimeSpan.FromSeconds(5));

        execution.Status.Should().Be(ExecutionStatus.Succeeded);
        activityCalls.Should().Be(1);
        await using var recoveredObserver = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
        var durableStep = await recoveredObserver.StepExecutions.AsNoTracking().SingleAsync();
        durableStep.Id.Should().Be(runningFailure.CapturedStepExecutionId);
        durableStep.Status.Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_DatabaseOutageAtTerminalStepWrite_WaitsAndPersistsBeforeContinuing()
    {
        var successorCalls = 0;
        var runScript = new Mock<IActivityExecutor>();
        runScript.Setup(e => e.ActivityType).Returns("runScript");
        runScript.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .Returns<StepExecutionContext, JsonElement, CancellationToken>((context, _, _) =>
            {
                if (context.StepId == "step-2")
                {
                    Interlocked.Increment(ref successorCalls);
                    return Task.FromResult(new ActivityResult { Success = true, Output = "successor ran" });
                }

                return Task.FromResult(new ActivityResult { Success = false, ErrorOutput = "activity failed" });
            });

        var registry = new ActivityRegistry(
        [
            MockExecutor("manualTrigger", new ActivityResult { Success = true, Output = "{}" }),
            runScript.Object,
        ]);

        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        availability.MarkBootComplete();
        var terminalFailure = new FailOnceOnFailedStepSaveInterceptor(
            new IOException("simulated database transport failure"), availability);

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts =>
            opts.UseSqlite(connection).AddInterceptors(terminalFailure));
        services.AddScoped(_ => registry);
        services.AddSingleton<IDatabaseAvailability>(availability);
        await using var stepScopeProvider = services.BuildServiceProvider();

        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var engine = new WorkflowEngine(
            db,
            NullLogger<WorkflowEngine>.Instance,
            stepScopeProvider,
            Mock.Of<IExecutionNotifier>());

        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Outage during terminal write",
            DefinitionJson = "{\"nodes\":[" + TriggerNodeJson +
                ",{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}" +
                ",{\"id\":\"step-2\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}]," +
                "\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"}," +
                "{\"id\":\"next\",\"source\":\"step-1\",\"target\":\"step-2\"}]}",
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var executionTask = engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);
        await terminalFailure.Fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        executionTask.IsCompleted.Should().BeFalse(
            "the step callback is a durability barrier while the database is unavailable");
        successorCalls.Should().Be(0,
            "the scheduler must not launch a successor before the failed row is durable");

        availability.ReportProbeSucceeded();
        var execution = await executionTask.WaitAsync(TimeSpan.FromSeconds(5));

        var failedRows = await db.StepExecutions.AsNoTracking()
            .CountAsync(s => s.WorkflowExecutionId == execution.Id && s.Status == ExecutionStatus.Failed);
        failedRows.Should().Be(1, "recovery must persist the same stable step id on a fresh context");
        execution.Status.Should().Be(ExecutionStatus.Failed);
        successorCalls.Should().Be(1, "normal graph execution may resume only after recovery");
    }

    [Fact]
    public async Task ExecuteAsync_NonDatabaseTerminalPersistenceFailure_AbortsGraphAndSurfacesFailure()
    {
        var successorCalls = 0;
        var runScript = new Mock<IActivityExecutor>();
        runScript.Setup(e => e.ActivityType).Returns("runScript");
        runScript.Setup(e => e.ExecuteAsync(
                It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .Returns<StepExecutionContext, JsonElement, CancellationToken>((context, _, _) =>
            {
                if (context.StepId == "step-2")
                {
                    Interlocked.Increment(ref successorCalls);
                    return Task.FromResult(new ActivityResult { Success = true });
                }

                return Task.FromResult(new ActivityResult { Success = false, ErrorOutput = "activity failed" });
            });

        var registry = new ActivityRegistry(
        [
            MockExecutor("manualTrigger", new ActivityResult { Success = true, Output = "{}" }),
            runScript.Object,
        ]);

        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        const string persistenceError = "simulated non-database persistence bug";
        var terminalFailure = new FailOnceOnFailedStepSaveInterceptor(
            new InvalidOperationException(persistenceError));

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts =>
            opts.UseSqlite(connection).AddInterceptors(terminalFailure));
        services.AddScoped(_ => registry);
        await using var stepScopeProvider = services.BuildServiceProvider();

        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var engine = new WorkflowEngine(
            db,
            NullLogger<WorkflowEngine>.Instance,
            stepScopeProvider,
            Mock.Of<IExecutionNotifier>());
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Non-database terminal persistence failure",
            DefinitionJson = "{\"nodes\":[" + TriggerNodeJson +
                ",{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}" +
                ",{\"id\":\"step-2\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}]," +
                "\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"}," +
                "{\"id\":\"next\",\"source\":\"step-1\",\"target\":\"step-2\"}]}",
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var execution = await engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        execution.Status.Should().Be(ExecutionStatus.Failed);
        execution.ErrorMessage.Should().Be(persistenceError,
            "a non-database persistence error must escape StepRunner and remain diagnosable");
        successorCalls.Should().Be(0,
            "the scheduler must abort rather than reinterpret persistence failure as an activity result");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DatabaseOutageAtWorkflowTerminalCas_WaitsForRecoveryOrHostStop(
        bool hostStops)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        availability.MarkBootComplete();
        var terminalFailure = new FailOnceOnTerminalExecutionUpdateInterceptor(availability);
        var registry = new ActivityRegistry(
        [
            MockExecutor("manualTrigger", new ActivityResult { Success = true, Output = "{}" }),
            MockExecutor("runScript", new ActivityResult { Success = true, Output = "done" }),
        ]);

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts =>
            opts.UseSqlite(connection).AddInterceptors(terminalFailure));
        services.AddScoped(_ => registry);
        services.AddSingleton<IDatabaseAvailability>(availability);
        using var callerCts = new CancellationTokenSource();
        using var hostCts = new CancellationTokenSource();
        var hostLifetime = new Mock<IHostApplicationLifetime>();
        hostLifetime.SetupGet(lifetime => lifetime.ApplicationStopping).Returns(hostCts.Token);
        services.AddSingleton(hostLifetime.Object);
        await using var serviceProvider = services.BuildServiceProvider();

        var engineOptions = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(terminalFailure)
            .Options;
        await using var db = new NodePilotDbContext(engineOptions);
        await db.Database.EnsureCreatedAsync();
        var engine = new WorkflowEngine(
            db,
            NullLogger<WorkflowEngine>.Instance,
            serviceProvider,
            Mock.Of<IExecutionNotifier>());
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Workflow terminal durability barrier",
            DefinitionJson = "{\"nodes\":[" + TriggerNodeJson
                + ",{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}],"
                + "\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"}]}",
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var executionTask = engine.ExecuteAsync(workflow, "test-user", callerCts.Token);
        await terminalFailure.Fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        executionTask.IsCompleted.Should().BeFalse(
            "workflow completion must park while the shared breaker confirms an outage");
        await using (var observer = new NodePilotDbContext(
                         new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options))
        {
            (await observer.WorkflowExecutions.AsNoTracking().SingleAsync()).Status
                .Should().Be(ExecutionStatus.Running);
        }

        if (hostStops)
        {
            await hostCts.CancelAsync();
            var execution = await executionTask.WaitAsync(TimeSpan.FromSeconds(5));
            execution.Status.Should().Be(ExecutionStatus.Running,
                "host shutdown hands the still-running row to startup recovery");
        }
        else
        {
            await callerCts.CancelAsync();
            await Task.Delay(150);
            executionTask.IsCompleted.Should().BeFalse(
                "a caller/user cancellation must not release the terminal durability barrier");
            availability.ReportProbeSucceeded();
            var execution = await executionTask.WaitAsync(TimeSpan.FromSeconds(5));
            execution.Status.Should().Be(ExecutionStatus.Succeeded);
        }

        await using var persistedObserver = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
        (await persistedObserver.WorkflowExecutions.AsNoTracking().SingleAsync()).Status
            .Should().Be(hostStops ? ExecutionStatus.Running : ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_NonOutageWorkflowTerminalCasFailure_Propagates()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var terminalFailure = new AlwaysFailTerminalExecutionUpdateInterceptor();
        var registry = new ActivityRegistry(
        [
            MockExecutor("manualTrigger", new ActivityResult { Success = true, Output = "{}" }),
        ]);
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts =>
            opts.UseSqlite(connection).AddInterceptors(terminalFailure));
        services.AddScoped(_ => registry);
        await using var serviceProvider = services.BuildServiceProvider();
        await using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(terminalFailure)
                .Options);
        await db.Database.EnsureCreatedAsync();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Non-outage terminal CAS failure",
            DefinitionJson = "{\"nodes\":[" + TriggerNodeJson + "],\"edges\":[]}",
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();
        var engine = new WorkflowEngine(
            db,
            NullLogger<WorkflowEngine>.Instance,
            serviceProvider,
            Mock.Of<IExecutionNotifier>());

        var act = () => engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*simulated non-outage terminal CAS bug*");
    }

    [Fact]
    public async Task ExecuteAsync_DatabaseOutageAtTerminalVerdictRead_ParksInsteadOfInvertingTheVerdict()
    {
        // The regression this pins: the run's verdict is READ from the database (COUNT of Failed
        // step rows) while every write around it was already barriered. Unguarded, a connection that
        // dies right after the last step committed threw into the engine's generic catch, which
        // reliably persists Failed — turning a run in which EVERY step succeeded into a durable
        // failure. Downstream that is not cosmetic: alerting fires, and a startWorkflow parent takes
        // its failure edge on a child that fully succeeded.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var availability = new DatabaseAvailabilityTracker(
            NullLogger<DatabaseAvailabilityTracker>.Instance,
            probeSuccessesToRecover: 1);
        availability.MarkBootComplete();
        var verdictFailure = new FailOnceOnTerminalVerdictReadInterceptor(availability);
        var registry = new ActivityRegistry(
        [
            MockExecutor("manualTrigger", new ActivityResult { Success = true, Output = "{}" }),
            MockExecutor("runScript", new ActivityResult { Success = true, Output = "done" }),
        ]);

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts =>
            opts.UseSqlite(connection).AddInterceptors(verdictFailure));
        services.AddScoped(_ => registry);
        services.AddSingleton<IDatabaseAvailability>(availability);
        var hostLifetime = new Mock<IHostApplicationLifetime>();
        using var hostCts = new CancellationTokenSource();
        hostLifetime.SetupGet(lifetime => lifetime.ApplicationStopping).Returns(hostCts.Token);
        services.AddSingleton(hostLifetime.Object);
        await using var serviceProvider = services.BuildServiceProvider();

        await using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(verdictFailure)
                .Options);
        await db.Database.EnsureCreatedAsync();
        var engine = new WorkflowEngine(
            db,
            NullLogger<WorkflowEngine>.Instance,
            serviceProvider,
            Mock.Of<IExecutionNotifier>());
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Terminal verdict barrier",
            DefinitionJson = "{\"nodes\":[" + TriggerNodeJson
                + ",{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}],"
                + "\"edges\":[{\"id\":\"te\",\"source\":\"trigger-1\",\"target\":\"step-1\"}]}",
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var executionTask = engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);
        await verdictFailure.Fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        executionTask.IsCompleted.Should().BeFalse(
            "the verdict read must park on the confirmed outage instead of guessing");

        availability.ReportProbeSucceeded();
        var execution = await executionTask.WaitAsync(TimeSpan.FromSeconds(5));

        // The discriminating assertion: the verdict read was ATTEMPTED AGAIN after the outage
        // cleared. Without the barrier the first IOException escapes and there is exactly one read.
        verdictFailure.VerdictReads.Should().BeGreaterThanOrEqualTo(2,
            "the verdict must be re-read after recovery rather than guessed or abandoned");
        execution.Status.Should().Be(ExecutionStatus.Succeeded,
            "every step succeeded - an outage at the verdict read must never invert that");

        await using var observer = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options);
        (await observer.WorkflowExecutions.AsNoTracking().SingleAsync()).Status
            .Should().Be(ExecutionStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledStepWriteFailsWithNonOutageError_DoesNotFailTheWinningRun()
    {
        // The regression this pins: the losing branches of a waitAny junction land in the
        // cancellation handler, whose Cancelled write was made to propagate like the durability
        // barrier. But a Cancelled row is load-bearing for nothing — the verdict counts Failed rows
        // only — so a deadlock there (classifies as None, i.e. NOT a confirmed outage, so the
        // barrier does not park) failed the ENTIRE execution, including the branch that won the
        // junction and succeeded, and abandoned the remaining in-flight steps.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var cancelledFailure = new AlwaysFailCancelledStepSaveInterceptor();

        var fast = new Mock<IActivityExecutor>();
        fast.Setup(e => e.ActivityType).Returns("runScript");
        fast.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .Returns<StepExecutionContext, JsonElement, CancellationToken>(async (ctx, _, token) =>
            {
                await Task.Delay(ctx.StepId == "branchSlow" ? 3000 : 10, token);
                return new ActivityResult { Success = true, Output = ctx.StepId };
            });
        var junction = new Mock<IActivityExecutor>();
        junction.Setup(e => e.ActivityType).Returns("junction");
        junction.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "merged" });

        var registry = new ActivityRegistry(
        [
            MockExecutor("manualTrigger", new ActivityResult { Success = true, Output = "{}" }),
            fast.Object,
            junction.Object,
        ]);

        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts =>
            opts.UseSqlite(connection).AddInterceptors(cancelledFailure));
        services.AddScoped(_ => registry);
        await using var serviceProvider = services.BuildServiceProvider();

        await using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(cancelledFailure)
                .Options);
        await db.Database.EnsureCreatedAsync();
        var engine = new WorkflowEngine(
            db, NullLogger<WorkflowEngine>.Instance, serviceProvider, Mock.Of<IExecutionNotifier>());

        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Junction loser cancellation write",
            DefinitionJson = "{\"nodes\":[" + TriggerNodeJson
                + ",{\"id\":\"branchFast\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}"
                + ",{\"id\":\"branchSlow\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"config\":{}}}"
                + ",{\"id\":\"join\",\"type\":\"junction\",\"data\":{\"activityType\":\"junction\",\"config\":{\"mode\":\"waitAny\"}}}],"
                + "\"edges\":[{\"id\":\"t1\",\"source\":\"trigger-1\",\"target\":\"branchFast\"}"
                + ",{\"id\":\"t2\",\"source\":\"trigger-1\",\"target\":\"branchSlow\"}"
                + ",{\"id\":\"e1\",\"source\":\"branchFast\",\"target\":\"join\"}"
                + ",{\"id\":\"e2\",\"source\":\"branchSlow\",\"target\":\"join\"}]}",
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var execution = await engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        cancelledFailure.Fired.Should().BeGreaterThan(0,
            "the losing branch must actually have attempted its Cancelled write");
        execution.Status.Should().Be(ExecutionStatus.Succeeded,
            "a cosmetic Cancelled row must never fail a run whose winning branch succeeded");
    }
}

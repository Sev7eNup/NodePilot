using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Api.ExecutionDispatch;
using NodePilot.Core.Enums;
using NodePilot.Core.ExecutionDispatch;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using NodePilot.Engine.Security;
using Xunit;

namespace NodePilot.Api.Tests.ExecutionDispatch;

public class ExecutionDispatchWorkerTests
{
    [Fact]
    public async Task DurableWorker_PollsPersistedItemsAndHonorsInteractivePriority()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var cluster = new NodePilot.Engine.Cluster.SingleNodeClusterStateProvider();
        var signal = new ExecutionDispatchSignal();
        var callbacks = new ExecutionDispatchCallbackRegistry();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
        ServiceProvider? provider = null;

        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(candidate => candidate.ExecuteAsync(
                It.IsAny<Workflow>(), It.IsAny<string>(), It.IsAny<CancellationToken>(),
                It.IsAny<Dictionary<string, string>?>(), It.IsAny<int?>(), It.IsAny<bool>(),
                It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<Guid?>(),
                It.IsAny<bool>()))
            .Returns(async (Workflow workflow, string _, CancellationToken _, Dictionary<string, string>? _,
                int? _, bool _, Guid? _, Guid? _, int _, Guid? executionId, bool _) =>
            {
                order.Enqueue(executionId!.Value);
                await using var updateScope = provider!.CreateAsyncScope();
                var updateDb = updateScope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
                await ExecutionStateLifecycle.TrySetTerminalAsync(
                    updateDb.WorkflowExecutions.Where(item => item.Id == executionId.Value),
                    ExecutionStatus.Succeeded, DateTime.UtcNow, null, null, CancellationToken.None);
                if (order.Count == 2) completed.TrySetResult();
                return new WorkflowExecution
                {
                    Id = executionId.Value,
                    WorkflowId = workflow.Id,
                    Status = ExecutionStatus.Succeeded,
                };
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NodePilotDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<IWorkflowEngine>(engine.Object);
        services.AddSingleton<IClusterStateProvider>(cluster);
        services.AddSingleton<IMaintenanceWindowEvaluator>(
            NodePilot.TestCommons.StubMaintenanceWindowEvaluator.AllowAll);
        services.AddSingleton(new OutputRedactor(null));
        services.AddSingleton(signal);
        services.AddSingleton(callbacks);
        services.AddSingleton<IDatabaseAvailability>(
            NodePilot.TestCommons.TestDatabaseAvailability.Available);
        services.AddScoped<ExecutionDispatchService>();
        provider = services.BuildServiceProvider();
        await using var providerLifetime = provider;

        Guid normalId;
        Guid interactiveId;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            await db.Database.EnsureCreatedAsync();
            var workflow = new Workflow
            {
                Id = Guid.NewGuid(), Name = "Priority", DefinitionJson = "{}", IsEnabled = true,
            };
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync();
            var dispatcher = setupScope.ServiceProvider.GetRequiredService<ExecutionDispatchService>();
            normalId = (await dispatcher.DispatchAsync(
                new WorkflowDispatchIntent(workflow.Id, "manual", null),
                CancellationToken.None)).Id;
            interactiveId = (await dispatcher.DispatchAsync(
                new WorkflowDispatchIntent(workflow.Id, "manual", null,
                    Priority: ExecutionDispatchPriority.Interactive),
                CancellationToken.None)).Id;
        }

        var worker = new ExecutionDispatchWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            signal,
            Options.Create(new ExecutionDispatchOptions { WorkerCount = 1 }),
            cluster,
            NullLogger<ExecutionDispatchWorker>.Instance,
            NodePilot.TestCommons.TestDatabaseAvailability.Available);
        using var stopCts = new CancellationTokenSource();
        await worker.StartAsync(stopCts.Token);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        order.Should().ContainInOrder(interactiveId, normalId);
        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<NodePilotDbContext>();
            (await db.ExecutionDispatchOutbox.CountAsync()).Should().Be(0);
            (await db.WorkflowExecutions.CountAsync(item => item.Status == ExecutionStatus.Succeeded))
                .Should().Be(2);
        }

        await stopCts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
    }
}

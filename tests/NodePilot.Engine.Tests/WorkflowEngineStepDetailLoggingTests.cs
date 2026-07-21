using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
using Xunit;

namespace NodePilot.Engine.Tests;

/// <summary>
/// Covers the step-detail logging hook in WorkflowEngine.ExecuteStepAsync —
/// see <c>Logging:StepDetail:Enabled</c> / <c>MaxOutputChars</c>. Builds a real
/// ServiceProvider (per-step-scope needs one) so the LogStepDetail path actually runs.
/// </summary>
[Collection("SerialEngineTests")]
public sealed class WorkflowEngineStepDetailLoggingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NodePilotDbContext _db;
    private readonly ServiceProvider _serviceProvider;
    private readonly Mock<IActivityExecutor> _mockExecutor;
    private readonly Mock<ILogger<WorkflowEngine>> _logger;
    private readonly ActivityRegistry _registry;

    public WorkflowEngineStepDetailLoggingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _mockExecutor = new Mock<IActivityExecutor>();
        _mockExecutor.Setup(e => e.ActivityType).Returns("runScript");
        _logger = new Mock<ILogger<WorkflowEngine>>();

        var manualTriggerExecutor = new Mock<IActivityExecutor>();
        manualTriggerExecutor.Setup(e => e.ActivityType).Returns("manualTrigger");
        manualTriggerExecutor.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "{}" });

        _registry = new ActivityRegistry(new[] { _mockExecutor.Object, manualTriggerExecutor.Object });

        // Real DI container: per-step-scope resolution needs a working
        // IServiceScopeFactory + scoped DbContext + ActivityRegistry.
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite(_connection));
        services.AddScoped(_ => _registry);
        _serviceProvider = services.BuildServiceProvider();

        // Create a long-lived context for the engine's own writes (separate from per-step scope)
        _db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private WorkflowEngine CreateEngine(bool enabled = true, int maxChars = 10_000)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:StepDetail:Enabled"] = enabled.ToString(),
                ["Logging:StepDetail:MaxOutputChars"] = maxChars.ToString(),
            })
            .Build();
        return new WorkflowEngine(_db, _registry, _logger.Object,
            _serviceProvider, new Mock<IExecutionNotifier>().Object, config);
    }

    private static Workflow CreateWorkflow() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test",
        DefinitionJson = """{"nodes":[{"id":"trigger-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"manualTrigger","config":{}}},{"id":"step-1","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"runScript","config":{}}}],"edges":[{"id":"te","source":"trigger-1","target":"step-1"}]}""",
    };

    private int CountLogsAt(LogLevel level, string contains)
    {
        return _logger.Invocations
            .Count(i => i.Method.Name == "Log" &&
                        (LogLevel)i.Arguments[0] == level &&
                        i.Arguments[2].ToString()!.Contains(contains));
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulStep_LogsInformationWithOutput()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "payload-xyz" });

        var engine = CreateEngine(enabled: true);
        var workflow = CreateWorkflow();
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        await engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        CountLogsAt(LogLevel.Information, "payload-xyz").Should().BeGreaterThanOrEqualTo(1);
        CountLogsAt(LogLevel.Information, "succeeded").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ExecuteAsync_FailedStep_LogsWarningWithErrorAndOutput()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = false, Output = "partial", ErrorOutput = "blew up" });

        var engine = CreateEngine(enabled: true);
        var workflow = CreateWorkflow();
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        await engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        CountLogsAt(LogLevel.Warning, "blew up").Should().BeGreaterThanOrEqualTo(1);
        CountLogsAt(LogLevel.Warning, "failed").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ExecuteAsync_LargeOutput_TruncatesToMaxChars()
    {
        var huge = new string('x', 20_000);
        _mockExecutor.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = huge });

        var engine = CreateEngine(enabled: true, maxChars: 100);
        var workflow = CreateWorkflow();
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        await engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        CountLogsAt(LogLevel.Information, "[truncated]").Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ExecuteAsync_StepDetailDisabled_SkipsDetailLogging()
    {
        _mockExecutor.Setup(e => e.ExecuteAsync(It.IsAny<StepExecutionContext>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActivityResult { Success = true, Output = "payload-xyz" });

        var engine = CreateEngine(enabled: false);
        var workflow = CreateWorkflow();
        _db.Workflows.Add(workflow);
        await _db.SaveChangesAsync();

        await engine.ExecuteAsync(workflow, "test-user", CancellationToken.None);

        CountLogsAt(LogLevel.Information, "payload-xyz").Should().Be(0);
    }
}

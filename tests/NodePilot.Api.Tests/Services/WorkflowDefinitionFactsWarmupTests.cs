using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Services;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Availability;
using Xunit;

namespace NodePilot.Api.Tests.Services;

/// <summary>
/// Covers <see cref="WorkflowDefinitionFactsWarmup"/>. The point of the service is that the first
/// workflow list after a restart finds the cache already populated instead of reading and parsing
/// every definition inside one user's request.
/// </summary>
public sealed class WorkflowDefinitionFactsWarmupTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly NodePilotDbContext _db;

    public WorkflowDefinitionFactsWarmupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        _db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    private WorkflowDefinitionFactsWarmup NewWarmup(WorkflowDefinitionFactsCache cache)
    {
        var availability = new Mock<IDatabaseAvailability>();
        availability.Setup(a => a.WaitUntilServableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new WorkflowDefinitionFactsWarmup(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            cache,
            availability.Object,
            NullLogger<WorkflowDefinitionFactsWarmup>.Instance);
    }

    private void AddWorkflow(string name, string definitionJson)
        => _db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = name,
            DefinitionJson = definitionJson,
            UpdatedAt = DateTime.UtcNow,
        });

    [Fact]
    public async Task WarmAsync_LeavesNothingStale_SoTheFirstListReadsNoDefinitions()
    {
        const string definition = """
            {"nodes":[{"id":"n1","data":{"activityType":"manualTrigger","config":{}}}],"edges":[]}
            """;
        AddWorkflow("A", definition);
        AddWorkflow("B", definition);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var cache = new WorkflowDefinitionFactsCache();
        var warmed = await NewWarmup(cache).WarmAsync(TestContext.Current.CancellationToken);

        warmed.Should().Be(2);

        // The list endpoint asks exactly this question before deciding what to read.
        var revisions = await _db.Workflows.AsNoTracking()
            .Select(w => new { w.Id, w.UpdatedAt })
            .ToListAsync(TestContext.Current.CancellationToken);
        cache.StaleIds(revisions.Select(r => (r.Id, r.UpdatedAt)))
            .Should().BeEmpty("a warmed cache means the first request reads no definitions");
    }

    [Fact]
    public async Task WarmAsync_EmptyInstance_IsANoOp()
    {
        var cache = new WorkflowDefinitionFactsCache();

        (await NewWarmup(cache).WarmAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task WarmAsync_WorkflowSavedAfterWarming_BecomesStaleAgain()
    {
        AddWorkflow("A", """{"nodes":[],"edges":[]}""");
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var cache = new WorkflowDefinitionFactsCache();
        await NewWarmup(cache).WarmAsync(TestContext.Current.CancellationToken);

        var workflow = await _db.Workflows.FirstAsync(TestContext.Current.CancellationToken);
        workflow.UpdatedAt = workflow.UpdatedAt.AddMinutes(1);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        cache.StaleIds([(workflow.Id, workflow.UpdatedAt)])
            .Should().ContainSingle("the warm-up must not mask a later save");
    }
}

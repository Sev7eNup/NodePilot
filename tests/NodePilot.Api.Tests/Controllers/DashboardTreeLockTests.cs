using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// The dashboard reads folder-scoped data without holding the process-wide folder-tree lock across
/// the read. These tests pin both halves of that: an aggregation in progress does not block the
/// lock, and a folder mutation that starts during the read makes the endpoint read again with the
/// new scope instead of answering from the outdated one.
/// </summary>
public sealed class DashboardTreeLockTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly NodePilotDbContext _db;
    private readonly DashboardAggregateCache _cache;
    // Own instance, so tests elsewhere that hold or mutate the process-wide tree cannot interfere.
    private readonly FolderTreeMutationLock _tree = new();

    public DashboardTreeLockTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        _db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _cache = new DashboardAggregateCache(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    private DashboardController NewController(Func<Task<AccessibleFolderSet>> scope, string? role = null)
    {
        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(a => a.GetAccessibleFolderIdsAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .Returns(scope);
        var principal = new ClaimsPrincipal(role is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "TestAuth"));
        return new DashboardController(_db, authz.Object, aggregates: _cache, folderTree: _tree)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
        };
    }

    /// <summary>Starts a computation for <paramref name="key"/> that runs until released.</summary>
    private (Task Prime, Task Started, TaskCompletionSource Release) Block<T>(string key, T value)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prime = _cache.PrimeAsync(key, TimeSpan.FromMinutes(1), async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return value;
        }, CancellationToken.None);
        return (prime, started.Task, release);
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("failure-causes")]
    [InlineData("duration-trend")]
    public async Task Endpoint_WhileItsAggregateComputes_DoesNotHoldTheTreeLock(string endpoint)
    {
        var unrestricted = AccessibleFolderSet.Unrestricted;
        var (prime, started, release) = endpoint switch
        {
            "dashboard" => Block(DashboardAggregateCache.Key("window", unrestricted, 24),
                new DashboardWindowAggregates(0, DateTime.UtcNow.AddHours(-24), [], new ExecutionRetryStats(0, 0))),
            "failure-causes" => Block(DashboardAggregateCache.Key("failure-causes", unrestricted, 24),
                new FailureCausesResponse(0, [], 0)),
            _ => Block(DashboardAggregateCache.Key("duration-trend:all", unrestricted, 24),
                new DurationTrendResponse([], [])),
        };
        await started.WaitAsync(Wait);
        var scopeResolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = NewController(() =>
        {
            scopeResolved.TrySetResult();
            return Task.FromResult(unrestricted);
        });

        Task request = endpoint switch
        {
            "dashboard" => controller.Get(CancellationToken.None),
            "failure-causes" => controller.GetFailureCauses(CancellationToken.None),
            _ => controller.GetDurationTrend(CancellationToken.None),
        };
        await scopeResolved.Task.WaitAsync(Wait);

        using (await _tree.AcquireAsync(CancellationToken.None).WaitAsync(Wait)) { }
        request.IsCompleted.Should().BeFalse("the aggregate is still being computed");

        release.TrySetResult();
        await request.WaitAsync(Wait);
        await prime.WaitAsync(Wait);
    }

    [Fact]
    public async Task Get_GlobalAdmin_DoesNotWaitForTheTreeLock_AScopedCallerDoes()
    {
        // The unrestricted scope and its data do not depend on folder placement, so an Admin never
        // queues behind a folder mutation, a live-event batch or a hub join holding the tree.
        var scoped = new AccessibleFolderSet { FolderIds = [Guid.NewGuid()] };
        Task scopedRequest;
        using (await _tree.AcquireAsync(CancellationToken.None))
        {
            var admin = NewController(() => Task.FromResult(AccessibleFolderSet.Unrestricted), "Admin");
            await admin.Get(CancellationToken.None).WaitAsync(Wait);

            scopedRequest = NewController(() => Task.FromResult(scoped), "Viewer").Get(CancellationToken.None);
            scopedRequest.IsCompleted.Should().BeFalse("a folder-scoped caller resolves its scope under the lock");
        }

        await scopedRequest.WaitAsync(Wait);
    }

    [Fact]
    public async Task GetFailureCauses_FolderMutationDuringTheRead_AnswersFromTheNewScope()
    {
        var oldScope = new AccessibleFolderSet { FolderIds = [Guid.NewGuid()] };
        var newScope = new AccessibleFolderSet { FolderIds = [Guid.NewGuid()] };
        var (prime, started, release) = Block(
            DashboardAggregateCache.Key("failure-causes", oldScope, 1), new FailureCausesResponse(1, [], 1));
        await started.WaitAsync(Wait);
        var calls = 0;
        var firstScopeResolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = NewController(() =>
        {
            if (Interlocked.Increment(ref calls) > 1) return Task.FromResult(newScope);
            firstScopeResolved.TrySetResult();
            return Task.FromResult(oldScope);
        });

        var request = controller.GetFailureCauses(CancellationToken.None, 1);
        await firstScopeResolved.Task.WaitAsync(Wait);
        // Waits until the request has left the lock, so the mutation starts after its snapshot.
        using (await _tree.BeginMutationAsync(CancellationToken.None).WaitAsync(Wait)) { }
        release.TrySetResult();

        var response = (await request.WaitAsync(Wait)).Result.As<OkObjectResult>().Value.As<FailureCausesResponse>();
        response.TotalFailed.Should().Be(0, "the answer read under the outdated scope must be discarded");
        calls.Should().Be(2);
        await prime.WaitAsync(Wait);
    }
}

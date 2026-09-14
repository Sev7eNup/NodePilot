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
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// The dashboard caches its window aggregates across callers. These tests pin the boundary that
/// makes that safe: only folder-scoped, role-independent values are shared, while the Admin-only
/// audit feed and the live counters are recomputed per request.
///
/// <para>Caching the whole response instead would hand one caller's Admin section to the next
/// caller, which is what these tests exist to prevent.</para>
/// </summary>
public sealed class DashboardCacheIsolationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly NodePilotDbContext _db;
    private readonly DashboardAggregateCache _cache;

    public DashboardCacheIsolationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<NodePilotDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        _db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _cache = new DashboardAggregateCache(_provider.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _provider.Dispose();
        _connection.Dispose();
    }

    private DashboardController NewController(string role)
    {
        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(a => a.GetAccessibleFolderIdsAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccessibleFolderSet.Unrestricted);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)], "TestAuth"));

        return new DashboardController(_db, authz.Object, aggregates: _cache)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
    }

    private static async Task<DashboardStats> ReadAsync(DashboardController controller)
        => (await controller.Get(TestContext.Current.CancellationToken))
            .Result.As<OkObjectResult>().Value.As<DashboardStats>();

    [Fact]
    public async Task Get_AfterAnAdminPopulatedTheCache_ViewerStillGetsNoAuditFeed()
    {
        _db.AuditLog.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Action = "WORKFLOW_PUBLISHED",
            ResourceType = "Workflow",
            ResourceId = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Same folder scope, same window — so both callers hit the same aggregate cache entry.
        // The audit feed must still follow the caller's role, not the cached entry.
        var asAdmin = await ReadAsync(NewController("Admin"));
        var asViewer = await ReadAsync(NewController("Viewer"));

        asAdmin.RecentAudit.Should().NotBeNullOrEmpty("an Admin sees the audit feed");
        asViewer.RecentAudit.Should().BeNull("the audit feed is Admin-only and is never cached");
    }

    [Fact]
    public async Task GetFailureCauses_IsScopedByFolderPermissions_NotSharedAcrossScopes()
    {
        var workflowId = Guid.NewGuid();
        _db.Workflows.Add(new Workflow
        {
            Id = workflowId, Name = "W", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow,
        });
        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = NodePilot.Core.Enums.ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow,
            ErrorMessage = "it broke",
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var unrestricted = await ReadFailureCausesAsync(AccessibleFolderSet.Unrestricted);
        // A caller who can read no folder at all must not be served the unrestricted result that
        // is already sitting in the cache for the same window.
        var noAccess = await ReadFailureCausesAsync(AccessibleFolderSet.None);

        unrestricted.TotalFailed.Should().Be(1);
        noAccess.TotalFailed.Should().Be(0);
    }

    private async Task<FailureCausesResponse> ReadFailureCausesAsync(AccessibleFolderSet accessible)
    {
        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(a => a.GetAccessibleFolderIdsAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(accessible);
        var controller = new DashboardController(_db, authz.Object, aggregates: _cache)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.GetFailureCauses(TestContext.Current.CancellationToken, 720);
        return result.Result.As<OkObjectResult>().Value.As<FailureCausesResponse>();
    }

    [Fact]
    public async Task Get_LiveQueueCounters_AreNotServedFromTheCache()
    {
        var workflowId = Guid.NewGuid();
        _db.Workflows.Add(new Workflow
        {
            Id = workflowId, Name = "W", DefinitionJson = "{}", UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var first = await ReadAsync(NewController("Admin"));
        first.RunningCount.Should().Be(0);

        _db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            Status = NodePilot.Core.Enums.ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Immediately afterwards: well inside the aggregate TTL. Queue depth is live state and
        // must reflect the new run right away.
        var second = await ReadAsync(NewController("Admin"));
        second.RunningCount.Should().Be(1);
    }
}

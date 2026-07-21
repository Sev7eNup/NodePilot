using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class OperationsControllerTests
{
    private static readonly Guid FolderA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid FolderB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static OperationsController NewController(
        NodePilot.Data.NodePilotDbContext db,
        IResourceAuthorizationService? authz = null,
        string role = "Admin")
    {
        var controller = new OperationsController(db, authz ?? new AlwaysAllowAuthorizationService());
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)], "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static async Task<OperationsGraphDto> GetGraph(OperationsController c)
    {
        var result = await c.GetGraph(CancellationToken.None);
        return result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<OperationsGraphDto>().Subject;
    }

    private static Workflow Wf(Guid id, string name, string def, Guid? folderId = null, bool enabled = true) => new()
    {
        Id = id,
        Name = name,
        DefinitionJson = def,
        IsEnabled = enabled,
        FolderId = folderId ?? SharedWorkflowFolder.RootFolderId,
        UpdatedAt = DateTime.UtcNow,
    };

    private static string CallsDef(string nameOrId) =>
        """{"nodes":[{"id":"call","type":"activity","data":{"activityType":"startWorkflow","config":{"workflowNameOrId":"__R__"}}}],"edges":[]}""".Replace("__R__", nameOrId);

    [Fact]
    public async Task GetGraph_AdminUnrestricted_ReturnsAllWorkflowsAsNodes()
    {
        var db = TestDbFactory.Create();
        db.Workflows.AddRange(
            Wf(Guid.NewGuid(), "Alpha", "{}"),
            Wf(Guid.NewGuid(), "Beta", "{}"));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Nodes.Should().HaveCount(2);
        graph.Nodes.Select(n => n.Name).Should().BeEquivalentTo("Alpha", "Beta");
    }

    [Fact]
    public async Task GetGraph_StartWorkflowRef_ResolvesEdgeBetweenNodes()
    {
        var db = TestDbFactory.Create();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        db.Workflows.AddRange(
            Wf(parent, "Parent", CallsDef(child.ToString())),
            Wf(child, "Child", "{}"));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        var edge = graph.Edges.Should().ContainSingle().Which;
        edge.Source.Should().Be(parent);
        edge.Target.Should().Be(child);
        edge.RefStatus.Should().Be("Resolved");
        edge.Kind.Should().Be("startWorkflow");
    }

    [Fact]
    public async Task GetGraph_DynamicRef_MarkedDynamic()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(Wf(Guid.NewGuid(), "Parent", CallsDef("{{manual.target}}")));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Edges.Should().ContainSingle().Which.RefStatus.Should().Be("Dynamic");
    }

    [Fact]
    public async Task GetGraph_FolderScoped_ExcludesOutOfScope_AndCrossScopeRefIsUnresolved()
    {
        var db = TestDbFactory.Create();
        db.SharedWorkflowFolders.AddRange(
            new SharedWorkflowFolder { Id = FolderA, Name = "A", Path = "/A", Depth = 1, ParentFolderId = SharedWorkflowFolder.RootFolderId },
            new SharedWorkflowFolder { Id = FolderB, Name = "B", Path = "/B", Depth = 1, ParentFolderId = SharedWorkflowFolder.RootFolderId });
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        db.Workflows.AddRange(
            Wf(parent, "Parent", CallsDef(child.ToString()), folderId: FolderA),
            Wf(child, "Child", "{}", folderId: FolderB)); // child lives in a folder the caller can't see
        await db.SaveChangesAsync();

        // Caller may only see FolderA.
        var scoped = new ScopedAuthz(new AccessibleFolderSet { IsUnrestricted = false, FolderIds = [FolderA] });
        var graph = await GetGraph(NewController(db, scoped, role: "Operator"));

        graph.Nodes.Should().ContainSingle().Which.Name.Should().Be("Parent");
        graph.Nodes[0].FolderPath.Should().Be("/A");
        var edge = graph.Edges.Should().ContainSingle().Which;
        edge.RefStatus.Should().Be("Unresolved"); // existence of out-of-scope child not leaked
        edge.Target.Should().BeNull();
    }

    [Fact]
    public async Task GetGraph_ZeroFolderAccess_ReturnsEmptyGraph()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(Wf(Guid.NewGuid(), "Hidden", "{}"));
        await db.SaveChangesAsync();

        var scoped = new ScopedAuthz(AccessibleFolderSet.None);
        var graph = await GetGraph(NewController(db, scoped, role: "Viewer"));

        graph.Nodes.Should().BeEmpty();
        graph.Edges.Should().BeEmpty();
        graph.Running.Should().BeEmpty();
        graph.Recent.Should().BeEmpty();
    }

    [Fact]
    public async Task GetGraph_RunningExecutions_ReflectedInRunningListAndNodeCount()
    {
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "Busy", "{}"));
        db.WorkflowExecutions.AddRange(
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Pending, StartedAt = DateTime.UtcNow },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Succeeded, StartedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Running.Should().HaveCount(2); // Running + Pending, not Succeeded
        graph.Nodes.Should().ContainSingle().Which.RunningCount.Should().Be(2);
    }

    [Fact]
    public async Task GetGraph_LastStatus_DerivedFromWorkflowStats()
    {
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        var t = DateTime.UtcNow;
        db.Workflows.Add(Wf(wf, "Stat", "{}"));
        db.WorkflowStats.Add(new WorkflowStats
        {
            WorkflowId = wf,
            SucceededWindow = 3,
            FailedWindow = 1,
            CancelledWindow = 0,
            LastExecutionAt = t,
            LastFailureAt = t,           // latest run was a failure
            LastSuccessAt = t.AddHours(-1),
        });
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        var node = graph.Nodes.Should().ContainSingle().Which;
        node.LastStatus.Should().Be("Failed");
        node.CallFrequency.Should().Be(4);
    }

    [Fact]
    public async Task GetGraph_Recent_ReturnsTerminalWithin30Min_ExcludesActiveAndOld()
    {
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Workflows.Add(Wf(wf, "Busy", "{}"));
        db.WorkflowExecutions.AddRange(
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Succeeded, StartedAt = now.AddMinutes(-7), CompletedAt = now.AddMinutes(-5) },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Failed, StartedAt = now.AddMinutes(-12), CompletedAt = now.AddMinutes(-10) },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Succeeded, StartedAt = now.AddHours(-3), CompletedAt = now.AddHours(-2) }, // too old
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Running, StartedAt = now.AddMinutes(-2) }); // active, no CompletedAt
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Recent.Should().HaveCount(2);
        graph.Recent.Select(r => r.Status).Should().ContainInOrder("Succeeded", "Failed"); // newest CompletedAt first
        graph.Recent.Should().OnlyContain(r => r.CompletedAt > DateTime.UtcNow.AddMinutes(-31));
    }

    [Fact]
    public async Task GetGraph_SubWorkflowRuns_CarryParentExecutionId()
    {
        var db = TestDbFactory.Create();
        var parentWf = Guid.NewGuid();
        var childWf = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Workflows.AddRange(Wf(parentWf, "Parent", "{}"), Wf(childWf, "Child", "{}"));
        var parentExec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = parentWf, Status = ExecutionStatus.Running, StartedAt = now.AddMinutes(-5) };
        var childRunning = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = childWf, Status = ExecutionStatus.Running, StartedAt = now.AddMinutes(-4), ParentExecutionId = parentExec.Id, CallDepth = 1 };
        var childDone = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = childWf, Status = ExecutionStatus.Succeeded, StartedAt = now.AddMinutes(-9), CompletedAt = now.AddMinutes(-8), ParentExecutionId = parentExec.Id, CallDepth = 1 };
        db.WorkflowExecutions.AddRange(parentExec, childRunning, childDone);
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Running.Single(r => r.ExecutionId == childRunning.Id).ParentExecutionId.Should().Be(parentExec.Id);
        graph.Running.Single(r => r.ExecutionId == parentExec.Id).ParentExecutionId.Should().BeNull();
        graph.Recent.Single(r => r.ExecutionId == childDone.Id).ParentExecutionId.Should().Be(parentExec.Id);
    }

    [Fact]
    public async Task GetGraph_Recent_FolderScoped()
    {
        var db = TestDbFactory.Create();
        db.SharedWorkflowFolders.AddRange(
            new SharedWorkflowFolder { Id = FolderA, Name = "A", Path = "/A", Depth = 1, ParentFolderId = SharedWorkflowFolder.RootFolderId },
            new SharedWorkflowFolder { Id = FolderB, Name = "B", Path = "/B", Depth = 1, ParentFolderId = SharedWorkflowFolder.RootFolderId });
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Workflows.AddRange(
            Wf(visible, "Visible", "{}", folderId: FolderA),
            Wf(hidden, "Hidden", "{}", folderId: FolderB));
        db.WorkflowExecutions.AddRange(
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = visible, Status = ExecutionStatus.Succeeded, StartedAt = now.AddMinutes(-6), CompletedAt = now.AddMinutes(-4) },
            new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = hidden, Status = ExecutionStatus.Failed, StartedAt = now.AddMinutes(-6), CompletedAt = now.AddMinutes(-3) });
        await db.SaveChangesAsync();

        var scoped = new ScopedAuthz(new AccessibleFolderSet { IsUnrestricted = false, FolderIds = [FolderA] });
        var graph = await GetGraph(NewController(db, scoped, role: "Operator"));

        graph.Recent.Should().ContainSingle().Which.WorkflowId.Should().Be(visible);
    }

    [Theory]
    [InlineData("Admin", true)]
    [InlineData("Operator", true)]
    [InlineData("Viewer", false)]
    public async Task GetGraph_Capabilities_CanCancelReflectsRole(string role, bool expected)
    {
        var db = TestDbFactory.Create();
        var graph = await GetGraph(NewController(db, role: role));
        graph.Capabilities.CanCancel.Should().Be(expected);
    }

    private sealed class ScopedAuthz : IResourceAuthorizationService
    {
        private readonly AccessibleFolderSet _set;
        public ScopedAuthz(AccessibleFolderSet set) => _set = set;

        public Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(_set);

        public Task<bool> CanAccessWorkflowAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
            => Task.FromResult(_set.IsUnrestricted || _set.FolderIds.Contains(folderId));
        public Task<bool> CanAccessFolderAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
            => CanAccessWorkflowAsync(user, folderId, op, ct);
        public Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.All);
        public Task<ResourceCapabilities> GetFolderCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.All);
        public Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult<SharedFolderRole?>(SharedFolderRole.FolderViewer);
        public void InvalidateAll() { }
    }
}

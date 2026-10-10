using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Moq;
using NodePilot.Api.Hubs;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using System.Security.Claims;
using Xunit;

namespace NodePilot.Api.Tests.Hubs;

/// <summary>
/// Coverage for the static auth-map helpers used by <see cref="HubRevocationSweeper"/>.
/// The hub's instance methods (<c>JoinExecution</c> / <c>JoinWorkflow</c>) need a full
/// SignalR runtime, so they are covered by the manual E2E playbook (<c>E2ETests.md</c>)
/// instead. This file pins the auth-map contract: FindConnectionsToDrop returns the right
/// ids for revoked jtis or deactivated users, and the map never double-counts or leaks.
/// </summary>
[Collection(ExecutionHubStaticStateCollection.Name)]
public sealed class ExecutionHubTests : IDisposable
{
    public ExecutionHubTests()
    {
        ExecutionHub.ClearAuthMapForTest();
        ExecutionHub.ClearGroupsForTest();
        ExecutionHub.ClearOpsFeedForTest();
    }

    public void Dispose()
    {
        ExecutionHub.ClearAuthMapForTest();
        ExecutionHub.ClearGroupsForTest();
        ExecutionHub.ClearOpsFeedForTest();
    }

    private static HubCallerContext FakeContext() => Mock.Of<HubCallerContext>();

    [Fact]
    public async Task RefreshOperationsFeedScopes_FailedLookup_LeavesAllRestrictedFeedsDenied()
    {
        var folder = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
        var context = new Mock<HubCallerContext>();
        context.SetupGet(x => x.User).Returns(principal);
        foreach (var id in new[] { "first", "second" })
        {
            ExecutionHub.RegisterAuthForTest(id, "jti", Guid.NewGuid(), context.Object);
            ExecutionHub.RegisterOpsFeedForTest(id, false, [folder]);
        }
        ExecutionHub.RegisterOpsFeedForTest("admin", true, []);
        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(x => x.GetAccessibleFolderIdsAsync(principal, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("lookup failed"));

        var refresh = () => ExecutionHub.RefreshOperationsFeedScopesAsync(authz.Object, TestContext.Current.CancellationToken);

        await refresh.Should().ThrowAsync<InvalidOperationException>();
        ExecutionHub.GetOpsFeedConnections(folder).Should().BeEquivalentTo(["admin"]);
    }

    [Fact]
    public async Task JoinOperationsFeed_InFlightJoin_CannotRestoreScopeAfterFolderMoveRefresh()
    {
        var oldFolder = Guid.NewGuid();
        var newFolder = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([], "test"));
        var context = new Mock<HubCallerContext>();
        context.SetupGet(x => x.User).Returns(principal);
        context.SetupGet(x => x.ConnectionId).Returns("reader");
        ExecutionHub.RegisterAuthForTest("reader", "jti", Guid.NewGuid(), context.Object);
        var firstRead = new TaskCompletionSource<AccessibleFolderSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authz = new Mock<IResourceAuthorizationService>();
        authz.SetupSequence(x => x.GetAccessibleFolderIdsAsync(principal, It.IsAny<CancellationToken>()))
            .Returns(firstRead.Task)
            .ReturnsAsync(new AccessibleFolderSet { FolderIds = [newFolder] });
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var hub = new ExecutionHub(db, authz.Object) { Context = context.Object };
        var join = hub.JoinOperationsFeed();

        async Task MoveAndRefresh()
        {
            using var treeLock = await FolderTreeMutationLock.SharedWorkflowFolders.AcquireAsync(TestContext.Current.CancellationToken);
            await ExecutionHub.RefreshOperationsFeedScopesAsync(authz.Object, TestContext.Current.CancellationToken);
        }
        var move = MoveAndRefresh();
        move.IsCompleted.Should().BeFalse("the move must wait for the in-flight subscription snapshot");
        firstRead.SetResult(new AccessibleFolderSet { FolderIds = [oldFolder] });
        await Task.WhenAll(join, move);

        ExecutionHub.GetOpsFeedConnections(oldFolder).Should().BeEmpty();
        ExecutionHub.GetOpsFeedConnections(newFolder).Should().ContainSingle().Which.Should().Be("reader");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JoinWorkflowOrExecution_WaitsForFolderMoveBeforeReadingPermissions(bool executionFeed)
    {
        await using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var workflow = new NodePilot.Core.Models.Workflow { Id = Guid.NewGuid(), Name = "workflow", DefinitionJson = "{}" };
        var execution = new NodePilot.Core.Models.WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = workflow.Id };
        db.AddRange(workflow, execution);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var context = new Mock<HubCallerContext>();
        context.SetupGet(x => x.User).Returns(new ClaimsPrincipal(new ClaimsIdentity([], "test")));
        context.SetupGet(x => x.ConnectionId).Returns("reader");
        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(x => x.CanAccessWorkflowAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<Guid>(), ResourceOp.Read, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var groups = new Mock<IGroupManager>();
        groups.Setup(x => x.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var hub = new ExecutionHub(db, authz.Object) { Context = context.Object, Groups = groups.Object };
        var treeLock = await FolderTreeMutationLock.SharedWorkflowFolders.AcquireAsync(TestContext.Current.CancellationToken);
        Task<object> join;
        try
        {
            join = executionFeed ? hub.JoinExecution(execution.Id.ToString()) : hub.JoinWorkflow(workflow.Id.ToString());
            join.IsCompleted.Should().BeFalse();
            authz.Verify(x => x.CanAccessWorkflowAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<Guid>(), It.IsAny<ResourceOp>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { treeLock.Dispose(); }

        await join;
        groups.Verify(x => x.AddToGroupAsync("reader", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void RegisterGroupForTest_TracksWorkflowSubscribers()
    {
        var wfId = Guid.NewGuid();
        var execId = Guid.NewGuid();

        ExecutionHub.HasSubscribers(execId, wfId).Should().BeFalse();

        ExecutionHub.RegisterGroupForTest("conn-1", $"workflow-{wfId}");

        ExecutionHub.HasSubscribers(execId, wfId).Should().BeTrue();
    }

    [Fact]
    public void FindConnectionsToDrop_EmptyMap_ReturnsNothing()
    {
        var hits = ExecutionHub.FindConnectionsToDrop(
            new HashSet<string> { "any-jti" },
            new HashSet<Guid> { Guid.NewGuid() }).ToList();

        hits.Should().BeEmpty();
    }

    [Fact]
    public void FindConnectionsToDrop_MatchesByRevokedJti()
    {
        var jti = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        ExecutionHub.RegisterAuthForTest("conn-1", jti, userId, FakeContext());
        ExecutionHub.RegisterAuthForTest("conn-2", "other-jti", Guid.NewGuid(), FakeContext());

        var hits = ExecutionHub.FindConnectionsToDrop(
            new HashSet<string> { jti },
            new HashSet<Guid>()).ToList();

        hits.Should().BeEquivalentTo(new[] { "conn-1" });
    }

    [Fact]
    public void FindConnectionsToDrop_MatchesByDeactivatedUser()
    {
        var deactivated = Guid.NewGuid();
        ExecutionHub.RegisterAuthForTest("conn-1", "j1", deactivated, FakeContext());
        ExecutionHub.RegisterAuthForTest("conn-2", "j2", Guid.NewGuid(), FakeContext());

        var hits = ExecutionHub.FindConnectionsToDrop(
            new HashSet<string>(),
            new HashSet<Guid> { deactivated }).ToList();

        hits.Should().BeEquivalentTo(new[] { "conn-1" });
    }

    [Fact]
    public void FindConnectionsToDrop_MatchesSecurityStampMismatch()
    {
        var userId = Guid.NewGuid();
        ExecutionHub.RegisterAuthForTest(
            "stale", "j1", userId, FakeContext(), securityStamp: 4);
        ExecutionHub.RegisterAuthForTest(
            "current", "j2", userId, FakeContext(), securityStamp: 5);

        var hits = ExecutionHub.FindConnectionsToDrop(
            [], [], new Dictionary<Guid, int> { [userId] = 5 }).ToList();

        hits.Should().BeEquivalentTo("stale");
    }

    [Fact]
    public void FindConnectionsToDrop_NullJti_StillMatchesByDeactivatedUser()
    {
        // Cookie-based auth in some flows may not carry a JTI claim. The fallback path
        // is matching by user id — pin that the connection is dropped even with a null jti
        // when its owner is deactivated.
        var deactivated = Guid.NewGuid();
        ExecutionHub.RegisterAuthForTest("conn-no-jti", null, deactivated, FakeContext());

        var hits = ExecutionHub.FindConnectionsToDrop(
            new HashSet<string>(),
            new HashSet<Guid> { deactivated }).ToList();

        hits.Should().Contain("conn-no-jti");
    }

    [Fact]
    public void FindConnectionsToDrop_JtiAndUser_BothMatch_DoesNotDuplicateConnection()
    {
        // Same conn id matches both predicates — must be returned at most once so the
        // sweeper doesn't try to abort + ForgetConnection it twice (the second pass would
        // be a no-op but pollute the debug log).
        var jti = "j-1";
        var userId = Guid.NewGuid();
        ExecutionHub.RegisterAuthForTest("conn-1", jti, userId, FakeContext());

        var hits = ExecutionHub.FindConnectionsToDrop(
            new HashSet<string> { jti },
            new HashSet<Guid> { userId }).ToList();

        hits.Should().BeEquivalentTo(new[] { "conn-1" });
    }

    [Fact]
    public void TryGetContext_ReturnsRegisteredContext()
    {
        var ctx = FakeContext();
        ExecutionHub.RegisterAuthForTest("conn-1", "j", Guid.NewGuid(), ctx);

        ExecutionHub.TryGetContext("conn-1").Should().BeSameAs(ctx);
    }

    [Fact]
    public void TryGetContext_UnknownConnection_ReturnsNull()
    {
        ExecutionHub.TryGetContext("ghost-conn").Should().BeNull();
    }

    [Fact]
    public void ForgetConnection_RemovesEntry_FromBothLookups()
    {
        ExecutionHub.RegisterAuthForTest("conn-1", "j", Guid.NewGuid(), FakeContext());

        ExecutionHub.ForgetConnection("conn-1");

        ExecutionHub.TryGetContext("conn-1").Should().BeNull();
        ExecutionHub.FindConnectionsToDrop(
            new HashSet<string> { "j" },
            new HashSet<Guid>()).Should().NotContain("conn-1");
    }

    [Fact]
    public void HasOpsFeedSubscribers_FalseWhenEmpty_TrueAfterRegister()
    {
        ExecutionHub.HasOpsFeedSubscribers().Should().BeFalse();
        ExecutionHub.RegisterOpsFeedForTest("conn-1", unrestricted: true, []);
        ExecutionHub.HasOpsFeedSubscribers().Should().BeTrue();
    }

    [Fact]
    public void GetOpsFeedConnections_ReturnsOnlyScopedConnections_AndDoesNotLeakOutOfScope()
    {
        // This is the live-ops RBAC leak guard: an event for a workflow in FolderA must reach
        // only connections whose captured scope covers FolderA (plus unrestricted admins) —
        // never a connection scoped to a different folder.
        var folderA = Guid.NewGuid();
        var folderB = Guid.NewGuid();

        ExecutionHub.RegisterOpsFeedForTest("conn-A", unrestricted: false, [folderA]);
        ExecutionHub.RegisterOpsFeedForTest("conn-B", unrestricted: false, [folderB]);
        ExecutionHub.RegisterOpsFeedForTest("conn-admin", unrestricted: true, []);

        var forA = ExecutionHub.GetOpsFeedConnections(folderA);

        forA.Should().BeEquivalentTo("conn-A", "conn-admin");
        forA.Should().NotContain("conn-B");
    }

    [Fact]
    public void GetOpsFeedConnections_EmptyFeed_ReturnsEmpty()
    {
        ExecutionHub.GetOpsFeedConnections(Guid.NewGuid()).Should().BeEmpty();
    }

    [Fact]
    public void RegisterAuthForTest_OverwritesExistingEntry()
    {
        // Reconnect-after-drop path: the same connId can never legitimately be reused
        // (server generates fresh ids per accept), but the underlying ConcurrentDictionary
        // uses Set semantics — pinning that re-registration replaces, not appends.
        var ctx1 = FakeContext();
        var ctx2 = FakeContext();
        ExecutionHub.RegisterAuthForTest("conn-1", "j1", Guid.NewGuid(), ctx1);
        ExecutionHub.RegisterAuthForTest("conn-1", "j2", Guid.NewGuid(), ctx2);

        ExecutionHub.TryGetContext("conn-1").Should().BeSameAs(ctx2);
    }
}

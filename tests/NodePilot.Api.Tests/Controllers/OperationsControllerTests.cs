using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static OperationsController NewController(
        NodePilot.Data.NodePilotDbContext db,
        IResourceAuthorizationService? authz = null,
        string role = "Admin",
        IConfiguration? configuration = null)
    {
        var controller = new OperationsController(db, authz ?? new AlwaysAllowAuthorizationService(), configuration);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)], "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static async Task<OperationsGraphDto> GetGraph(OperationsController c, int windowMinutes = 20)
    {
        var result = await c.GetGraph(CancellationToken.None, windowMinutes);
        return result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<OperationsGraphDto>().Subject;
    }

    /// <summary>Seeds FolderA + FolderB — Workflow.FolderId is FK-constrained.</summary>
    private static void SeedFolders(NodePilot.Data.NodePilotDbContext db)
        => db.SharedWorkflowFolders.AddRange(
            new SharedWorkflowFolder { Id = FolderA, Name = "A", Path = "/A", Depth = 1, ParentFolderId = SharedWorkflowFolder.RootFolderId },
            new SharedWorkflowFolder { Id = FolderB, Name = "B", Path = "/B", Depth = 1, ParentFolderId = SharedWorkflowFolder.RootFolderId });

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
        SeedFolders(db);
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
        SeedFolders(db);
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

    // ---- Per-node action capabilities -------------------------------------------------------
    //
    // These replace the old snapshot-wide OpsCapabilities.CanCancel, which was derived from the
    // GLOBAL role only. Cancel/retry need folder ResourceOp.Run and disable needs Edit, so a
    // global Operator holding just folder-Viewer used to be offered buttons the endpoints then
    // 403'd. The flags are now per node and come straight from GetWorkflowCapabilitiesAsync,
    // which already ANDs the folder role with the global one.

    [Fact]
    public async Task GetGraph_GlobalAdmin_NodeCarriesRunAndEdit()
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(Wf(Guid.NewGuid(), "W", "{}"));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db, role: "Admin"));

        var node = graph.Nodes.Should().ContainSingle().Subject;
        node.CanRun.Should().BeTrue();
        node.CanEdit.Should().BeTrue();
    }

    [Fact]
    public async Task GetGraph_FolderRunOnly_AllowsRunButNotEdit()
    {
        // The exact case the old flag got wrong: may cancel, must not be offered quarantine.
        var db = TestDbFactory.Create();
        SeedFolders(db);
        db.Workflows.Add(Wf(Guid.NewGuid(), "W", "{}", folderId: FolderA));
        await db.SaveChangesAsync();

        var scoped = new ScopedAuthz(new AccessibleFolderSet { IsUnrestricted = false, FolderIds = [FolderA] })
        {
            Capabilities = new ResourceCapabilities(CanRead: true, CanRun: true, CanEdit: false, CanDelete: false, CanAdmin: false),
        };
        var graph = await GetGraph(NewController(db, scoped, role: "Operator"));

        var node = graph.Nodes.Should().ContainSingle().Subject;
        node.CanRun.Should().BeTrue();
        node.CanEdit.Should().BeFalse();
    }

    [Fact]
    public async Task GetGraph_FolderReadOnly_OffersNeitherRunNorEdit()
    {
        // A global Operator with only folder-Viewer rights. Previously CanCancel was true here
        // purely because of the global role, and every action button 403'd on click.
        var db = TestDbFactory.Create();
        SeedFolders(db);
        db.Workflows.Add(Wf(Guid.NewGuid(), "W", "{}", folderId: FolderA));
        await db.SaveChangesAsync();

        var scoped = new ScopedAuthz(new AccessibleFolderSet { IsUnrestricted = false, FolderIds = [FolderA] })
        {
            Capabilities = new ResourceCapabilities(CanRead: true, CanRun: false, CanEdit: false, CanDelete: false, CanAdmin: false),
        };
        var graph = await GetGraph(NewController(db, scoped, role: "Operator"));

        var node = graph.Nodes.Should().ContainSingle().Subject;
        node.CanRun.Should().BeFalse();
        node.CanEdit.Should().BeFalse();
    }

    [Fact]
    public async Task GetGraph_Capabilities_ResolvedOncePerDistinctFolder()
    {
        // Three workflows across two folders → two lookups, not three. Guards the dedup that
        // keeps the 5 s poll cheap on a large snapshot.
        var db = TestDbFactory.Create();
        SeedFolders(db);
        db.Workflows.AddRange(
            Wf(Guid.NewGuid(), "A1", "{}", folderId: FolderA),
            Wf(Guid.NewGuid(), "A2", "{}", folderId: FolderA),
            Wf(Guid.NewGuid(), "B1", "{}", folderId: FolderB));
        await db.SaveChangesAsync();

        var scoped = new ScopedAuthz(new AccessibleFolderSet { IsUnrestricted = false, FolderIds = [FolderA, FolderB] });
        var graph = await GetGraph(NewController(db, scoped, role: "Operator"));

        graph.Nodes.Should().HaveCount(3);
        scoped.CapabilityLookups.Should().BeEquivalentTo([FolderA, FolderB]);
    }

    // ---- Snapshot meta: the overdue threshold ------------------------------------------------
    //
    // Must match LongRunningExecutionCollector byte for byte (same key, same 600 default, same
    // Math.Max(1, …) floor). If these drift, the console highlights a run at a different moment
    // than the alerting rule fires for it — two contradicting definitions of "long-running".

    [Fact]
    public async Task GetGraph_Meta_OverdueSeconds_ComesFromAlertingConfig()
    {
        var db = TestDbFactory.Create();
        var graph = await GetGraph(NewController(db, configuration: Config(("Alerting:LongRunningSeconds", "1800"))));
        graph.Meta.OverdueSeconds.Should().Be(1800);
    }

    [Fact]
    public async Task GetGraph_Meta_OverdueSeconds_DefaultsTo600()
    {
        var db = TestDbFactory.Create();
        var graph = await GetGraph(NewController(db, configuration: Config()));
        graph.Meta.OverdueSeconds.Should().Be(600);
    }

    [Fact]
    public async Task GetGraph_Meta_OverdueSeconds_NoConfigurationWired_DefaultsTo600()
    {
        var db = TestDbFactory.Create();
        var graph = await GetGraph(NewController(db));
        graph.Meta.OverdueSeconds.Should().Be(600);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("1", 1)]
    public async Task GetGraph_Meta_OverdueSeconds_FlooredAtOne_LikeTheAlertingCollector(string configured, int expected)
    {
        var db = TestDbFactory.Create();
        var graph = await GetGraph(NewController(db, configuration: Config(("Alerting:LongRunningSeconds", configured))));
        graph.Meta.OverdueSeconds.Should().Be(expected);
    }

    [Fact]
    public async Task GetGraph_ZeroFolderAccess_StillCarriesMeta()
    {
        // The early-return path must not ship a null meta — the console reads it unconditionally.
        var db = TestDbFactory.Create();
        var scoped = new ScopedAuthz(AccessibleFolderSet.None);
        var graph = await GetGraph(NewController(db, scoped, role: "Viewer",
            configuration: Config(("Alerting:LongRunningSeconds", "900"))));

        graph.Nodes.Should().BeEmpty();
        graph.Meta.OverdueSeconds.Should().Be(900);
    }

    // ---- Window + truncation honesty ---------------------------------------------------------

    private static WorkflowExecution Settled(Guid wfId, DateTime completedAt)
        => new()
        {
            Id = Guid.NewGuid(), WorkflowId = wfId, Status = ExecutionStatus.Succeeded,
            StartedAt = completedAt.AddMinutes(-1), CompletedAt = completedAt,
        };

    [Theory]
    [InlineData(20)]
    [InlineData(60)]
    [InlineData(240)]
    public async Task GetGraph_AllowedWindow_IsEchoedInMeta(int windowMinutes)
    {
        var db = TestDbFactory.Create();
        var graph = await GetGraph(NewController(db), windowMinutes);
        graph.Meta.WindowMinutes.Should().Be(windowMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(1440)]
    [InlineData(-30)]
    public async Task GetGraph_UnsupportedWindow_ClampsToTwenty(int windowMinutes)
    {
        var db = TestDbFactory.Create();
        var graph = await GetGraph(NewController(db), windowMinutes);
        graph.Meta.WindowMinutes.Should().Be(20);
    }

    [Fact]
    public async Task GetGraph_WiderWindow_IncludesRunsOlderThanTheDefaultWindow()
    {
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        db.WorkflowExecutions.Add(Settled(wf, DateTime.UtcNow.AddMinutes(-45)));
        await db.SaveChangesAsync();

        var narrow = await GetGraph(NewController(db), 20);
        var wide = await GetGraph(NewController(db), 60);

        narrow.Recent.Should().BeEmpty();
        wide.Recent.Should().ContainSingle();
    }

    [Fact]
    public async Task GetGraph_RunningExecution_IsReturnedRegardlessOfWindow()
    {
        // The stuck-run case: a job started six hours ago must never age out of the snapshot,
        // whatever look-back the caller picked for finished runs.
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Running,
            StartedAt = DateTime.UtcNow.AddHours(-6),
        });
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db), 20);

        graph.Running.Should().ContainSingle();
    }

    [Fact]
    public async Task GetGraph_UntruncatedWindow_ReportsOldestReturnedAndNoTruncation()
    {
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        var oldest = DateTime.UtcNow.AddMinutes(-10);
        db.WorkflowExecutions.AddRange(Settled(wf, oldest), Settled(wf, DateTime.UtcNow.AddMinutes(-2)));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Meta.RecentTruncated.Should().BeFalse();
        graph.Meta.OldestReturnedCompletedAt.Should().BeCloseTo(oldest, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetGraph_NoSettledRuns_OldestReturnedIsNull()
    {
        var db = TestDbFactory.Create();
        var graph = await GetGraph(NewController(db));
        graph.Recent.Should().BeEmpty();
        graph.Meta.OldestReturnedCompletedAt.Should().BeNull();
        graph.Meta.RecentTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task GetGraph_MoreSettledRunsThanTheCap_FlagsTruncationAndCapsTheList()
    {
        // Silent trimming would punch a hole into the timeline that reads as "nothing ran".
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        var now = DateTime.UtcNow;
        for (var i = 0; i < 1005; i++)
            db.WorkflowExecutions.Add(Settled(wf, now.AddSeconds(-i)));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Recent.Should().HaveCount(1000);
        graph.Meta.RecentTruncated.Should().BeTrue();
        // The honest left edge sits INSIDE the window, right of what the caller asked for.
        graph.Meta.OldestReturnedCompletedAt.Should().NotBeNull();
        graph.Meta.OldestReturnedCompletedAt!.Value.Should().BeAfter(graph.Meta.RecentSinceUtc);
    }

    // ---- Density: the window covered where individual bars run out ---------------------------
    //
    // The defect these guard: at 1 h / 4 h a busy system blew past the raw cap after ~30 minutes,
    // so every wider window showed the same newest half hour and an empty band for the rest. The
    // window selector was decoration. Bars still cap — thousands of them cannot be re-sent every
    // poll and re-positioned every tick — but what they cannot reach now comes back counted.

    private static async Task<NodePilot.Data.NodePilotDbContext> SeedBusyWindow(
        Guid wf, int count, TimeSpan spacing, params (int Index, ExecutionStatus Status)[] outcomes)
    {
        var db = TestDbFactory.Create();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        var now = DateTime.UtcNow;
        var overrides = outcomes.ToDictionary(o => o.Index, o => o.Status);
        for (var i = 0; i < count; i++)
        {
            var run = Settled(wf, now - spacing * i);
            if (overrides.TryGetValue(i, out var status)) run.Status = status;
            db.WorkflowExecutions.Add(run);
        }
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task GetGraph_UntruncatedWindow_ShipsNoDensityAtAll()
    {
        // A quiet system must pay nothing for this: no second query, no extra payload.
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        db.WorkflowExecutions.Add(Settled(wf, DateTime.UtcNow.AddMinutes(-5)));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Density.Should().BeEmpty();
        graph.Meta.DensityBucketSeconds.Should().Be(0);
        graph.Meta.DensityCapped.Should().BeFalse();
    }

    [Fact]
    public async Task GetGraph_TruncatedWindow_CountsEveryRunIncludingTheOnesPastTheBarCap()
    {
        // 1200 runs, 1000 of them bars — the density must account for all 1200, otherwise the
        // console cannot answer "how much ran in this window?" at all.
        var wf = Guid.NewGuid();
        // 500 ms apart, so all 1200 comfortably fit the 20-minute window even if the request
        // lands a moment after the seed — a 1 s spacing would push the oldest rows out of it.
        var db = await SeedBusyWindow(wf, 1200, TimeSpan.FromMilliseconds(500),
            (3, ExecutionStatus.Failed), (7, ExecutionStatus.Failed), (11, ExecutionStatus.Cancelled));

        var graph = await GetGraph(NewController(db));

        var lane = graph.Density.Should().ContainSingle().Subject;
        lane.WorkflowId.Should().Be(wf);
        lane.Buckets.Sum(b => b.Total).Should().Be(1200);
        lane.Buckets.Sum(b => b.Failed).Should().Be(2);
        lane.Buckets.Sum(b => b.Cancelled).Should().Be(1);
        graph.Meta.DensityCapped.Should().BeFalse();
    }

    [Theory]
    [InlineData(20, 25)]
    [InlineData(60, 75)]
    [InlineData(240, 300)]
    public async Task GetGraph_DensityBucketWidth_ScalesWithTheWindow(int windowMinutes, int expectedSeconds)
    {
        // Fixed bucket COUNT, not fixed bucket width: that is what makes a wider window cost the
        // console nothing extra, however many runs sit behind it.
        var db = await SeedBusyWindow(Guid.NewGuid(), 1010, TimeSpan.FromSeconds(1));

        var graph = await GetGraph(NewController(db), windowMinutes);

        graph.Meta.DensityBucketSeconds.Should().Be(expectedSeconds);
        graph.Density.Should().ContainSingle().Which.Buckets.Should().HaveCountLessThanOrEqualTo(48);
    }

    [Fact]
    public async Task GetGraph_DensityBuckets_AreAscendingAndAnchoredOnRecentSince()
    {
        // Bucket 0 starts at RecentSinceUtc — the console turns an index straight back into a
        // time range, so an off-by-one anchor would slide the whole history sideways.
        var db = await SeedBusyWindow(Guid.NewGuid(), 1010, TimeSpan.FromSeconds(1));

        var graph = await GetGraph(NewController(db), 240);

        var buckets = graph.Density.Should().ContainSingle().Subject.Buckets;
        buckets.Select(b => b.BucketIndex).Should().BeInAscendingOrder();
        buckets.Should().OnlyContain(b => b.Total > 0);
        // Everything was seeded within the last ~17 min of a 4 h window → the newest 5-min slices.
        var lastIndex = (int)((DateTime.UtcNow - graph.Meta.RecentSinceUtc).TotalSeconds / graph.Meta.DensityBucketSeconds);
        buckets.Should().OnlyContain(b => b.BucketIndex <= lastIndex && b.BucketIndex >= lastIndex - 5);
    }

    [Fact]
    public async Task GetGraph_Density_IsFolderScopedLikeEverythingElse()
    {
        var db = TestDbFactory.Create();
        SeedFolders(db);
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        db.Workflows.AddRange(
            Wf(visible, "Visible", "{}", folderId: FolderA),
            Wf(hidden, "Hidden", "{}", folderId: FolderB));
        // The visible folder alone has to blow past the cap: RBAC scoping runs BEFORE the cap, so
        // the hidden folder's runs can neither trigger the truncation nor inflate the counts.
        var now = DateTime.UtcNow;
        for (var i = 0; i < 1100; i++)
        {
            db.WorkflowExecutions.Add(Settled(visible, now.AddMilliseconds(-500 * i)));
            db.WorkflowExecutions.Add(Settled(hidden, now.AddMilliseconds(-500 * i)));
        }
        await db.SaveChangesAsync();

        var scoped = new ScopedAuthz(new AccessibleFolderSet { IsUnrestricted = false, FolderIds = [FolderA] });
        var graph = await GetGraph(NewController(db, scoped, role: "Operator"));

        graph.Meta.RecentTruncated.Should().BeTrue();
        var lane = graph.Density.Should().ContainSingle().Subject;
        lane.WorkflowId.Should().Be(visible);
        lane.Buckets.Sum(b => b.Total).Should().Be(1100); // the hidden folder's 1100 never counted
    }

    [Fact]
    public async Task GetGraph_Density_SeparatesLanesPerWorkflow()
    {
        var db = TestDbFactory.Create();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        db.Workflows.AddRange(Wf(a, "A", "{}"), Wf(b, "B", "{}"));
        var now = DateTime.UtcNow;
        for (var i = 0; i < 600; i++) db.WorkflowExecutions.Add(Settled(a, now.AddSeconds(-i)));
        for (var i = 0; i < 500; i++) db.WorkflowExecutions.Add(Settled(b, now.AddSeconds(-i)));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Density.Should().HaveCount(2);
        graph.Density.Single(l => l.WorkflowId == a).Buckets.Sum(x => x.Total).Should().Be(600);
        graph.Density.Single(l => l.WorkflowId == b).Buckets.Sum(x => x.Total).Should().Be(500);
    }

    [Fact]
    public async Task GetGraph_Density_ExcludesActiveRunsJustLikeRecent()
    {
        // A Running row has no CompletedAt; counting it would inflate "what finished here".
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        var now = DateTime.UtcNow;
        for (var i = 0; i < 1010; i++) db.WorkflowExecutions.Add(Settled(wf, now.AddSeconds(-i)));
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Running, StartedAt = now.AddMinutes(-2),
        });
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Density.Should().ContainSingle().Which.Buckets.Sum(b => b.Total).Should().Be(1010);
    }

    // ---- Step activity on live runs ----------------------------------------------------------

    private static StepExecution StepRow(Guid execId, string stepId, ExecutionStatus status,
        DateTime startedAt, DateTime? completedAt, string? stepName = null)
        => new()
        {
            Id = Guid.NewGuid(), WorkflowExecutionId = execId, StepId = stepId, StepName = stepName,
            StepType = "runScript", Status = status, StartedAt = startedAt, CompletedAt = completedAt,
        };

    [Fact]
    public async Task GetGraph_RunningExecution_ReportsFinishedStepsAndLastProgress()
    {
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        var exec = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = exec, WorkflowId = wf, Status = ExecutionStatus.Running, StartedAt = t0,
        });
        // Only terminal rows exist under the default DeferRunningStateWrite — mirrored here.
        db.StepExecutions.AddRange(
            StepRow(exec, "s1", ExecutionStatus.Succeeded, t0, t0.AddMinutes(1), "Fetch"),
            StepRow(exec, "s2", ExecutionStatus.Succeeded, t0.AddMinutes(1), t0.AddMinutes(3), "Copy files"),
            StepRow(exec, "s3", ExecutionStatus.Skipped, t0.AddMinutes(3), t0.AddMinutes(3)));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        var run = graph.Running.Should().ContainSingle().Subject;
        // All three reached a terminal state…
        run.StepsFinished.Should().Be(3);
        // …but the Skipped branch is not progress: it never ran, so it must neither name the
        // last step nor reset the stagnation clock.
        run.LastCompletedStepName.Should().Be("Copy files");
        run.LastProgressAt.Should().BeCloseTo(t0.AddMinutes(3), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetGraph_RunningExecution_SkippedBranchDoesNotCountAsProgress()
    {
        // A branch skipped AFTER the last real step would otherwise silently reset the
        // stagnation clock — the run would look busy while sitting on one step.
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        var exec = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        var t0 = DateTime.UtcNow.AddMinutes(-20);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = exec, WorkflowId = wf, Status = ExecutionStatus.Running, StartedAt = t0 });
        db.StepExecutions.AddRange(
            StepRow(exec, "s1", ExecutionStatus.Succeeded, t0, t0.AddMinutes(1), "Real work"),
            StepRow(exec, "s2", ExecutionStatus.Skipped, t0.AddMinutes(15), t0.AddMinutes(15), "Dead branch"));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        var run = graph.Running.Should().ContainSingle().Subject;
        run.LastCompletedStepName.Should().Be("Real work");
        run.LastProgressAt.Should().BeCloseTo(t0.AddMinutes(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetGraph_RunningExecution_LastCompletedStepFallsBackToStepId()
    {
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        var exec = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        var t0 = DateTime.UtcNow.AddMinutes(-5);
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = exec, WorkflowId = wf, Status = ExecutionStatus.Running, StartedAt = t0 });
        db.StepExecutions.Add(StepRow(exec, "step-42", ExecutionStatus.Succeeded, t0, t0.AddMinutes(1)));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Running.Should().ContainSingle().Which.LastCompletedStepName.Should().Be("step-42");
    }

    [Fact]
    public async Task GetGraph_RunningExecution_WithNoFinishedSteps_ReportsZeroAndNullProgress()
    {
        // Freshly started run: enriched (so not null), but nothing has finished yet.
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = wf, Status = ExecutionStatus.Running, StartedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        var run = graph.Running.Should().ContainSingle().Subject;
        run.StepsFinished.Should().BeNull(); // no step rows at all → no aggregate row → unknown
        run.LastProgressAt.Should().BeNull();
    }

    [Fact]
    public async Task GetGraph_StepActivity_DoesNotBleedBetweenConcurrentRuns()
    {
        var db = TestDbFactory.Create();
        var wf = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        db.Workflows.Add(Wf(wf, "W", "{}"));
        var t0 = DateTime.UtcNow.AddMinutes(-5);
        db.WorkflowExecutions.AddRange(
            new WorkflowExecution { Id = a, WorkflowId = wf, Status = ExecutionStatus.Running, StartedAt = t0 },
            new WorkflowExecution { Id = b, WorkflowId = wf, Status = ExecutionStatus.Running, StartedAt = t0 });
        db.StepExecutions.AddRange(
            StepRow(a, "a1", ExecutionStatus.Succeeded, t0, t0.AddMinutes(1), "A one"),
            StepRow(a, "a2", ExecutionStatus.Succeeded, t0, t0.AddMinutes(2), "A two"),
            StepRow(b, "b1", ExecutionStatus.Succeeded, t0, t0.AddMinutes(1), "B one"));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Running.Single(r => r.ExecutionId == a).StepsFinished.Should().Be(2);
        graph.Running.Single(r => r.ExecutionId == b).StepsFinished.Should().Be(1);
        graph.Running.Single(r => r.ExecutionId == b).LastCompletedStepName.Should().Be("B one");
    }

    [Fact]
    public async Task GetGraph_NoRunningExecutions_SkipsTheActivityQueriesEntirely()
    {
        // An idle system must pay nothing for the enrichment; also guards the Contains([])
        // translation on an empty StepExecutions table.
        var db = TestDbFactory.Create();
        db.Workflows.Add(Wf(Guid.NewGuid(), "W", "{}"));
        await db.SaveChangesAsync();

        var graph = await GetGraph(NewController(db));

        graph.Running.Should().BeEmpty();
    }

    private sealed class ScopedAuthz : IResourceAuthorizationService
    {
        private readonly AccessibleFolderSet _set;
        public ScopedAuthz(AccessibleFolderSet set) => _set = set;

        /// <summary>What <see cref="GetWorkflowCapabilitiesAsync"/> hands back. Defaults to full.</summary>
        public ResourceCapabilities Capabilities { get; set; } = ResourceCapabilities.All;

        /// <summary>Every folder the subject asked about — asserts the per-folder dedup.</summary>
        public List<Guid> CapabilityLookups { get; } = [];

        public Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(_set);

        public Task<bool> CanAccessWorkflowAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
            => Task.FromResult(_set.IsUnrestricted || _set.FolderIds.Contains(folderId));
        public Task<bool> CanAccessFolderAsync(ClaimsPrincipal user, Guid folderId, ResourceOp op, CancellationToken ct = default)
            => CanAccessWorkflowAsync(user, folderId, op, ct);
        public Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
        {
            CapabilityLookups.Add(folderId);
            return Task.FromResult(Capabilities);
        }
        public Task<ResourceCapabilities> GetFolderCapabilitiesAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult(Capabilities);
        public Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult<SharedFolderRole?>(SharedFolderRole.FolderViewer);
        public void InvalidateAll() { }
    }
}

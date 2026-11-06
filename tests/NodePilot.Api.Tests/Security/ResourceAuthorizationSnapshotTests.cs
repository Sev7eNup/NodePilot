using System.Data.Common;
using System.Diagnostics;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Security;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Security;

public sealed class ResourceAuthorizationSnapshotTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task FolderList_ReadCountDoesNotGrowWithVisibleFolders(int visibleFolders)
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var ownedConnection = connection;
        await using var ownedSeed = seed;
        var user = LocalUser("folder-list-operator");
        var parent = Folder("Visible", SharedWorkflowFolder.RootFolderId);
        seed.AddRange(user, parent, UserGrant(parent.Id, user.Id, SharedFolderRole.FolderEditor));
        for (var i = 1; i < visibleFolders; i++)
            seed.Add(Folder($"Child-{i}", parent.Id, depth: 2));
        seed.Add(Folder("Hidden", SharedWorkflowFolder.RootFolderId));
        await seed.SaveChangesAsync(Ct);

        var counter = new ReadCounter();
        await using var db = CountingContext(connection, counter);
        async Task ReadList()
        {
            var controller = new SharedWorkflowFoldersController(db, NoopAuditWriter.Instance,
                new ResourceAuthorizationService(db), new RecordingHubContext(), new RecordingFolderProjection())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = Principal(user.Id) },
                },
            };
            var result = await controller.GetAll(Ct);
            var folders = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                .Should().BeOfType<List<SharedFolderResponse>>().Subject;
            folders.Should().HaveCount(visibleFolders);
            folders.Should().OnlyContain(folder => folder.Capabilities.CanRead && folder.Capabilities.CanEdit);
        }

        await ReadList(); // Warm EF query compilation before comparing request costs.
        var elapsed = new List<double>();
        for (var iteration = 0; iteration < 5; iteration++)
        {
            counter.Reset();
            var timer = Stopwatch.StartNew();
            await ReadList();
            elapsed.Add(timer.Elapsed.TotalMilliseconds);
        }

        output.WriteLine("Folders={0}; reads={1}; grants={2}; users={3}; median_ms={4:F3}",
            visibleFolders, counter.Reads, counter.Grants, counter.Users, elapsed.Order().ElementAt(2));
        counter.Grants.Should().Be(1, "visibility and every capability must share the principal's grant snapshot");
        counter.Users.Should().Be(1, "directory identity is loaded once per request");
        counter.Reads.Should().BeLessThanOrEqualTo(5, "listing must not add a database read per visible folder");
    }

    [Fact]
    public async Task CapabilitiesBeforeVisibility_ReuseTheSameGrantRead()
    {
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        await using var ownedConnection = connection;
        await using var ownedSeed = seed;
        var user = LocalUser("capabilities-first");
        var parent = Folder("Parent", SharedWorkflowFolder.RootFolderId);
        var child = Folder("Child", parent.Id, depth: 2);
        seed.AddRange(user, parent, child, UserGrant(parent.Id, user.Id, SharedFolderRole.FolderEditor));
        await seed.SaveChangesAsync(Ct);
        var counter = new ReadCounter();
        await using var db = CountingContext(connection, counter);
        var service = new ResourceAuthorizationService(db);
        var principal = Principal(user.Id);

        (await service.GetWorkflowCapabilitiesAsync(principal, child.Id, Ct)).CanEdit.Should().BeTrue();
        (await service.GetAccessibleFolderIdsAsync(principal, Ct)).FolderIds.Should().BeEquivalentTo([parent.Id, child.Id]);
        (await service.GetFolderCapabilitiesAsync(principal, parent.Id, Ct)).CanEdit.Should().BeTrue();
        (await service.GetWorkflowCapabilitiesAsync(principal, Guid.NewGuid(), Ct)).Should().Be(ResourceCapabilities.None);

        counter.Grants.Should().Be(1);
        counter.Reads.Should().Be(3);
    }

    [Fact]
    public async Task Snapshot_IsPrincipalSpecific_AndGlobalCapsAreAppliedPerCall()
    {
        await using var db = TestDbFactory.Create();
        var alice = LocalUser("alice");
        var bob = LocalUser("bob");
        var finance = Folder("Finance", SharedWorkflowFolder.RootFolderId);
        var sales = Folder("Sales", SharedWorkflowFolder.RootFolderId);
        db.AddRange(alice, bob, finance, sales,
            UserGrant(finance.Id, alice.Id, SharedFolderRole.FolderAdmin),
            UserGrant(sales.Id, bob.Id, SharedFolderRole.FolderViewer));
        await db.SaveChangesAsync(Ct);
        var service = new ResourceAuthorizationService(db);

        (await service.GetAccessibleFolderIdsAsync(Principal(alice.Id), Ct)).FolderIds.Should().BeEquivalentTo([finance.Id]);
        (await service.GetAccessibleFolderIdsAsync(Principal(bob.Id), Ct)).FolderIds.Should().BeEquivalentTo([sales.Id]);
        (await service.GetWorkflowCapabilitiesAsync(Principal(bob.Id), finance.Id, Ct)).Should().Be(ResourceCapabilities.None);
        (await service.GetWorkflowCapabilitiesAsync(Principal(alice.Id), finance.Id, Ct))
            .Should().Be(new ResourceCapabilities(true, true, true, false, false));
        (await service.GetWorkflowCapabilitiesAsync(Principal(alice.Id, "Viewer"), finance.Id, Ct))
            .Should().Be(new ResourceCapabilities(true, false, false, false, false));
        (await service.GetWorkflowCapabilitiesAsync(Principal(bob.Id, "Admin"), Guid.NewGuid(), Ct))
            .Should().Be(ResourceCapabilities.All);
    }

    [Fact]
    public async Task InvalidateAll_RefreshesVisibilityCapabilitiesAndAncestryAfterMutation()
    {
        await using var db = TestDbFactory.Create();
        var user = LocalUser("mutation");
        var parent = Folder("Granted", SharedWorkflowFolder.RootFolderId);
        var child = Folder("Child", parent.Id, depth: 2);
        var grant = UserGrant(parent.Id, user.Id, SharedFolderRole.FolderEditor);
        db.AddRange(user, parent, child, grant);
        await db.SaveChangesAsync(Ct);
        var service = new ResourceAuthorizationService(db);
        var principal = Principal(user.Id);
        (await service.GetAccessibleFolderIdsAsync(principal, Ct)).FolderIds.Should().Contain(child.Id);
        (await service.GetWorkflowCapabilitiesAsync(principal, child.Id, Ct)).CanEdit.Should().BeTrue();

        using (await FolderTreeMutationLock.SharedWorkflowFolders.BeginMutationAsync(Ct))
        {
            child.ParentFolderId = SharedWorkflowFolder.RootFolderId;
            child.Depth = 1;
            await db.SaveChangesAsync(Ct);
            service.InvalidateAll();
            (await service.GetAccessibleFolderIdsAsync(principal, Ct)).FolderIds.Should().NotContain(child.Id);
            (await service.GetWorkflowCapabilitiesAsync(principal, child.Id, Ct)).Should().Be(ResourceCapabilities.None);
        }

        db.Remove(grant);
        await db.SaveChangesAsync(Ct);
        service.InvalidateAll();
        (await service.GetAccessibleFolderIdsAsync(principal, Ct)).FolderIds.Should().BeEmpty();
        (await service.GetWorkflowCapabilitiesAsync(principal, parent.Id, Ct)).Should().Be(ResourceCapabilities.None);
    }

    [Fact]
    public async Task GroupSnapshot_PreservesAuthorityPairs_AndRefreshesMembershipAfterInvalidation()
    {
        await using var db = TestDbFactory.Create();
        const string issuerA = "https://idp.example.test/a";
        const string issuerB = "https://idp.example.test/b";
        var user = LocalUser("oidc");
        user.Provider = AuthProvider.Oidc;
        var allowed = Folder("Allowed", SharedWorkflowFolder.RootFolderId);
        var crossed = Folder("Crossed", SharedWorkflowFolder.RootFolderId);
        var membership = new DirectoryMembership
        {
            UserId = user.Id, Authority = issuerA, GroupKey = "operators", LastSeenAt = DateTime.UtcNow,
        };
        db.AddRange(user, allowed, crossed, membership,
            new ExternalIdentity { Id = Guid.NewGuid(), UserId = user.Id, Authority = issuerA, Subject = "a" },
            new ExternalIdentity { Id = Guid.NewGuid(), UserId = user.Id, Authority = issuerB, Subject = "b" },
            new DirectoryMembership { UserId = user.Id, Authority = issuerB, GroupKey = "finance", LastSeenAt = DateTime.UtcNow },
            GroupGrant(allowed.Id, issuerA, "operators"),
            GroupGrant(crossed.Id, issuerB, "operators"));
        await db.SaveChangesAsync(Ct);
        var service = new ResourceAuthorizationService(db);
        var principal = Principal(user.Id);

        (await service.GetAccessibleFolderIdsAsync(principal, Ct)).FolderIds.Should().BeEquivalentTo([allowed.Id]);
        (await service.GetWorkflowCapabilitiesAsync(principal, allowed.Id, Ct)).CanRun.Should().BeTrue();
        (await service.GetWorkflowCapabilitiesAsync(principal, crossed.Id, Ct)).Should().Be(ResourceCapabilities.None);

        db.Remove(membership);
        await db.SaveChangesAsync(Ct);
        service.InvalidateAll();
        (await service.GetAccessibleFolderIdsAsync(principal, Ct)).FolderIds.Should().BeEmpty();
        (await service.GetWorkflowCapabilitiesAsync(principal, allowed.Id, Ct)).Should().Be(ResourceCapabilities.None);
    }

    private static NodePilotDbContext CountingContext(Microsoft.Data.Sqlite.SqliteConnection connection, ReadCounter counter)
        => new(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).AddInterceptors(counter).Options);

    private static User LocalUser(string name) => new()
    {
        Id = Guid.NewGuid(), Username = name, Provider = AuthProvider.Local, Role = UserRole.Operator, IsActive = true,
    };

    private static SharedWorkflowFolder Folder(string name, Guid parentId, int depth = 1) => new()
    {
        Id = Guid.NewGuid(), Name = name, ParentFolderId = parentId, Path = "/" + name, Depth = depth,
    };

    private static SharedFolderPermission UserGrant(Guid folderId, Guid userId, SharedFolderRole role) => new()
    {
        Id = Guid.NewGuid(), FolderId = folderId, PrincipalType = FolderPrincipalType.User,
        PrincipalKey = userId.ToString("D"), Role = role,
    };

    private static SharedFolderPermission GroupGrant(Guid folderId, string authority, string key) => new()
    {
        Id = Guid.NewGuid(), FolderId = folderId, PrincipalType = FolderPrincipalType.Group,
        PrincipalAuthority = authority, PrincipalKey = key, Role = SharedFolderRole.FolderOperator,
    };

    private static ClaimsPrincipal Principal(Guid userId, string role = "Operator") => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role)], "test"));

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }
        public int Grants { get; private set; }
        public int Users { get; private set; }
        public void Reset() => Reads = Grants = Users = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (command.CommandText.Contains("FROM \"SharedFolderPermissions\"", StringComparison.Ordinal)) Grants++;
            if (command.CommandText.Contains("FROM \"Users\"", StringComparison.Ordinal)) Users++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

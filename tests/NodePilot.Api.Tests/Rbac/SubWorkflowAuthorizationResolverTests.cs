using FluentAssertions;
using NodePilot.Api.Security;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Rbac;

/// <summary>
/// Engine-side runtime gate for cross-folder sub-workflow calls. Backstop for the
/// publish-time PrePublishChecklist check â€” folder permissions can revoke between
/// publish and run.
/// </summary>
public sealed class SubWorkflowAuthorizationResolverTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly Data.NodePilotDbContext _db;
    private readonly Guid _financeId = Guid.NewGuid();
    private readonly Guid _salesId = Guid.NewGuid();
    private readonly User _adminUser = new() { Id = Guid.NewGuid(), Username = "admin", PasswordHash = "x", Role = UserRole.Admin };
    private readonly User _editorUser = new() { Id = Guid.NewGuid(), Username = "editor", PasswordHash = "x", Role = UserRole.Operator };
    private readonly User _strangerUser = new() { Id = Guid.NewGuid(), Username = "stranger", PasswordHash = "x", Role = UserRole.Operator };
    private readonly Workflow _financeParent;
    private readonly Workflow _salesChild;

    public SubWorkflowAuthorizationResolverTests()
    {
        var (conn, db) = TestDbFactory.CreateWithConnection();
        _conn = conn;
        _db = db;

        var rootId = SharedWorkflowFolder.RootFolderId;
        _db.SharedWorkflowFolders.AddRange(
            new SharedWorkflowFolder { Id = _financeId, ParentFolderId = rootId, Name = "Finance", Path = "/Finance", Depth = 1 },
            new SharedWorkflowFolder { Id = _salesId, ParentFolderId = rootId, Name = "Sales", Path = "/Sales", Depth = 1 });
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _financeId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _editorUser.Id.ToString("D"),
            Role = SharedFolderRole.FolderEditor,
        });

        _db.Users.AddRange(_adminUser, _editorUser, _strangerUser);
        _financeParent = new Workflow { Id = Guid.NewGuid(), Name = "fwf", DefinitionJson = "{}", FolderId = _financeId, Version = 1, IsEnabled = true };
        _salesChild = new Workflow { Id = Guid.NewGuid(), Name = "swf", DefinitionJson = "{}", FolderId = _salesId, Version = 1, IsEnabled = true };
        _db.Workflows.AddRange(_financeParent, _salesChild);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task SameFolder_ActivePrincipal_IsAllowedWithoutFolderBoundaryCheck()
    {
        // Parent and child both in /Finance: account state is still checked, while no
        // second folder grant is required.
        var sibling = new Workflow { Id = Guid.NewGuid(), Name = "fwf2", DefinitionJson = "{}", FolderId = _financeId, Version = 1, IsEnabled = true };
        _db.Workflows.Add(sibling);
        _db.SaveChanges();

        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = _financeParent.Id,
            Status = ExecutionStatus.Running, StartedByUserId = _adminUser.Id,
        };
        var resolver = new SubWorkflowAuthorizationResolver(_db);
        var blocked = await resolver.IsBlockedAsync(exec, sibling, CancellationToken.None);
        blocked.Should().BeNull();
    }

    [Fact]
    public async Task SameFolder_StaleExternalPrincipal_IsBlocked()
    {
        _adminUser.Provider = AuthProvider.Ldap;
        _adminUser.LastDirectorySyncAt = DateTime.UtcNow.AddMinutes(-16);
        _db.SaveChanges();
        var sibling = new Workflow
        {
            Id = Guid.NewGuid(), Name = "fwf2", DefinitionJson = "{}",
            FolderId = _financeId, Version = 1, IsEnabled = true,
        };
        _db.Workflows.Add(sibling);
        _db.SaveChanges();
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = _financeParent.Id,
            Status = ExecutionStatus.Running, StartedByUserId = _adminUser.Id,
        };

        var blocked = await new SubWorkflowAuthorizationResolver(_db)
            .IsBlockedAsync(exec, sibling, CancellationToken.None);

        blocked.Should().Contain("stale directory authorization snapshot");
    }

    [Fact]
    public async Task CrossFolder_AdminPrincipal_IsAllowed()
    {
        var exec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = _financeParent.Id, Status = ExecutionStatus.Running, StartedByUserId = _adminUser.Id };
        var resolver = new SubWorkflowAuthorizationResolver(_db);
        (await resolver.IsBlockedAsync(exec, _salesChild, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CrossFolder_InactiveAdminPrincipal_IsBlocked()
    {
        _adminUser.IsActive = false;
        _db.SaveChanges();

        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = _financeParent.Id,
            Status = ExecutionStatus.Running, StartedByUserId = _adminUser.Id,
        };
        var resolver = new SubWorkflowAuthorizationResolver(_db);

        var msg = await resolver.IsBlockedAsync(exec, _salesChild, CancellationToken.None);

        msg.Should().Contain("inactive");
    }

    [Fact]
    public async Task TriggerDrivenRun_StaleExternalAdmin_IsBlockedFailClosed()
    {
        _adminUser.Provider = AuthProvider.Ldap;
        _adminUser.LastDirectorySyncAt = DateTime.UtcNow.AddMinutes(-16);
        _adminUser.DirectorySyncStatus = "Stale";
        _financeParent.PublishedByUserId = _adminUser.Id;
        _db.SaveChanges();
        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = _financeParent.Id,
            Status = ExecutionStatus.Running, StartedByUserId = null,
        };

        var msg = await new SubWorkflowAuthorizationResolver(_db)
            .IsBlockedAsync(exec, _salesChild, CancellationToken.None);

        msg.Should().Contain("stale directory authorization snapshot");
    }

    [Fact]
    public async Task CrossFolder_PrincipalWithoutGrant_IsBlocked()
    {
        var exec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = _financeParent.Id, Status = ExecutionStatus.Running, StartedByUserId = _strangerUser.Id };
        var resolver = new SubWorkflowAuthorizationResolver(_db);
        var msg = await resolver.IsBlockedAsync(exec, _salesChild, CancellationToken.None);
        msg.Should().NotBeNull();
        msg.Should().Contain("stranger");
        msg.Should().Contain("no folder-permission grant");
    }

    [Fact]
    public async Task CrossFolder_PrincipalWithFolderViewerOnly_IsBlocked()
    {
        // Editor on /Finance has no grant on /Sales â€” cross-folder call to /Sales fails.
        var exec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = _financeParent.Id, Status = ExecutionStatus.Running, StartedByUserId = _editorUser.Id };
        var resolver = new SubWorkflowAuthorizationResolver(_db);
        var msg = await resolver.IsBlockedAsync(exec, _salesChild, CancellationToken.None);
        msg.Should().NotBeNull();
        msg.Should().Contain("editor");
    }

    [Fact]
    public async Task CrossFolder_PrincipalWithFolderOperator_IsAllowed()
    {
        // Add a FolderOperator grant on /Sales for editorUser.
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _salesId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _editorUser.Id.ToString("D"),
            Role = SharedFolderRole.FolderOperator,
        });
        _db.SaveChanges();

        var exec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = _financeParent.Id, Status = ExecutionStatus.Running, StartedByUserId = _editorUser.Id };
        var resolver = new SubWorkflowAuthorizationResolver(_db);
        (await resolver.IsBlockedAsync(exec, _salesChild, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CrossFolder_OidcGroupGrant_UsesExactIssuerMembership()
    {
        const string issuer = "https://login.example.test/tenant/v2.0";
        const string group = "automation-operators";
        var oidcUser = new User
        {
            Id = Guid.NewGuid(), Username = "oidc@example.test", Provider = AuthProvider.Oidc,
            Role = UserRole.Operator, IsActive = true,
            LastDirectorySyncAt = DateTime.UtcNow,
        };
        _db.AddRange(
            oidcUser,
            new ExternalIdentity
            {
                Id = Guid.NewGuid(), UserId = oidcUser.Id,
                Authority = issuer, Subject = "oidc-subject",
            },
            new DirectoryMembership
            {
                UserId = oidcUser.Id, Authority = issuer, GroupKey = group,
                LastSeenAt = DateTime.UtcNow,
            },
            new SharedFolderPermission
            {
                Id = Guid.NewGuid(), FolderId = _salesId,
                PrincipalType = FolderPrincipalType.Group,
                PrincipalAuthority = issuer,
                PrincipalKey = group,
                Role = SharedFolderRole.FolderOperator,
            });
        await _db.SaveChangesAsync();
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = _financeParent.Id,
            Status = ExecutionStatus.Running, StartedByUserId = oidcUser.Id,
        };

        var blocked = await new SubWorkflowAuthorizationResolver(_db)
            .IsBlockedAsync(execution, _salesChild, CancellationToken.None);

        blocked.Should().BeNull();
    }

    [Fact]
    public async Task CrossFolder_GlobalViewerWithFolderOperator_IsBlockedByGlobalRoleCap()
    {
        _editorUser.Role = UserRole.Viewer;
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _salesId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _editorUser.Id.ToString("D"),
            Role = SharedFolderRole.FolderOperator,
        });
        _db.SaveChanges();

        var exec = new WorkflowExecution
        {
            Id = Guid.NewGuid(), WorkflowId = _financeParent.Id,
            Status = ExecutionStatus.Running, StartedByUserId = _editorUser.Id,
        };
        var resolver = new SubWorkflowAuthorizationResolver(_db);

        var msg = await resolver.IsBlockedAsync(exec, _salesChild, CancellationToken.None);

        msg.Should().Contain("global role");
        msg.Should().Contain("Viewer");
    }

    [Fact]
    public async Task TriggerDrivenRun_UsesStablePublishedByPrincipal()
    {
        _financeParent.PublishedByUserId = _editorUser.Id;
        // A later administrative move/update must not replace the publisher's authority.
        _financeParent.UpdatedBy = _adminUser.Username;
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = _salesId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = _editorUser.Id.ToString("D"),
            Role = SharedFolderRole.FolderOperator,
        });
        _db.SaveChanges();

        var exec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = _financeParent.Id, Status = ExecutionStatus.Running, StartedByUserId = null };
        var resolver = new SubWorkflowAuthorizationResolver(_db);
        (await resolver.IsBlockedAsync(exec, _salesChild, CancellationToken.None))
            .Should().BeNull("trigger-driven run uses the stable publish principal");
    }

    [Fact]
    public async Task TriggerDrivenRun_NoPublishedBy_IsBlockedEvenWhenUpdatedByResolves()
    {
        _financeParent.PublishedByUserId = null;
        _financeParent.UpdatedBy = _adminUser.Username;
        _financeParent.CreatedBy = _adminUser.Username;
        _db.SaveChanges();

        var exec = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowId = _financeParent.Id, Status = ExecutionStatus.Running, StartedByUserId = null };
        var resolver = new SubWorkflowAuthorizationResolver(_db);
        var msg = await resolver.IsBlockedAsync(exec, _salesChild, CancellationToken.None);
        msg.Should().NotBeNull();
        msg.Should().Contain("requires an effective principal");
    }
}

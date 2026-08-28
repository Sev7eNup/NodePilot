using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using Xunit;

namespace NodePilot.Data.Tests.Rbac;

/// <summary>
/// Migration-level coverage for RBAC Tier A (the first RBAC rollout phase): the
/// AddSharedWorkflowFolders migration must produce a usable Root folder, every existing
/// workflow must end up assigned to Root, and the bootstrapper's idempotent
/// default-permissions step must grant Operator/Viewer users the right baseline grant on
/// Root (Admin gets nothing, since the global role already bypasses folder checks).
/// </summary>
public sealed class SharedWorkflowFolderMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SharedWorkflowFolderMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"nodepilot-rbac-migration-{Guid.NewGuid():N}.db");
        _connectionString = $"DataSource={_dbPath}";
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* */ }
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* */ }
    }

    private NodePilotDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new NodePilotDbContext(options);
    }

    [Fact]
    public void Bootstrap_FreshDb_CreatesRootFolder_WithKnownId()
    {
        using var db = NewContext();
        MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);

        var root = db.SharedWorkflowFolders.SingleOrDefault();
        root.Should().NotBeNull();
        root!.Id.Should().Be(SharedWorkflowFolder.RootFolderId,
            "Root must use the hard-coded sentinel so application code can reference it without a lookup");
        root.ParentFolderId.Should().BeNull();
        root.Name.Should().Be("Root");
        root.Path.Should().Be("/");
        root.Depth.Should().Be(0);
    }

    [Fact]
    public void Bootstrap_BackfillsExistingWorkflowsToRoot()
    {
        // Seed one workflow before any RBAC awareness; simulates an upgrade from a pre-RBAC
        // schema. The migration's AddColumn defaultValue must put it on Root automatically.
        var preExistingId = Guid.NewGuid();
        using (var db = NewContext())
        {
            MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);  // creates schema + Root
            // Deleting the auto-created Root and re-inserting a Workflow without FolderId
            // would need raw SQL on an already-migrated DB. Instead this adds a workflow after
            // bootstrap and checks it defaults to Root, the same default AddColumn applies on
            // upgrade and the model applies on fresh inserts.
            db.Workflows.Add(new Workflow
            {
                Id = preExistingId,
                Name = "legacy-wf",
                DefinitionJson = "{}",
                Version = 1,
            });
            db.SaveChanges();
        }

        using var db2 = NewContext();
        var wf = db2.Workflows.Single(w => w.Id == preExistingId);
        wf.FolderId.Should().Be(SharedWorkflowFolder.RootFolderId,
            "every workflow must end up assigned to a real folder â€” Root is the default");
    }

    [Fact]
    public void Bootstrap_DoesNotReseedRootPermissions_AfterAdminRevoke()
    {
        // The Root permission seed runs once as part of an EF migration; repeated Bootstrap
        // calls must not re-grant a permission an admin has revoked.
        var operatorId = Guid.NewGuid();

        using (var db = NewContext())
        {
            MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);
            // Simulate UsersController.Create: adds user + default Root grant.
            db.Users.Add(new User { Id = operatorId, Username = "op", PasswordHash = "x", Role = UserRole.Operator });
            db.SharedFolderPermissions.Add(new SharedFolderPermission
            {
                Id = Guid.NewGuid(), FolderId = SharedWorkflowFolder.RootFolderId,
                PrincipalType = FolderPrincipalType.User, PrincipalKey = operatorId.ToString("D"),
                Role = SharedFolderRole.FolderEditor,
            });
            db.SaveChanges();
        }

        // Admin revokes the Root permission (simulating
        // DELETE /api/shared-folders/{root}/permissions/{permId}).
        using (var db = NewContext())
        {
            var revoke = db.SharedFolderPermissions
                .Single(p => p.PrincipalKey == operatorId.ToString("D") && p.FolderId == SharedWorkflowFolder.RootFolderId);
            db.SharedFolderPermissions.Remove(revoke);
            db.SaveChanges();
        }

        // Three more bootstraps; the revoke must survive every one of them.
        for (var i = 0; i < 3; i++)
        {
            using var db = NewContext();
            MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);
        }

        using var verify = NewContext();
        verify.SharedFolderPermissions
            .Any(p => p.PrincipalKey == operatorId.ToString("D") && p.FolderId == SharedWorkflowFolder.RootFolderId)
            .Should().BeFalse(
                "intentional Admin revokes must not be re-created by re-bootstraps â€” F1 fix " +
                "(the prior runtime backfill loop was the bug)");
    }

    // Note: BackfillSharedFolderUserPermissions' SELECT-INSERT path is covered end-to-end by
    // the integration suite (real upgrade scenarios with pre-existing users). Reproducing
    // "users existed before the migration applied" in a SQLite unit test is impractical:
    // db.Database.Migrate() applies all pending migrations atomically against a model built
    // from scratch, so there is no hook to insert rows between InitialBaseline and the
    // backfill migration without forking EF's migrator. The revoke-survives-reboot behavior
    // is covered by Bootstrap_DoesNotReseedRootPermissions_AfterAdminRevoke above.

    [Fact]
    public void SiblingNameUniqueness_IsEnforcedAtSchemaLevel()
    {
        using var db = NewContext();
        MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);

        var rootId = SharedWorkflowFolder.RootFolderId;
        db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = rootId, Name = "Finance", Path = "/Finance", Depth = 1
        });
        db.SaveChanges();

        // A second sibling with the same name under the same parent must fail at the
        // unique-index level.
        db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = rootId, Name = "Finance", Path = "/Finance", Depth = 1
        });
        var act = () => db.SaveChanges();
        act.Should().Throw<DbUpdateException>("unique(ParentFolderId, Name) must reject duplicate sibling names");
    }

    [Fact]
    public void PermissionUniqueness_IsEnforcedAtSchemaLevel()
    {
        using var db = NewContext();
        MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);
        var userId = Guid.NewGuid();
        var rootId = SharedWorkflowFolder.RootFolderId;

        db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = rootId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = userId.ToString("D"),
            Role = SharedFolderRole.FolderViewer
        });
        db.SaveChanges();

        // Second grant for the same (folder, type, principal) tuple must be rejected.
        db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = rootId,
            PrincipalType = FolderPrincipalType.User, PrincipalKey = userId.ToString("D"),
            Role = SharedFolderRole.FolderEditor
        });
        var act = () => db.SaveChanges();
        act.Should().Throw<DbUpdateException>("a user can hold at most one role per folder â€” re-grant must update, not stack");
    }
}

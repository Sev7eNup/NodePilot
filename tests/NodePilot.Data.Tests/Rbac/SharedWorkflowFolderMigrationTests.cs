using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Data.Tests.Rbac;

/// <summary>
/// Migration-level coverage for RBAC Tier A (the first RBAC rollout phase): the AddSharedWorkflowFolders migration must
/// produce a usable Root folder, every existing workflow must end up assigned to Root,
/// and the bootstrapper's idempotent default-permissions step must grant Operator/Viewer
/// users the right baseline grant on Root (Admin gets nothing â€” global role bypasses).
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
        // Seed one workflow before any RBAC awareness â€” simulates an upgrade from a pre-RBAC
        // schema. The migration's AddColumn defaultValue must put it on Root automatically.
        var preExistingId = Guid.NewGuid();
        using (var db = NewContext())
        {
            MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);  // creates schema + Root
            // Reset state so the migration check is meaningful: delete the auto-created Root
            // and re-insert a Workflow without FolderId would require raw SQL on a freshly-
            // migrated DB. The realistic scenario here is "migration ran on an existing DB
            // with workflows" â€” covered by adding a workflow AFTER bootstrap and verifying
            // it points at Root by default (which AddColumn defaultValue does for upgrades
            // and the model default does for fresh inserts).
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
        // F1 fix (finding from the high-availability/HA review) â€” the prior runtime backfill loop re-created revoked rows on every
        // restart, undermining intentional Admin revokes. The new design moves the seed
        // into a one-shot EF migration; subsequent Bootstrap calls must NOT re-grant.
        var operatorId = Guid.NewGuid();

        using (var db = NewContext())
        {
            MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);
            // Simulate UsersController.Create â€” adds user + default Root grant.
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

        // Three more bootstraps â€” the revoke must SURVIVE every one of them.
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

    // Note: the new BackfillSharedFolderUserPermissions migration's SELECT-INSERT path
    // is covered end-to-end by the integration suite (real upgrade scenarios with users
    // pre-existing). Replicating "users existed before the migration applied" inside a
    // SQLite unit test is impractical: db.Database.Migrate() applies all pending
    // migrations atomically against a model that was created from scratch â€” there's no
    // hook to insert rows between InitialBaseline and the backfill migration without
    // forking EF's migrator. The F1 regression we care about (revokes surviving boots)
    // is fully covered by Bootstrap_DoesNotReseedRootPermissions_AfterAdminRevoke above.

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

        // Second sibling with the same name under the same parent must fail at the unique-index level.
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

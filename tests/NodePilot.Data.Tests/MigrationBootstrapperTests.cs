using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// MigrationBootstrapper.Bootstrap is a thin wrapper around
/// <c>db.Database.Migrate()</c>. The interesting contract:
///   * Empty DB → applies the full migration set, all schema is created.
///   * Already-migrated DB → no-op, no errors.
///   * Logs the applied count + provider name (operator visibility).
///
/// Tests use a real on-disk SQLite file (rather than :memory:) because EF's migration
/// pipeline relies on a connection it can re-open after schema changes — :memory:
/// connections forget their schema between Open() calls in some EF code paths. Each
/// test cleans up its own file.
/// </summary>
public sealed class MigrationBootstrapperTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public MigrationBootstrapperTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"nodepilot-migration-test-{Guid.NewGuid():N}.db");
        _connectionString = $"DataSource={_dbPath}";
    }

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best-effort */ }
        // SQLite holds connections in a pool — clear it so the file can be deleted on
        // Windows where open handles block the unlink.
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* */ }
    }

    private NodePilotDbContext NewContext()
    {
        // Suppress PendingModelChangesWarning the same way Program.cs does — the
        // ModelSnapshot is provider-specific (whichever provider was active at scaffolding
        // time) but our migrations are provider-agnostic, so EF reports false-positive
        // drift against the inactive providers. Without this, EF Core 9+ promotes the
        // warning to Error and `db.Database.Migrate()` throws.
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new NodePilotDbContext(options);
    }

    [Fact]
    public void Bootstrap_FreshDatabase_AppliesAllMigrationsAndCreatesSchema()
    {
        using var db = NewContext();

        MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);

        db.Database.GetAppliedMigrations().Should().NotBeEmpty(
            "an empty DB must end up with at least one applied migration after Bootstrap");

        // Sanity: at least one of the core tables exists and is queryable. Empty count is fine.
        db.Workflows.Count().Should().Be(0);
        db.Users.Count().Should().Be(0);
    }

    [Fact]
    public void EnterpriseIdentityMigration_BackfillsOnlyUnambiguousKeys_AndPreservesBreakGlassAdmin()
    {
        using var db = NewContext();
        var migrator = db.Database.GetService<IMigrator>();
        migrator.Migrate("20260712090321_AddWorkflowPublishedByUserId");

        static string UserInsert(Guid id, string username, string provider, string externalId, string role = "Viewer") => $"""
            INSERT INTO "Users"
                ("Id", "Username", "PasswordHash", "Role", "Provider", "ExternalId",
                 "KnownGroupSidsJson", "IsActive", "CreatedAt", "PasswordChangedAt",
                 "FailedLoginCount", "LockedUntil", "SecurityStamp")
            VALUES
                ('{id:D}', '{username}', NULL, '{role}', '{provider}', '{externalId}',
                 '[]', 1, '2026-07-12 00:00:00', '2026-07-12 00:00:00', 0, NULL, 0);
            """;

        var windowsId = Guid.NewGuid();
        var ldapId = Guid.NewGuid();
        var duplicateA = Guid.NewGuid();
        var duplicateB = Guid.NewGuid();
        var localAdminId = Guid.NewGuid();
        db.Database.ExecuteSqlRaw(UserInsert(windowsId, @"FIRMA\alice", "Windows", "S-1-5-21-1-2-3-1001"));
        db.Database.ExecuteSqlRaw(UserInsert(ldapId, "bob@firma.de", "Ldap", "71fe6e8c-546a-4b73-9910-a1d92090994a"));
        db.Database.ExecuteSqlRaw(UserInsert(duplicateA, "dup-a@firma.de", "Ldap", "82373442-c6bb-4091-a49f-a906a800198e"));
        db.Database.ExecuteSqlRaw(UserInsert(duplicateB, "dup-b@firma.de", "Ldap", "82373442-c6bb-4091-a49f-a906a800198e"));
        db.Database.ExecuteSqlRaw(UserInsert(localAdminId, "break-glass", "Local", "", "Admin"));

        migrator.Migrate();
        db.ChangeTracker.Clear();

        db.ExternalIdentities.Should().ContainSingle(i =>
            i.UserId == windowsId
            && i.Authority == NodePilot.Core.Models.ExternalIdentity.ActiveDirectoryAuthority
            && i.Subject == "S-1-5-21-1-2-3-1001");
        db.ExternalIdentities.Should().ContainSingle(i =>
            i.UserId == ldapId
            && i.Authority == NodePilot.Core.Models.ExternalIdentity.LegacyLdapAuthority);
        db.ExternalIdentities.Should().NotContain(i => i.UserId == duplicateA || i.UserId == duplicateB,
            "ambiguous legacy identities must be left for explicit administrator resolution");
        db.Users.Single(u => u.Username == "break-glass").IsBreakGlass.Should().BeTrue();
    }

    [Fact]
    public void Bootstrap_AlreadyMigratedDatabase_IsNoOp_DoesNotThrow()
    {
        // First bootstrap: applies the whole set.
        using (var db1 = NewContext())
        {
            MigrationBootstrapper.Bootstrap(db1, NullLogger.Instance);
        }

        // Second bootstrap on the same file: must be a no-op. Migration count unchanged.
        int afterFirst, afterSecond;
        using (var db2 = NewContext())
        {
            afterFirst = db2.Database.GetAppliedMigrations().Count();
        }
        using (var db3 = NewContext())
        {
            MigrationBootstrapper.Bootstrap(db3, NullLogger.Instance);
            afterSecond = db3.Database.GetAppliedMigrations().Count();
        }

        afterSecond.Should().Be(afterFirst,
            "a second Bootstrap on the same DB must not re-apply any migrations");
    }

    [Fact]
    public void Bootstrap_LogsProviderNameAndAppliedCount()
    {
        var logger = new CapturingLogger();
        using var db = NewContext();

        MigrationBootstrapper.Bootstrap(db, logger);

        logger.Messages.Should().ContainSingle(
            m => m.Contains("Database bootstrap") && m.Contains("Sqlite"),
            "operators read this line on startup to verify the right provider is wired up");
    }

    [Fact]
    public void Bootstrap_FreshDatabase_SeedsClusterLeaderRow_WithExpiredLease()
    {
        using var db = NewContext();

        MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);

        var rows = db.ClusterLeaders.ToList();
        rows.Should().ContainSingle(r => r.Resource == "primary",
            "exactly one ClusterLeader row keyed 'primary' must exist after fresh bootstrap");

        var row = rows.Single();
        row.OwnerNodeId.Should().BeEmpty("seed row is unowned until the first node acquires");
        row.LeaseEpoch.Should().Be(0);
        row.ExpiresAt.Should().BeBefore(DateTime.UtcNow,
            "seed lease must already be expired so the first node can immediately acquire");
    }

    [Fact]
    public void Bootstrap_AlreadySeeded_DoesNotOverwriteClusterLeaderRow()
    {
        // First bootstrap: seeds the row.
        using (var db1 = NewContext()) { MigrationBootstrapper.Bootstrap(db1, NullLogger.Instance); }

        // Simulate that a node has acquired the lease in the meantime.
        var acquiredAt = DateTime.UtcNow.AddMinutes(-1);
        using (var db2 = NewContext())
        {
            var row = db2.ClusterLeaders.Single(r => r.Resource == "primary");
            row.OwnerNodeId = "node-a";
            row.AcquiredAt = acquiredAt;
            row.ExpiresAt = DateTime.UtcNow.AddSeconds(20);
            row.LeaseEpoch = 7;
            db2.SaveChanges();
        }

        // Second bootstrap must NOT overwrite — the seed is opt-in for empty rows only.
        using var db3 = NewContext();
        MigrationBootstrapper.Bootstrap(db3, NullLogger.Instance);
        var preserved = db3.ClusterLeaders.Single(r => r.Resource == "primary");
        preserved.OwnerNodeId.Should().Be("node-a");
        preserved.LeaseEpoch.Should().Be(7);
    }

    [Fact]
    public void Bootstrap_SeedClusterLeader_StructuralDbError_IsRethrown()
    {
        // Drop the ClusterLeaders table after the migration set has applied, then run
        // Bootstrap a second time. The existence check `Any(x => x.Resource == "primary")`
        // raises a SqliteException (no such table) which is NOT a DbUpdateException — so
        // it bypasses the catch entirely and Bootstrap fails. This is the "boot loudly
        // when the schema is wrong" guarantee: an admin who fat-fingered a DROP must see
        // the error immediately, not get a silent partial-startup followed by lease-time
        // crashes.
        using (var primer = NewContext())
        {
            MigrationBootstrapper.Bootstrap(primer, NullLogger.Instance);
            primer.Database.ExecuteSqlRaw("DROP TABLE \"ClusterLeaders\";");
        }

        using var db = NewContext();
        var act = () => MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);
        act.Should().Throw<Exception>(
            "a real DB error (table missing, permissions, schema drift) must NOT be swallowed");
    }

    [Fact]
    public void Bootstrap_SeedClusterLeader_DbUpdateException_WithRowAbsent_IsRethrown()
    {
        // The narrow case the new logic specifically protects: SaveChanges fails with a
        // DbUpdateException AND the row does not exist afterward. Previously the catch
        // swallowed the exception unconditionally and the boot continued — masking real
        // permission/constraint problems behind a "lost the seed race" log message.
        //
        // We exercise this by constructing a ClusterLeader with a value that violates
        // the schema (e.g. NULL where NOT NULL is required) and then invoking the seed
        // logic directly. To avoid duplicating the production code, we use the public
        // Bootstrap entry: pre-seed-but-remove the row so the existence check returns
        // false, then add a NULL violation for the actual INSERT.
        using (var setup = NewContext())
        {
            MigrationBootstrapper.Bootstrap(setup, NullLogger.Instance);
            // Remove the seed row that the first Bootstrap created.
            setup.Database.ExecuteSqlRaw("DELETE FROM \"ClusterLeaders\";");
            // Add a CHECK constraint that the next INSERT will violate. SQLite syntax;
            // suffices for the test backend. Production uses real Postgres/SqlServer
            // which would enforce the same NOT NULL invariants.
            setup.Database.ExecuteSqlRaw(
                "CREATE TRIGGER reject_inserts BEFORE INSERT ON \"ClusterLeaders\" " +
                "BEGIN SELECT RAISE(ABORT, 'no inserts allowed'); END;");
        }

        using var db = NewContext();
        var act = () => MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);
        act.Should().Throw<DbUpdateException>(
            "if the INSERT fails AND the row still doesn't exist afterward, the catch " +
            "must rethrow so the underlying DB error surfaces — F1 fix from the HA review");
    }

    [Fact]
    public void Bootstrap_AppliedMigrationsList_HasNoUnknownEntries()
    {
        // Smoke check: every applied migration must also be in the project's assembly
        // (i.e. EF didn't pick up a stray migration from a sibling assembly). Catches
        // the rare misconfiguration where Migrations land in the wrong project.
        using var db = NewContext();

        MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);

        var applied = db.Database.GetAppliedMigrations().ToHashSet();
        var available = db.Database.GetMigrations().ToHashSet();
        applied.Should().BeSubsetOf(available,
            "applied migrations must be a subset of those defined in NodePilot.Data — " +
            "anything else means a stray migration was picked up from a sibling assembly");
    }

    /// <summary>Tiny ILogger that captures formatted messages for assertion.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}

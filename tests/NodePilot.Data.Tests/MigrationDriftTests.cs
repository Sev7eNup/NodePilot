using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Data.Tests;

/// <summary>
/// Drift guard for the provider-agnostic migration convention (see the "Datenbank"
/// section of CLAUDE.md). Freshly generated EF migrations contain provider-specific
/// column-type strings (<c>type: "uuid"</c>, <c>type: "uniqueidentifier"</c>,
/// <c>type: "text"</c>, …); if those aren't removed before commit, the inactive
/// provider breaks at <c>Migrate()</c> time. These tests catch that in CI — before
/// it turns into a production bug during a provider switch.
/// </summary>
public class MigrationDriftTests
{
    /// <summary>
    /// Scans all migration source files for <c>type: "…"</c> annotations and blocks
    /// known provider-specific type strings. The whitelist deliberately allows
    /// <c>SqlServer:Include</c> / <c>Npgsql:IndexInclude</c> — these are documented as
    /// intentionally dual-annotated (see AddCoveringIndexesForSqlServer).
    /// </summary>
    [Fact]
    public void MigrationSources_DoNotContain_ProviderSpecificTypeStrings()
    {
        var migrationsDir = ResolveMigrationsDirectory();
        var files = Directory.GetFiles(migrationsDir, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(f => !f.EndsWith(".Designer.cs") && !f.EndsWith("ModelSnapshot.cs"))
            .ToList();

        files.Should().NotBeEmpty($"expected migration files in {migrationsDir}");

        var typeAnnotation = new Regex(@"type:\s*""([^""]+)""", RegexOptions.Compiled);
        var offenders = new List<string>();

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var match = typeAnnotation.Match(lines[i]);
                if (!match.Success) continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        offenders.Should().BeEmpty(
            "Migrations müssen provider-agnostisch sein (CLAUDE.md). " +
            "Entferne `type: \"…\"`-Annotations — EF Core leitet den Typ aus dem CLR-" +
            "Property-Typ ab. Gefunden:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Round-trip: runs every migration Up once, Down once, then Up again against a
    /// fresh SQLite file. Catches missing Down operations, duplicate index names, and
    /// provider-specific constructs (e.g. <c>USING gin</c>) that only exist in the Up
    /// path and would look for the wrong index in the Down path.
    /// </summary>
    [Fact]
    public void Migrations_RoundTrip_UpDownUp_OnSqlite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nodepilot-roundtrip-{Guid.NewGuid():N}.db");
        var connStr = $"DataSource={dbPath}";

        try
        {
            using (var db = NewSqliteContext(connStr))
            {
                db.Database.Migrate();
                var applied = db.Database.GetAppliedMigrations().ToList();
                applied.Should().NotBeEmpty();

                var migrator = db.Database.GetService<IMigrator>();

                // Down: roll back to the first migration. If a Down operation is broken
                // (e.g. it drops an index that an earlier migration's Down already
                // removed), this fails here with a clear SQL error message.
                migrator.Migrate(targetMigration: applied.First());

                // Back up again: confirms that after Down + Up the schema ends up with
                // the same tables/indexes. This only checks schema stability, not data.
                migrator.Migrate();

                db.Database.GetAppliedMigrations().Should().BeEquivalentTo(applied,
                    "Up→Down→Up muss wieder beim selben Migration-Set landen");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void FullMigrationChain_GeneratesNativeSqlServerSchemaAndSafeEnterpriseKeys()
    {
        using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=NodePilotMigrationScriptOnly;Trusted_Connection=True")
                .Options);
        var latestMigration = db.Database.GetMigrations().Last();
        var script = db.Database.GetService<IMigrator>().GenerateScript(
            fromMigration: Migration.InitialDatabase,
            toMigration: latestMigration);

        script.Should().Contain("[Id] uniqueidentifier")
            .And.Contain("PRIMARY KEY ([Id])")
            .And.Contain("IX_DirectoryMemberships_UserId_Authority_GroupKey")
            .And.Contain("UX_SharedFolderPermissions_Principal")
            .And.Contain("[PrincipalAuthority] nvarchar(512)")
            .And.Contain("Latin1_General_100_BIN2");
        script.Should().NotContain("character varying")
            .And.NotContain(" uuid")
            .And.NotContain("bytea")
            .And.NotContain("boolean")
            .And.NotContain("timestamp with time zone");
        script.Should().NotContain(
            "PRIMARY KEY ([UserId], [Authority], [GroupKey])",
            "that intermediate clustered key exceeds SQL Server's 900-byte limit");
    }

    [Fact]
    public void FullMigrationChain_GeneratesNativePostgresTypes()
    {
        using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseNpgsql("Host=localhost;Database=NodePilotMigrationScriptOnly;Username=nodepilot;Password=not-used")
                .Options);
        var latestMigration = db.Database.GetMigrations().Last();
        var script = db.Database.GetService<IMigrator>().GenerateScript(
            fromMigration: Migration.InitialDatabase,
            toMigration: latestMigration);

        script.Should().Contain("uuid")
            .And.Contain("character varying")
            .And.Contain("bytea")
            .And.Contain("timestamp with time zone")
            .And.Contain("UX_SharedFolderPermissions_Principal")
            .And.Contain("\"PrincipalAuthority\" character varying(512)");
        script.Should().NotContain("uniqueidentifier")
            .And.NotContain("nvarchar")
            .And.NotContain("varbinary")
            .And.NotContain("datetime2")
            .And.NotContain("Latin1_General_100_BIN2");
    }

    [Fact]
    public void CurrentSqlServerModel_PreservesOrdinalIdentityCollationOnFreshCreate()
    {
        using var db = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=NodePilotCreateScriptOnly;Trusted_Connection=True")
                .Options);

        var script = db.Database.GenerateCreateScript();

        Regex.Matches(script, "COLLATE Latin1_General_100_BIN2", RegexOptions.IgnoreCase)
            .Should().HaveCountGreaterThanOrEqualTo(8,
                "authority/subject/group identifiers require exact case-sensitive storage semantics");
    }

    [Fact]
    public void GroupPrincipalAuthorityDowngrade_RejectsNamespaceCollisions()
    {
        const string latest = "20260712210753_ScopeFolderGroupPrincipalsByAuthority";
        const string previous = "20260712194756_EnforceSqlServerOrdinalIdentityCollation";
        using var sqlServer = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=NodePilotMigrationScriptOnly;Trusted_Connection=True")
                .Options);
        using var postgres = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseNpgsql("Host=localhost;Database=NodePilotMigrationScriptOnly;Username=nodepilot;Password=not-used")
                .Options);

        sqlServer.Database.GetService<IMigrator>().GenerateScript(latest, previous)
            .Should().Contain("authority-distinct folder grants would collide")
            .And.Contain("THROW 51000");
        postgres.Database.GetService<IMigrator>().GenerateScript(latest, previous)
            .Should().Contain("authority-distinct folder grants would collide")
            .And.Contain("RAISE EXCEPTION");
    }

    /// <summary>
    /// Guards against someone renaming or moving the migrations folder without
    /// updating these tests — otherwise the drift scan would silently find nothing
    /// and always stay green.
    /// </summary>
    [Fact]
    public void MigrationsDirectory_IsResolvable_AndContainsKnownBaseline()
    {
        var migrationsDir = ResolveMigrationsDirectory();
        Directory.Exists(migrationsDir).Should().BeTrue($"resolved {migrationsDir}");

        var baseline = Directory.GetFiles(migrationsDir, "*_InitialBaseline.cs");
        baseline.Should().ContainSingle(
            "InitialBaseline ist der Startpunkt aller Migrations — wenn die fehlt, " +
            "ist entweder der Pfad-Resolver kaputt oder der Migration-Ordner umgezogen");
    }

    /// <summary>
    /// The keyset index for the notification event poller (ExecutionEventCollector) must be
    /// emitted as a covering <c>(CompletedAt, Id) INCLUDE (Status)</c> index on BOTH providers.
    /// The migration is generated under whichever provider is active, so the *other* provider's
    /// Include annotation is added by hand — this guards it from being dropped.
    /// </summary>
    [Fact]
    public void CompletedAtKeysetIndex_IsCovering_OnBothProviders()
    {
        using var sqlServer = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=NodePilotMigrationScriptOnly;Trusted_Connection=True")
                .Options);
        using var postgres = new NodePilotDbContext(
            new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseNpgsql("Host=localhost;Database=NodePilotMigrationScriptOnly;Username=nodepilot;Password=not-used")
                .Options);

        var sqlServerScript = sqlServer.Database.GetService<IMigrator>().GenerateScript(
            fromMigration: Migration.InitialDatabase,
            toMigration: sqlServer.Database.GetMigrations().Last());
        var postgresScript = postgres.Database.GetService<IMigrator>().GenerateScript(
            fromMigration: Migration.InitialDatabase,
            toMigration: postgres.Database.GetMigrations().Last());

        sqlServerScript.Should().Contain("IX_WorkflowExecutions_CompletedAt_Id")
            .And.Contain("([CompletedAt], [Id]) INCLUDE ([Status])");
        postgresScript.Should().Contain("IX_WorkflowExecutions_CompletedAt_Id")
            .And.Contain("(\"CompletedAt\", \"Id\") INCLUDE (\"Status\")");
    }

    /// <summary>
    /// Every migration must be discoverable by EF: it needs both a <c>[Migration]</c> id and a
    /// <c>[DbContext]</c> attribute, or <c>Migrate()</c> silently skips it and the schema drifts
    /// (the bug documented in AddNotificationRouteConditionExpression). The single deliberate
    /// exception is the intentionally-invisible <c>RemoveUserWorkflowFolders</c>; its cleanup is
    /// now done by the attributed <c>DropOrphanedUserFolderTables</c> migration instead.
    /// </summary>
    [Fact]
    public void EveryMigration_IsDiscoverable_ExceptTheIntentionallyInvisibleOne()
    {
        var migrationTypes = typeof(NodePilotDbContext).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Migration).IsAssignableFrom(t))
            .ToList();

        migrationTypes.Should().NotBeEmpty("the migrations assembly must contain migration classes");

        var undiscoverable = migrationTypes
            .Where(t => t.GetCustomAttribute<MigrationAttribute>() is null
                     || t.GetCustomAttribute<DbContextAttribute>() is null)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        undiscoverable.Should().Equal(new[] { "RemoveUserWorkflowFolders" },
            "only RemoveUserWorkflowFolders may lack [Migration]/[DbContext] (deliberately invisible). " +
            "A new migration missing those attributes would be silently skipped by Migrate() and drift the schema.");
    }

    /// <summary>
    /// The attributed <c>DropOrphanedUserFolderTables</c> migration must actually remove the two
    /// orphaned personal-folders tables that <c>InitialBaseline</c> still creates.
    /// </summary>
    [Fact]
    public void OrphanedFolderTables_AreDroppedByTheMigrationChain_OnSqlite()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nodepilot-orphan-{Guid.NewGuid():N}.db");
        var connStr = $"DataSource={dbPath}";
        try
        {
            using var db = NewSqliteContext(connStr);
            db.Database.Migrate();

            var conn = db.Database.GetDbConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' " +
                "AND name IN ('UserWorkflowFolders', 'WorkflowFolderAssignments')";
            var found = new List<string>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read()) found.Add(reader.GetString(0));
            }

            found.Should().BeEmpty("the orphaned folder tables must be dropped by the migration chain");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    private static NodePilotDbContext NewSqliteContext(string connStr)
    {
        var options = new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connStr)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new NodePilotDbContext(options);
    }

    /// <summary>
    /// Walks upward from the test assembly directory until it finds a folder that
    /// looks like the repo root (<c>.slnx</c>, <c>.sln</c>, or <c>.git</c>), then
    /// builds the path to <c>src/NodePilot.Data/Migrations</c>. Robust against build
    /// configuration changes (Debug/Release) and test-runner working-directory
    /// quirks — deliberately uses the assembly directory instead of the current
    /// directory.
    /// </summary>
    private static string ResolveMigrationsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !LooksLikeRepoRoot(dir))
        {
            dir = dir.Parent;
        }
        if (dir is null)
            throw new InvalidOperationException(
                "Konnte Repo-Root (Ordner mit *.slnx/*.sln/.git) nicht finden, ausgehend von " +
                AppContext.BaseDirectory);

        return Path.Combine(dir.FullName, "src", "NodePilot.Data", "Migrations");
    }

    private static bool LooksLikeRepoRoot(DirectoryInfo dir)
        => dir.GetFiles("*.slnx").Length > 0
        || dir.GetFiles("*.sln").Length > 0
        || dir.GetDirectories(".git").Length > 0;
}

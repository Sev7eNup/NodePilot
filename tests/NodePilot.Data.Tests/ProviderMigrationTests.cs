using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests;

[Trait("Category", "DatabaseIntegration")]
public sealed class ProviderMigrationTests
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public async Task Baseline_CreatesCurrentSchema_AndRoundTrips_OnRealProvider(string provider)
    {
        Assert.SkipUnless(ProviderTestDatabase.IsConfigured(provider), $"No {provider} test server configured.");
        var ct = TestContext.Current.CancellationToken;
        await using var database = await ProviderTestDatabase.CreateAsync(provider, ct: ct);
        await using var db = database.CreateContext();
        var migrations = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        migrations.Should().Contain("20260915180058_InitialBaseline");
        await AssertSchemaAsync(db, provider, ct);
        await AssertIndexAsync(db, provider, 1, ct);

        var migrator = db.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(Migration.InitialDatabase, ct);
        (await db.Database.GetAppliedMigrationsAsync(ct)).Should().BeEmpty();
        await AssertIndexAsync(db, provider, 0, ct);

        await migrator.MigrateAsync(migrations[^1], ct);
        await AssertSchemaAsync(db, provider, ct);
        await AssertIndexAsync(db, provider, 1, ct);
        (await db.Database.GetAppliedMigrationsAsync(ct)).Should().Equal(migrations);
    }

    private static async Task AssertSchemaAsync(NodePilot.Data.NodePilotDbContext db,
        string provider, CancellationToken ct)
    {
        var sql = provider == "postgres"
            ? """
              SELECT table_name || '.' || column_name AS "Value" FROM information_schema.columns
              WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
              """
            : """
              SELECT TABLE_NAME + '.' + COLUMN_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS
              WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME <> '__EFMigrationsHistory'
              """;
        var expected = db.Model.GetEntityTypes().SelectMany(entity =>
            entity.GetProperties().Select(property => entity.GetTableName() + "." +
                property.GetColumnName(StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema()))));
        (await db.Database.SqlQueryRaw<string>(sql).ToListAsync(ct)).Should().BeEquivalentTo(expected.Distinct());
        (await db.SharedWorkflowFolders.AsNoTracking().SingleAsync(ct)).Id.Should().Be(SharedWorkflowFolder.RootFolderId);
        (await db.GlobalVariableFolders.AsNoTracking().SingleAsync(ct)).Id.Should().Be(GlobalVariableFolder.RootFolderId);

        if (provider == "sqlserver")
        {
            var exactColumns = await db.Database.SqlQueryRaw<string>("""
                SELECT OBJECT_NAME(object_id) + '.' + name AS [Value] FROM sys.columns
                WHERE collation_name = 'Latin1_General_100_BIN2'
                """).ToListAsync(ct);
            exactColumns.Should().BeEquivalentTo(
                "ExternalIdentities.Authority", "ExternalIdentities.Subject",
                "ScimGroups.Authority", "ScimGroups.ExternalId",
                "DirectoryMemberships.Authority", "DirectoryMemberships.GroupKey",
                "SharedFolderPermissions.PrincipalAuthority", "SharedFolderPermissions.PrincipalKey");
        }
    }

    private static async Task AssertIndexAsync(NodePilot.Data.NodePilotDbContext db,
        string provider, int expected, CancellationToken ct)
    {
        var sql = provider == "postgres"
            ? """
              SELECT count(*)::int AS "Value" FROM pg_indexes
              WHERE schemaname = 'public' AND tablename = 'StepExecutions'
                AND indexname = 'IX_StepExecutions_Running'
                AND indexdef LIKE '%WHERE%' AND indexdef LIKE '%Running%'
              """
            : """
              SELECT count(*) AS [Value] FROM sys.indexes
              WHERE object_id = OBJECT_ID(N'dbo.StepExecutions')
                AND name = N'IX_StepExecutions_Running'
                AND has_filter = 1 AND filter_definition LIKE N'%Running%'
              """;
        (await db.Database.SqlQueryRaw<int>(sql).SingleAsync(ct)).Should().Be(expected);
    }
}

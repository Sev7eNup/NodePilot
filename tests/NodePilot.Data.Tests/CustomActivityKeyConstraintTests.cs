using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.TestCommons;
using Npgsql;
using Xunit;

namespace NodePilot.Data.Tests;

public class CustomActivityKeyConstraintTests
{
    [Theory]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    [InlineData("sqlite")]
    public void FreshSchemaAndMigration_EnforceOnlyLiveKeys(string provider)
    {
        var options = new DbContextOptionsBuilder<NodePilotDbContext>();
        if (provider == "postgres") options.UseNpgsql("Host=localhost;Database=unused");
        else if (provider == "sqlserver") options.UseSqlServer("Server=unused;Database=unused");
        else options.UseSqlite("DataSource=:memory:");
        using var db = new NodePilotDbContext(options.Options);
        var expectedFilter = provider == "postgres" ? "\"IsDeleted\" = FALSE" : "\"IsDeleted\" = 0";
        var create = db.Database.GenerateCreateScript();
        var migrate = db.GetService<IMigrator>().GenerateScript(
            "20261009105535_PreserveCustomActivityExecutionContract", db.Database.GetMigrations().Last());
        foreach (var script in new[] { create, migrate })
            script.Should().Contain("CREATE UNIQUE INDEX").And.Contain(CustomActivityKeyConstraint.IndexName)
                .And.Contain("WHERE " + expectedFilter);
    }

    [Fact]
    public async Task DeletedDefinitions_CanRetainDuplicateKeysWhileLiveKeyIsUnique()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var input = new CustomActivityDefinitionInput { Key = "same", Name = "same", ScriptTemplate = "1" };
        for (var i = 0; i < 2; i++)
        {
            var old = await store.CreateAsync(input, "admin", ct);
            await store.SoftDeleteAsync(old.Id, old.ConcurrencyToken, ct);
        }
        await store.CreateAsync(input, "admin", ct);
        (await db.CustomActivityDefinitions.CountAsync(ct)).Should().Be(3);
        var duplicate = () => store.CreateAsync(input, "admin", ct);
        await duplicate.Should().ThrowAsync<CustomActivityDuplicateKeyException>();
    }

    [Fact]
    public async Task Migration_WithExistingDuplicates_FailsWithDiagnosticWithoutChangingDefinitions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection).ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261009105535_PreserveCustomActivityExecutionContract", ct);
        db.CustomActivityDefinitions.AddRange(
            new CustomActivityDefinition { Id = Guid.NewGuid(), Key = "duplicate", Name = "first", ScriptTemplate = "first" },
            new CustomActivityDefinition { Id = Guid.NewGuid(), Key = "duplicate", Name = "second", ScriptTemplate = "second" });
        await db.SaveChangesAsync(ct);

        var migrate = () => MigrationBootstrapper.Bootstrap(db, NullLogger.Instance);
        migrate.Should().Throw<InvalidOperationException>()
            .WithMessage("*duplicate keys*No definitions were deleted or renamed*read-only*");
        db.ChangeTracker.Clear();
        (await db.CustomActivityDefinitions.OrderBy(x => x.Name).Select(x => x.ScriptTemplate).ToListAsync(ct))
            .Should().Equal("first", "second");
        db.Database.GetAppliedMigrations().Should().NotContain("20261009112618_EnforceLiveCustomActivityKeys");
    }

    [Theory]
    [InlineData("UX_Other", false)]
    [InlineData(CustomActivityKeyConstraint.IndexName, true)]
    public void PostgresViolation_RecognizesOnlyTheOwnedConstraint(string constraint, bool expected)
    {
        var error = new PostgresException("duplicate", "ERROR", "ERROR", "23505", constraintName: constraint);
        CustomActivityKeyConstraint.IsViolation(new DbUpdateException("write", error)).Should().Be(expected);
    }

    [Fact]
    public void UnrelatedSqliteConstraint_IsNotTranslated()
    {
        CustomActivityKeyConstraint.IsViolation(new SqliteException(
            "UNIQUE constraint failed: Users.Username", 19, 2067)).Should().BeFalse();
    }
}

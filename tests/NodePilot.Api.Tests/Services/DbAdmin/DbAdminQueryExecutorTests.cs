using FluentAssertions;
using NodePilot.Api.Services.DbAdmin;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Services.DbAdmin;

/// <summary>
/// Direct coverage for <see cref="DbAdminQueryExecutor"/> against the in-memory SQLite backend:
/// the read path, row-value normalisation (byte[]→base64, NULL→null, passthrough scalars) and
/// the provider-name fallback branch. Complements <c>DbAdminControllerTests</c>, which drives the
/// executor through the HTTP controller for the write/confirmation flows.
/// </summary>
public class DbAdminQueryExecutorTests
{
    private static DbAdminQueryExecutor NewExecutor(NodePilotDbContext db, DbAdminOptions? options = null)
        => new(db, new StaticOptionsMonitor<DbAdminOptions>(options ?? new DbAdminOptions()));

    [Fact]
    public void Provider_ForSqliteBackend_ResolvesToSqlite()
    {
        using var db = TestDbFactory.Create();
        NewExecutor(db).Provider.Should().Be("sqlite");
    }

    [Fact]
    public void Options_ReflectsMonitorCurrentValue()
    {
        using var db = TestDbFactory.Create();
        var exec = NewExecutor(db, new DbAdminOptions { QueryMaxRows = 1234, AllowWriteQueries = true });
        exec.Options.QueryMaxRows.Should().Be(1234);
        exec.Options.AllowWriteQueries.Should().BeTrue();
    }

    [Fact]
    public void ResolveProvider_UnknownName_ReturnsRawName()
    {
        // Neither Npgsql/SqlServer/Sqlite — the fallback returns the raw provider string.
        DbAdminQueryExecutor.ResolveProvider("Contoso.Custom.Provider").Should().Be("Contoso.Custom.Provider");
    }

    [Fact]
    public async Task ExecuteReadAsync_SelectScalars_ReturnsColumnsWithFriendlyTypes()
    {
        using var db = TestDbFactory.Create();
        var exec = NewExecutor(db);

        var result = await exec.ExecuteReadAsync("SELECT 'hi' AS s, 42 AS n, 3.5 AS d", CancellationToken.None);

        result.Mode.Should().Be("read");
        result.RowsAffected.Should().BeNull("read mode never surfaces a records-affected count");
        result.Truncated.Should().BeFalse();
        result.Columns.Select(c => c.Name).Should().Equal("s", "n", "d");
        result.Columns.Select(c => c.Type).Should().Equal("string", "long", "double");

        var row = result.Rows.Should().ContainSingle().Subject;
        row[0].Should().Be("hi");
        row[1].Should().Be(42L);
        row[2].Should().Be(3.5d);
    }

    [Fact]
    public async Task ExecuteReadAsync_NullColumn_NormalisesToNull()
    {
        using var db = TestDbFactory.Create();
        var exec = NewExecutor(db);

        var result = await exec.ExecuteReadAsync("SELECT NULL AS empty", CancellationToken.None);

        result.Rows.Should().ContainSingle();
        result.Rows[0][0].Should().BeNull();
    }

    [Fact]
    public async Task ExecuteReadAsync_ByteArrayColumn_NormalisesToBase64_AndTypeIsBytes()
    {
        using var db = TestDbFactory.Create();
        db.Credentials.Add(new Credential
        {
            Id = Guid.NewGuid(), Name = "cred", Username = "u", EncryptedPassword = [1, 2, 3, 4],
        });
        await db.SaveChangesAsync();

        var exec = NewExecutor(db);
        var result = await exec.ExecuteReadAsync("SELECT EncryptedPassword FROM Credentials", CancellationToken.None);

        result.Columns.Should().ContainSingle().Which.Type.Should().Be("bytes");
        result.Rows.Should().ContainSingle();
        result.Rows[0][0].Should().Be(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public async Task ExecuteReadAsync_EmptyResultSet_ReturnsNoRows()
    {
        using var db = TestDbFactory.Create();
        var exec = NewExecutor(db);

        var result = await exec.ExecuteReadAsync("SELECT Name FROM Workflows WHERE 1 = 0", CancellationToken.None);

        result.Rows.Should().BeEmpty();
        result.Columns.Should().ContainSingle().Which.Name.Should().Be("Name");
    }

    [Fact]
    public async Task ExecuteReadAsync_MoreRowsThanCap_TruncatesAndFlags()
    {
        using var db = TestDbFactory.Create();
        for (var i = 0; i < 4; i++)
            db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = $"wf{i}", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var exec = NewExecutor(db, new DbAdminOptions { QueryMaxRows = 2 });
        var result = await exec.ExecuteReadAsync("SELECT Name FROM Workflows", CancellationToken.None);

        result.Rows.Should().HaveCount(2);
        result.Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteReadAsync_RejectsMutatingStatementAtExecutorBoundary()
    {
        using var db = TestDbFactory.Create();
        var exec = NewExecutor(db);

        var act = () => exec.ExecuteReadAsync(
            "INSERT INTO Workflows (Id, Name, DefinitionJson, Version, IsEnabled, CreatedAt, UpdatedAt, ActivityCount, FolderId) " +
            "VALUES ('11111111-1111-1111-1111-111111111111', 'ghost', '{}', 1, 0, '2026-01-01', '2026-01-01', 0, '00000000-0000-0000-0000-000000000001')",
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*INSERT*not allowed*");
        db.Workflows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM Workflows")]
    [InlineData("SELECT 1 INTO NewTable")]
    [InlineData("EXEC xp_cmdshell 'whoami'")]
    [InlineData("SELECT pg_sleep(5)")]
    [InlineData("EXPLAIN ANALYZE SELECT 1")]
    public async Task ExecuteReadAsync_RejectsDangerousSingleStatements(string sql)
    {
        using var db = TestDbFactory.Create();
        var act = () => NewExecutor(db).ExecuteReadAsync(sql, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExecuteReadAsync_DangerousWordsInsideLiteralsAndComments_AreIgnored()
    {
        using var db = TestDbFactory.Create();
        var result = await NewExecutor(db).ExecuteReadAsync(
            "SELECT 'DELETE INTO pg_sleep' AS text /* EXEC xp_cmdshell */",
            CancellationToken.None);
        result.Rows.Should().ContainSingle();
    }
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Api.Ai;
using NodePilot.Api.Services.DbAdmin;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Ai;

/// <summary>
/// Direct coverage for the text2sql reader's redaction contract: secret columns named in the
/// schema (<c>User.PasswordHash</c>, <c>Credential.EncryptedPassword</c>) are masked to <c>"***"</c>
/// by result-column name, the masked-by-name <c>GlobalVariable.Value</c> too, every other cell runs
/// through the redactor, rows are capped, and SQL errors surface as <c>Error</c> instead of throwing.
/// Uses the same in-memory SQLite backend as <c>DbAdminQueryExecutorTests</c>.
/// </summary>
public class SqlKnowledgeReaderTests
{
    private sealed class FakeScopeFactory : IServiceScopeFactory
    {
        private readonly NodePilotDbContext _db;
        public FakeScopeFactory(NodePilotDbContext db) => _db = db;
        public IServiceScope CreateScope() => new Scope(_db);
        private sealed class Scope : IServiceScope
        {
            public Scope(NodePilotDbContext db) => ServiceProvider = new Provider(db);
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() { }
        }
        private sealed class Provider : IServiceProvider
        {
            private readonly NodePilotDbContext _db;
            public Provider(NodePilotDbContext db) => _db = db;
            public object? GetService(Type serviceType) => serviceType == typeof(NodePilotDbContext) ? _db : null;
        }
    }

    private static SqlKnowledgeReader NewReader(NodePilotDbContext db)
    {
        var metadata = new DbAdminMetadataService(new FakeScopeFactory(db));
        var executor = new DbAdminQueryExecutor(db, new StaticOptionsMonitor<DbAdminOptions>(new DbAdminOptions()));
        var redactor = new OutputRedactor(null);
        return new SqlKnowledgeReader(metadata, executor, redactor, new DbAdminSecretColumns(metadata));
    }

    [Fact]
    public async Task ListTables_OmitsHiddenColumns_ListsDbTableName()
    {
        using var db = TestDbFactory.Create();
        var reader = NewReader(db);
        var tables = await reader.ListTablesAsync(CancellationToken.None);
        var user = tables.Single(t => t.Name == "User");
        user.DbTableName.Should().Be("Users");
        user.ColumnNames.Should().NotContain("PasswordHash"); // hidden
        user.ColumnNames.Should().Contain("Username");
    }

    [Fact]
    public async Task GetTable_OmitsHiddenSecretColumns()
    {
        using var db = TestDbFactory.Create();
        var reader = NewReader(db);
        var detail = await reader.GetTableAsync("User", CancellationToken.None);
        detail.Should().NotBeNull();
        detail!.Columns.Select(c => c.Name).Should().NotContain("PasswordHash");
    }

    [Fact]
    public async Task ExecuteRead_RejectsDirectPasswordHashReference()
    {
        using var db = TestDbFactory.Create();
        db.Users.Add(new User { Username = "admin", PasswordHash = "SUPER_SECRET_HASH" });
        await db.SaveChangesAsync();

        var reader = NewReader(db);
        var result = await reader.ExecuteReadAsync("SELECT Username, PasswordHash FROM Users", CancellationToken.None);

        result.Error.Should().Be("Query references a protected column.");
        result.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("SELECT PasswordHash AS x FROM Users")]
    [InlineData("SELECT substr(PasswordHash, 1, 4) AS prefix FROM Users")]
    [InlineData("SELECT \"PasswordHash\" AS x FROM Users")]
    public async Task ExecuteRead_RejectsProtectedColumnReferencesBeforeExecution(string sql)
    {
        using var db = TestDbFactory.Create();
        db.Users.Add(new User { Username = "admin", PasswordHash = "SUPER_SECRET_HASH" });
        await db.SaveChangesAsync();

        var result = await NewReader(db).ExecuteReadAsync(sql, CancellationToken.None);

        result.Error.Should().Be("Query references a protected column.");
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteRead_MasksGlobalVariableValueColumn_ToStars()
    {
        using var db = TestDbFactory.Create();
        db.GlobalVariables.Add(new GlobalVariable { Name = "api-key", Value = "sk-live-123" });
        await db.SaveChangesAsync();

        var reader = NewReader(db);
        var result = await reader.ExecuteReadAsync("SELECT Name, Value FROM GlobalVariables", CancellationToken.None);
        result.Error.Should().Be("Query references a protected column.");
        result.Rows.Should().BeEmpty();
    }

    /// <summary>
    /// Security audit 2026-07-26: both older layers key on NAMES, so a whole-row serializer slipped
    /// past both at once — <c>to_json(u)</c> never mentions <c>PasswordHash</c> and returns it in a
    /// column called <c>to_json</c>. Rejection happens before execution, which is what lets this
    /// test assert the contract against SQLite (which has no <c>to_json</c> at all).
    /// </summary>
    [Theory]
    [InlineData("SELECT to_json(u) FROM Users u")]
    [InlineData("SELECT row_to_json(u) FROM \"Users\" u")]
    [InlineData("SELECT to_jsonb(u) FROM Users u")]
    [InlineData("SELECT u::text FROM Users u")]
    [InlineData("SELECT json_agg(u) FROM Users u")]
    [InlineData("SELECT to_json(c) FROM Credentials c")]
    [InlineData("SELECT to_json(g) FROM GlobalVariables g")]
    [InlineData("SELECT * FROM Users FOR JSON AUTO")]
    [InlineData("SELECT * FROM Users FOR XML AUTO")]
    [InlineData("WITH x AS (SELECT * FROM Users) SELECT to_json(x) FROM x")]
    public async Task ExecuteRead_RejectsWholeRowProjectionOverProtectedTable(string sql)
    {
        using var db = TestDbFactory.Create();
        db.Users.Add(new User { Username = "admin", PasswordHash = "SUPER_SECRET_HASH" });
        await db.SaveChangesAsync();

        var result = await NewReader(db).ExecuteReadAsync(sql, CancellationToken.None);

        result.Error.Should().Contain("serializes a whole row");
        result.Rows.Should().BeEmpty();
    }

    /// <summary>
    /// The row-projection guard is blunt by design, so it must stay scoped to tables that actually
    /// hold a masked column — otherwise it would break ordinary analysis on the ~34 tables that
    /// hold no secret.
    /// </summary>
    [Theory]
    [InlineData("SELECT to_json(w) FROM Workflows w")]
    [InlineData("SELECT w::text FROM Workflows w")]
    public async Task ExecuteRead_AllowsRowProjectionOverTableWithoutSecrets(string sql)
    {
        using var db = TestDbFactory.Create();

        var result = await NewReader(db).ExecuteReadAsync(sql, CancellationToken.None);

        // SQLite has neither to_json nor ::, so the statement still fails — but it must fail at the
        // database, not at the guard. Anything else would mean the guard fires on tables that hold
        // no secret at all.
        result.Error.Should().NotBeNull();
        result.Error.Should().NotContain("serializes a whole row");
    }

    [Fact]
    public async Task ExecuteRead_AllowsExplicitColumnListOnProtectedTable()
    {
        using var db = TestDbFactory.Create();
        db.Users.Add(new User { Username = "admin", PasswordHash = "SUPER_SECRET_HASH" });
        await db.SaveChangesAsync();

        var result = await NewReader(db).ExecuteReadAsync(
            "SELECT Username FROM Users", CancellationToken.None);

        result.Error.Should().BeNull();
        result.Rows.Should().ContainSingle();
        result.Rows[0][0].Should().Be("admin");
    }

    [Fact]
    public async Task ExecuteRead_BadSql_SurfacesErrorWithoutThrowing()
    {
        using var db = TestDbFactory.Create();
        var reader = NewReader(db);
        var result = await reader.ExecuteReadAsync("SELEC * FROM NoSuchTable", CancellationToken.None);
        result.Error.Should().NotBeNullOrEmpty();
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTable_IncludesForeignKeysAndProviderContext()
    {
        using var db = TestDbFactory.Create();
        var reader = NewReader(db);

        var detail = await reader.GetTableAsync("WorkflowExecution", CancellationToken.None);

        reader.Provider.Should().Be("sqlite");
        detail.Should().NotBeNull();
        detail!.ForeignKeys.Should().Contain(fk =>
            fk.Columns.Contains("WorkflowId")
            && fk.PrincipalTable == "Workflows"
            && fk.PrincipalColumns.Contains("Id"));
    }

    [Fact]
    public async Task ExecuteRead_MultiStatement_SurfacesError()
    {
        using var db = TestDbFactory.Create();
        var reader = NewReader(db);
        // The executor rejects multi-statement input — the reader turns that into Error, not an exception.
        var result = await reader.ExecuteReadAsync("SELECT 1; SELECT 2", CancellationToken.None);
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExecuteRead_CapsRowsAtLimit_AndFlagsTruncated()
    {
        using var db = TestDbFactory.Create();
        for (var i = 0; i < 250; i++)
            db.GlobalVariables.Add(new GlobalVariable { Name = $"g{i}", Value = "v" });
        await db.SaveChangesAsync();

        var reader = NewReader(db);
        var result = await reader.ExecuteReadAsync("SELECT Name FROM GlobalVariables", CancellationToken.None);
        result.Error.Should().BeNull();
        result.Truncated.Should().BeTrue();
        result.Rows.Count.Should().Be(200);
    }
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Api.Ai;
using NodePilot.Api.Services.DbAdmin;
using NodePilot.Core.Interfaces;
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
            private readonly NodePilotDbContext _db;
            public Scope(NodePilotDbContext db) { _db = db; ServiceProvider = new Provider(db); }
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
        return new SqlKnowledgeReader(metadata, executor, redactor);
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

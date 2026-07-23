using System.Net;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests;

/// <summary>text2sql read-only MCP tools: schema discovery + read-only SQL execution.</summary>
public sealed class DbAdminMcpToolsTests
{
    private static readonly object[] SampleTables =
    {
        new
        {
            name = "Workflow",
            displayName = "Workflow",
            dbTableName = "Workflows",
            pkColumns = new[] { "Id" },
            capabilities = new { canUpdate = true, canDelete = false },
            columns = new object[]
            {
                new { name = "Id", clrType = "Guid", isNullable = false, maxLength = (int?)null, isPrimaryKey = true, isMasked = false, isReadOnly = true },
                new { name = "Name", clrType = "string", isNullable = false, maxLength = (int?)200, isPrimaryKey = false, isMasked = false, isReadOnly = false },
            },
            rowCount = 12L,
            cascadeDeletesTo = Array.Empty<string>(),
        },
        new
        {
            name = "GlobalVariable",
            displayName = "Global Variable",
            dbTableName = "GlobalVariables",
            pkColumns = new[] { "Id" },
            capabilities = new { canUpdate = true, canDelete = true },
            columns = new object[]
            {
                new { name = "Id", clrType = "Guid", isNullable = false, maxLength = (int?)null, isPrimaryKey = true, isMasked = false, isReadOnly = true },
                new { name = "Value", clrType = "string", isNullable = true, maxLength = (int?)null, isPrimaryKey = false, isMasked = true, isReadOnly = false },
            },
            rowCount = 3L,
            cascadeDeletesTo = Array.Empty<string>(),
        },
    };

    [Fact]
    public async Task ListDbTables_ReturnsCompactSchema_WithoutSecretColumns()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/dbadmin/tables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(SampleTables));

        var tools = new DbAdminMcpTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListDbTables());

        json.Should().Contain("\"name\":\"Workflow\"");
        json.Should().Contain("\"isMasked\":true");          // GlobalVariable.Value masked flag carried through
        json.Should().NotContain("capabilities");            // capabilities/cascade dropped for token efficiency
        json.Should().NotContain("cascadeDeletesTo");
        json.Should().NotContain("PasswordHash");            // hidden columns never present from API
    }

    [Fact]
    public async Task ListDbTables_NameFilter_IsCaseInsensitiveSubstring()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/dbadmin/tables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(SampleTables));

        var tools = new DbAdminMcpTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.ListDbTables(name: "global"));

        json.Should().Contain("GlobalVariable");
        json.Should().NotContain("\"name\":\"Workflow\"");
    }

    [Fact]
    public async Task GetDbInfo_ReturnsProviderAndLimits()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/dbadmin/info").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                provider = "postgres", allowWriteQueries = false, queryTimeoutSeconds = 30, queryMaxRows = 10000,
            }));

        var tools = new DbAdminMcpTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.GetDbInfo());

        json.Should().Contain("\"provider\":\"postgres\"");
        json.Should().Contain("\"queryMaxRows\":10000");
        json.Should().Contain("run_readonly_sql only accepts read statements");
    }

    [Fact]
    public async Task RunReadonlySql_SendsReadMode_AndReturnsRows()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                columns = new[] { new { name = "x", type = "int" } },
                rows = new object[] { new object[] { 1 }, new object[] { 2 } },
                rowsAffected = (int?)null,
                durationMs = 7L,
                truncated = false,
                mode = "read",
            }));

        var tools = new DbAdminMcpTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.RunReadonlySql("SELECT 1 AS x"));

        // The body sent to the API forced mode=read.
        var body = api.Server.LogEntries.Last().RequestMessage.Body;
        body.Should().Contain("\"mode\":\"read\"");
        body.Should().Contain("SELECT 1 AS x");

        json.Should().Contain("\"rowCount\":2");
        json.Should().Contain("\"truncated\":false");
        json.Should().NotContain("\"note\":null"); // success path omits a note
    }

    [Fact]
    public async Task RunReadonlySql_CapsRowsAt200_AndSetsTruncated()
    {
        using var api = new TestApi();
        var bigRows = Enumerable.Range(0, 250).Select(i => (object)new object[] { i }).ToArray();
        api.Server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                columns = new[] { new { name = "n", type = "int" } },
                rows = bigRows,
                rowsAffected = (int?)null,
                durationMs = 10L,
                truncated = false,
                mode = "read",
            }));

        var tools = new DbAdminMcpTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.RunReadonlySql("SELECT n FROM t"));

        json.Should().Contain("\"rowCount\":200");
        json.Should().Contain("\"truncated\":true");
    }

    [Fact]
    public async Task RunReadonlySql_PassesServerTruncatedFlagThrough()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                columns = new[] { new { name = "x", type = "int" } },
                rows = new object[] { new object[] { 1 } },
                rowsAffected = (int?)null,
                durationMs = 1L,
                truncated = true,
                mode = "read",
            }));

        var tools = new DbAdminMcpTools(api.Client());
        var json = JsonSerializer.Serialize(await tools.RunReadonlySql("SELECT x FROM t"));

        json.Should().Contain("\"truncated\":true");
    }

    [Fact]
    public async Task RunReadonlySql_ApiError_MapsToApiException()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403).WithBodyAsJson(new
            {
                title = "Forbidden", detail = "Admin role required",
            }));

        var tools = new DbAdminMcpTools(api.Client());
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.RunReadonlySql("SELECT 1"));
        ex.Message.Should().Contain("Permission denied");
    }

    [Fact]
    public async Task ListDbTables_ApiError_MapsToApiException()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/dbadmin/tables").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401).WithBodyAsJson(new { title = "Unauthorized", detail = "token" }));

        var tools = new DbAdminMcpTools(api.Client());
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.ListDbTables());
        ex.Message.Should().Contain("np auth login");
    }
}
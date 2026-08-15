using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests.Tools;

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
                new { name = "DefinitionJson", clrType = "string", isNullable = false, maxLength = (int?)null, isPrimaryKey = false, isMasked = false, isReadOnly = true },
            },
            rowCount = 12L,
            cascadeDeletesTo = Array.Empty<string>(),
        },
        new
        {
            name = "CustomActivityDefinition",
            displayName = "Custom Activity Definition",
            dbTableName = "CustomActivityDefinitions",
            pkColumns = new[] { "Id" },
            capabilities = new { canUpdate = true, canDelete = false },
            columns = new object[]
            {
                new { name = "Id", clrType = "Guid", isNullable = false, maxLength = (int?)null, isPrimaryKey = true, isMasked = false, isReadOnly = true },
                new { name = "Name", clrType = "string", isNullable = false, maxLength = (int?)200, isPrimaryKey = false, isMasked = false, isReadOnly = false },
                new { name = "ScriptTemplate", clrType = "string", isNullable = false, maxLength = (int?)null, isPrimaryKey = false, isMasked = false, isReadOnly = false },
                new { name = "InputParametersJson", clrType = "string", isNullable = false, maxLength = (int?)null, isPrimaryKey = false, isMasked = false, isReadOnly = false },
            },
            rowCount = 2L,
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

        json.Should().NotContain("\"name\":\"Workflow\"");
        json.Should().NotContain("CustomActivityDefinition");
        json.Should().Contain("\"isMasked\":true");          // GlobalVariable.Value masked flag carried through
        json.Should().NotContain("capabilities");            // capabilities/cascade dropped for token efficiency
        json.Should().NotContain("cascadeDeletesTo");
        json.Should().NotContain("PasswordHash");            // hidden columns never present from API
        json.Should().NotContain("DefinitionJson");          // raw DbAdmin schema is filtered at MCP boundary
        json.Should().NotContain("ScriptTemplate");
        json.Should().NotContain("InputParametersJson");
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
        var body = api.Server.LogEntries.Last().RequestMessage!.Body;
        body.Should().Contain("\"mode\":\"read\"");
        body.Should().Contain("SELECT 1 AS x");

        json.Should().Contain("\"rowCount\":2");
        json.Should().Contain("\"truncated\":false");
        json.Should().NotContain("\"note\":null"); // success path omits a note
    }

    [Theory]
    [InlineData("SELECT DefinitionJson FROM Workflows")]
    [InlineData("SELECT w.DefinitionJson AS payload FROM Workflows w")]
    [InlineData("SELECT * FROM WorkflowVersions")]
    [InlineData("SELECT CAST(w AS text) FROM Workflows w")]
    [InlineData("SELECT array_to_json(array_agg(w)) FROM Workflows w")]
    [InlineData("SELECT Id, Name FROM Workflows")]
    [InlineData("SELECT leak FROM Workflows w CROSS JOIN LATERAL regexp_split_to_table(CAST(w AS text), 'NEVER_MATCH') AS leak")]
    [InlineData("SELECT ScriptTemplate FROM CustomActivityDefinitions")]
    [InlineData("SELECT substr(InputParametersJson, 1, 10) FROM CustomActivityDefinitionVersions")]
    [InlineData("SELECT query_to_xml('SELECT \"DefinitionJson\" FROM \"Workflows\"', false, true, '')")]
    [InlineData("SELECT U&\"Definiti\\006FnJson\" AS payload FROM U&\"Workfl\\006Fws\"")]
    public async Task RunReadonlySql_RejectsOpaqueAutomationPayloadBeforeApiCall(string sql)
    {
        using var api = new TestApi();
        var tools = new DbAdminMcpTools(api.Client());

        var ex = await Assert.ThrowsAsync<McpException>(() => tools.RunReadonlySql(sql));

        ex.Message.Should().Contain("workflow definition or custom activity implementation");
        api.Server.LogEntries.Should().BeEmpty("rejected agent SQL must never reach raw DbAdmin");
    }

    [Fact]
    public async Task RunReadonlySql_MasksOpaqueResultColumnNames_AsDefenseInDepth()
    {
        using var api = new TestApi();
        api.Server.Given(Request.Create().WithPath("/api/dbadmin/query").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                columns = new[]
                {
                    new { name = "Name", type = "string" },
                    new { name = "DefinitionJson", type = "string" },
                    new { name = "ScriptTemplate", type = "string" },
                    new { name = "InputParametersJson", type = "string" },
                },
                rows = new object[]
                {
                    new object[] { "safe-name", "definition-canary-741", "script-canary-852", "defaults-canary-963" },
                },
                rowsAffected = (int?)null,
                durationMs = 2L,
                truncated = false,
                mode = "read",
            }));

        var tools = new DbAdminMcpTools(api.Client());
        var json = JsonSerializer.Serialize(
            await tools.RunReadonlySql("SELECT * FROM AgentSafeProjection"));

        json.Should().Contain("safe-name");
        json.Should().Contain("***");
        json.Should().NotContain("definition-canary-741");
        json.Should().NotContain("script-canary-852");
        json.Should().NotContain("defaults-canary-963");
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

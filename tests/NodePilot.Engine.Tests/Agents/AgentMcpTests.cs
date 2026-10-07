using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.Engine.Agents;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentMcpTests
{
    [Fact]
    public async Task StreamableHttp_InitializesListsAndCallsTools_WithHostOwnedAuthentication()
    {
        await using var db = TestDbFactory.Create();
        var handler = new McpHandler();
        using var http = new HttpClient(handler);
        var id = Guid.NewGuid();
        db.AgentMcpServers.Add(new AgentMcpServer { Id = id, Name = "HTTP fixture", Transport = "streamableHttp", Endpoint = "https://mcp.example.test/mcp",
            ProtectedSecrets = Encoding.UTF8.GetBytes("{\"Authorization\":\"Bearer host-secret\"}") });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var factory = Factory(db, http);
        await using var client = await factory.ConnectAsync(id, TestContext.Current.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(tools); Assert.Equal("echo", tools[0].Name);
        var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = "evidence" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("evidence", JsonSerializer.Serialize(result));
        Assert.All(handler.Authorizations, value => Assert.Equal("Bearer host-secret", value));
        Assert.DoesNotContain("host-secret", JsonSerializer.Serialize(tools.Select(t => t.JsonSchema)));
        db.AgentMcpServers.Single().Enabled = false;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => factory.EnsureEnabledAsync(id, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(503)]
    public async Task StreamableHttp_ConnectionFailureDoesNotProduceAnUsableClient(int status)
    {
        await using var db = TestDbFactory.Create();
        using var http = new HttpClient(new McpHandler { Failure = status });
        var id = Guid.NewGuid();
        db.AgentMcpServers.Add(new AgentMcpServer { Id = id, Name = "Failing server", Transport = "streamableHttp", Endpoint = "https://mcp.example.test/mcp" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<Exception>(() => Factory(db, http).ConnectAsync(id, deadline.Token));
    }

    [Fact]
    public async Task Stdio_ConnectsToRealSdkServer_UsingFixedRegisteredExecutable()
    {
        await using var db = TestDbFactory.Create();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NodePilot.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = Path.Combine(root.FullName, "src", "NodePilot.Mcp", "bin", configuration, "net10.0-windows", "nodepilot-mcp.exe");
        Assert.True(File.Exists(executable), executable);
        var id = Guid.NewGuid();
        db.AgentMcpServers.Add(new AgentMcpServer { Id = id, Name = "stdio fixture", Command = executable,
            ProtectedSecrets = Encoding.UTF8.GetBytes("{\"NODEPILOT_MCP_SERVER\":\"https://127.0.0.1:1\",\"NODEPILOT_MCP_TOKEN\":\"test-token\",\"NODEPILOT_MCP_ALLOW_DESTRUCTIVE\":\"false\"}") });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var client = await Factory(db, http).ConnectAsync(id, deadline.Token);
        var tools = await client.ListToolsAsync(cancellationToken: deadline.Token);
        Assert.Contains(tools, tool => tool.Name == "get_safety_status");
        Assert.DoesNotContain(tools, tool => tool.Name == "delete_agent_skill");
        var result = await client.CallToolAsync("get_safety_status", cancellationToken: deadline.Token);
        Assert.Contains("delete_agent_skill", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task StreamableHttp_DisconnectAfterDispatch_DoesNotReplayToolRequest()
    {
        await using var db = TestDbFactory.Create();
        var handler = new McpHandler { DisconnectOnCall = true };
        using var http = new HttpClient(handler);
        var id = Guid.NewGuid();
        db.AgentMcpServers.Add(new AgentMcpServer { Id = id, Name = "Disconnect fixture", Transport = "streamableHttp", Endpoint = "https://mcp.example.test/mcp" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var client = await Factory(db, http).ConnectAsync(id, deadline.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => client.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = "action" }, cancellationToken: deadline.Token).AsTask());
        Assert.Equal(1, handler.ToolCalls);
    }

    private static AgentMcpClientFactory Factory(NodePilotDbContext db, HttpClient http)
    {
        var factory = new Mock<IHttpClientFactory>(); factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(http);
        var secrets = new Mock<ISecretProtector>(); secrets.Setup(p => p.Unprotect(It.IsAny<byte[]>())).Returns((byte[] value) => Encoding.UTF8.GetString(value));
        return new AgentMcpClientFactory(db, secrets.Object, new RestApiHttpClientProvider(factory.Object, new ConfigurationBuilder().Build()), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task AgentMcpRequiresApprovalAndRechecksRevocationSchemaAndServerRevision()
    {
        await using var db = TestDbFactory.Create();
        var handler = new McpHandler { ReadOnly = true };
        using var http = new HttpClient(handler);
        var server = new AgentMcpServer { Id = Guid.NewGuid(), Name = "reviewed reader", Transport = "streamableHttp", Endpoint = "https://mcp.example.test/mcp" };
        db.AgentMcpServers.Add(server); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var factory = Factory(db, http);
        await using var discovery = await factory.ConnectAsync(server.Id, TestContext.Current.CancellationToken);
        var tool = (await discovery.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single();
        var options = new AgentOptions(); var policy = AgentReadCallsTests.Policy(options);
        var definition = new AgentDefinition { Tools = [new() { Name = "mcp", McpServerId = server.Id, McpToolName = "echo" }] };
        var host = new AgentToolHost(null!, factory, new AgentRunDatabase(db), null!, null!, policy);
        var connections = handler.Authorizations.Count;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.OpenAsync(definition, new(), Guid.NewGuid(), null!, null!, TestContext.Current.CancellationToken));
        Assert.Equal(connections, handler.Authorizations.Count);
        var grant = new AgentMcpReadGrant { ServerId = server.Id, ToolName = "echo", ServerUpdatedAt = server.UpdatedAt.ToString("O"), ContractSha256 = AgentExternalReadPolicy.ContractFingerprint(tool) };
        options.ReadOnlyMcpTools = [grant];
        await using var session = await host.OpenAsync(definition, new(), Guid.NewGuid(), null!, null!, TestContext.Current.CancellationToken);
        var arguments = JsonSerializer.SerializeToElement(new { text = "evidence" });
        Assert.Contains("evidence", await session.Tools.Single().InvokeAsync(arguments, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.ToolCalls);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Tools.Single().InvokeAsync(JsonSerializer.SerializeToElement(new { text = 42 }), TestContext.Current.CancellationToken));
        handler.ReadOnly = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Tools.Single().InvokeAsync(arguments, TestContext.Current.CancellationToken));
        handler.ReadOnly = true; handler.Description = "Changed contract";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Tools.Single().InvokeAsync(arguments, TestContext.Current.CancellationToken));
        handler.Description = "Return text"; options.ReadOnlyMcpTools = [];
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Tools.Single().InvokeAsync(arguments, TestContext.Current.CancellationToken));
        options.ReadOnlyMcpTools = [grant]; server.UpdatedAt = server.UpdatedAt.AddSeconds(1);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Tools.Single().InvokeAsync(arguments, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.ToolCalls);
    }

    [Fact]
    public async Task McpToolError_IsAnExecutionFailure_WithoutReplay()
    {
        await using var db = TestDbFactory.Create();
        var handler = new McpHandler { ReadOnly = true, ToolError = true };
        using var http = new HttpClient(handler);
        var server = new AgentMcpServer { Id = Guid.NewGuid(), Name = "error fixture", Transport = "streamableHttp", Endpoint = "https://mcp.example.test/mcp" };
        db.AgentMcpServers.Add(server);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var factory = Factory(db, http);
        await using var discovery = await factory.ConnectAsync(server.Id, TestContext.Current.CancellationToken);
        var tool = (await discovery.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Single();
        var options = new AgentOptions { ReadOnlyMcpTools = [new() { ServerId = server.Id, ToolName = "echo", ServerUpdatedAt = server.UpdatedAt.ToString("O"), ContractSha256 = AgentExternalReadPolicy.ContractFingerprint(tool) }] };
        var host = new AgentToolHost(null!, factory, new AgentRunDatabase(db), null!, null!, AgentReadCallsTests.Policy(options));
        var definition = new AgentDefinition { Tools = [new() { Name = "mcp", McpServerId = server.Id, McpToolName = "echo" }] };
        await using var session = await host.OpenAsync(definition, new(), Guid.NewGuid(), null!, null!, TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<AgentToolExecutionException>(() => session.Tools.Single().InvokeAsync(
            JsonSerializer.SerializeToElement(new { text = "Source unavailable" }), TestContext.Current.CancellationToken));
        Assert.Equal("mcp_tool_failed", failure.Code);
        Assert.Contains("Source unavailable", failure.Message);
        Assert.Equal(1, handler.ToolCalls);
    }

    private sealed class McpHandler : HttpMessageHandler
    {
        public bool ReadOnly { get; set; }
        public string Description { get; set; } = "Return text";
        public int? Failure { get; init; }
        public bool DisconnectOnCall { get; init; }
        public bool ToolError { get; init; }
        public int ToolCalls { get; private set; }
        public List<string?> Authorizations { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (Failure is { } status) return new HttpResponseMessage((HttpStatusCode)status);
            if (request.Method != HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (!body.RootElement.TryGetProperty("id", out var id)) return new HttpResponseMessage(HttpStatusCode.Accepted);
            var method = body.RootElement.GetProperty("method").GetString();
            if (method == "tools/call")
            {
                ToolCalls++;
                if (DisconnectOnCall) throw new HttpRequestException("Connection lost after dispatch.");
            }
            if (method is not ("initialize" or "tools/list" or "tools/call"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "Method not found" } }), Encoding.UTF8, "application/json") };
            object result = method switch
            {
                "initialize" => new { protocolVersion = "2025-03-26", capabilities = new { tools = new { } }, serverInfo = new { name = "test", version = "1" } },
                "tools/list" => new { tools = new[] { new { name = "echo", description = Description, annotations = new { readOnlyHint = ReadOnly, destructiveHint = false }, inputSchema = new { type = "object", properties = new { text = new { type = "string" } } } } } },
                "tools/call" => new { content = new[] { new { type = "text", text = body.RootElement.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString() } }, isError = ToolError },
                _ => new { }
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result }), Encoding.UTF8, "application/json") };
        }
    }
}

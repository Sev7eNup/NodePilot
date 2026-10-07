using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Engine.Agents;
using NodePilot.Engine.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Engine.Tests.Agents;

public sealed class AgentReadCallsTests
{
    [Fact]
    public async Task HttpToolSchemaRejectsEmptyArgumentsBeforePermissionChecks()
    {
        await using var db = TestDbFactory.Create();
        var handler = new RecordingHttpHandler();
        var host = new AgentToolHost(null!, null!, new AgentRunDatabase(db), null!, HttpProvider(handler), Policy());
        await using var session = await host.OpenAsync(new AgentDefinition { Tools = [new() { Name = "http_request" }] },
            new(), Guid.NewGuid(), null!, null!, TestContext.Current.CancellationToken);
        var schema = NodePilot.Ai.Agents.AgentJsonSchema.Compile(session.Tools.Single().Schema);
        Assert.False(NodePilot.Ai.Agents.AgentJsonSchema.IsValid(schema, "{}"));
        Assert.False(NodePilot.Ai.Agents.AgentJsonSchema.IsValid(schema, "{\"url\":\"\",\"method\":\"\"}"));
        Assert.True(NodePilot.Ai.Agents.AgentJsonSchema.IsValid(schema, "{\"url\":\"https://example.test/status\"}"));
        Assert.Empty(handler.Methods);
    }
    [Theory]
    [InlineData("2026-09-19T12:00:00.123456Z")]
    [InlineData("2026-09-19T14:00:00.123456+02:00")]
    public void McpApprovalRevisionSurvivesConfigurationBindingWithoutLosingTimezoneOrPrecision(string revision)
    {
        var id = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReadOnlyMcpTools:0:ServerId"] = id.ToString(), ["ReadOnlyMcpTools:0:ToolName"] = "read",
            ["ReadOnlyMcpTools:0:ServerUpdatedAt"] = revision, ["ReadOnlyMcpTools:0:ContractSha256"] = new string('a', 64)
        }).Build();
        var options = config.Get<AgentOptions>()!;
        var server = new AgentMcpServer { Id = id, UpdatedAt = new DateTime(2026, 9, 19, 12, 0, 0).AddTicks(1234560) };
        Assert.Equal(revision, Policy(options).AuthorizeMcpServer(server, "read").ServerUpdatedAt);
        server.UpdatedAt = server.UpdatedAt.AddTicks(10);
        Assert.Throws<UnauthorizedAccessException>(() => Policy(options).AuthorizeMcpServer(server, "read"));
    }

    internal static AgentExternalReadPolicy Policy(AgentOptions? options = null)
    {
        var monitor = new Mock<IOptionsMonitor<AgentOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options ?? new AgentOptions());
        return new AgentExternalReadPolicy(monitor.Object);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task HttpReadsWorkWithoutAnyUrlOrHostList(string method)
    {
        await using var db = TestDbFactory.Create();
        var handler = new RecordingHttpHandler();
        var host = new AgentToolHost(null!, null!, new AgentRunDatabase(db), null!, HttpProvider(handler), Policy());
        await using var session = await host.OpenAsync(new AgentDefinition { Tools = [new() { Name = "http_request" }] },
            new StepExecutionContext(), Guid.NewGuid(), null!, null!, TestContext.Current.CancellationToken);
        var output = await session.Tools.Single().InvokeAsync(JsonSerializer.SerializeToElement(new { url = "https://example.test/status?node=1", method }), TestContext.Current.CancellationToken);
        Assert.Contains("healthy", output);
        Assert.Equal(method, Assert.Single(handler.Methods));
    }

    [Theory]
    [InlineData("POST", null)]
    [InlineData("PUT", null)]
    [InlineData("DELETE", null)]
    [InlineData("PATCH", null)]
    [InlineData("TRACE", null)]
    [InlineData("GET", "override=DELETE")]
    [InlineData("HEAD", "")]
    public async Task HttpWritesAndBodiesAreRejectedBeforeNetwork(string method, string? body)
    {
        await using var db = TestDbFactory.Create();
        var handler = new RecordingHttpHandler();
        var host = new AgentToolHost(null!, null!, new AgentRunDatabase(db), null!, HttpProvider(handler), Policy());
        await using var session = await host.OpenAsync(new AgentDefinition { Tools = [new() { Name = "http_request" }] }, new(), Guid.NewGuid(), null!, null!, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Tools.Single().InvokeAsync(
            JsonSerializer.SerializeToElement(new { url = "https://example.test/status", method, body }), TestContext.Current.CancellationToken));
        Assert.Empty(handler.Methods);
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("https://user:password@example.test/status")]
    [InlineData("https://example.test/status#fragment")]
    public void HttpRejectsAmbiguousDestinations(string url)
        => Assert.Throws<UnauthorizedAccessException>(() => Policy().AuthorizeHttp(url, "GET", null));

    [Fact]
    public async Task HttpRedirectIsNotFollowedAndExistingNetworkProtectionStillApplies()
    {
        await using var db = TestDbFactory.Create();
        var handler = new RecordingHttpHandler { Redirect = true };
        var host = new AgentToolHost(null!, null!, new AgentRunDatabase(db), null!, HttpProvider(handler), Policy());
        await using var session = await host.OpenAsync(new AgentDefinition { Tools = [new() { Name = "http_request" }] }, new(), Guid.NewGuid(), null!, null!, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Tools.Single().InvokeAsync(JsonSerializer.SerializeToElement(new { url = "https://example.test/status" }), TestContext.Current.CancellationToken));
        Assert.Single(handler.Methods);
        using var realHandler = RestApiHttpClientProvider.BuildDefaultHandler(new(), new ConfigurationBuilder().Build());
        Assert.False(realHandler.AllowAutoRedirect);
    }

    private static RestApiHttpClientProvider HttpProvider(HttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        return new RestApiHttpClientProvider(factory.Object, new ConfigurationBuilder().Build());
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public bool Redirect { get; init; }
        public List<string> Methods { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method);
            Assert.Null(request.Content);
            var response = new HttpResponseMessage(Redirect ? HttpStatusCode.Found : HttpStatusCode.OK)
            { Content = new StringContent("healthy", Encoding.UTF8) };
            if (Redirect) response.Headers.Location = new Uri("https://example.test/delete");
            return Task.FromResult(response);
        }
    }
}

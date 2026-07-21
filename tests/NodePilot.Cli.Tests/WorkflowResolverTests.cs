using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Cli.Tests;

public sealed class WorkflowResolverTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly NodePilotApiClient _client;

    public WorkflowResolverTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url + "/") };
        _client = new NodePilotApiClient(http);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    [Fact]
    public async Task ByGuid_HitsGetWorkflowDirectly()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/workflows/{id}").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Build")));

        var w = await WorkflowResolver.ResolveAsync(_client, id.ToString(), CancellationToken.None);
        w.Id.Should().Be(id);
        // List endpoint must NOT have been called.
        _server.LogEntries.Should().NotContain(e => e.RequestMessage.AbsolutePath == "/api/workflows" && e.RequestMessage.Method == "GET");
    }

    [Fact]
    public async Task ByName_UniqueMatch_Resolves()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/workflows").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(List(
                    (idA, "Build"), (idB, "Report"))));

        var w = await WorkflowResolver.ResolveAsync(_client, "Report", CancellationToken.None);
        w.Id.Should().Be(idB);
    }

    [Fact]
    public async Task ByName_CaseInsensitive()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/workflows").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(List((id, "Build"))));

        var w = await WorkflowResolver.ResolveAsync(_client, "build", CancellationToken.None);
        w.Id.Should().Be(id);
    }

    [Fact]
    public async Task ByName_AmbiguousMatch_Throws()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/workflows").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(List(
                    (idA, "Build"), (idB, "Build"))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowResolver.ResolveAsync(_client, "Build", CancellationToken.None));
        ex.Message.Should().Contain("Multiple workflows");
    }

    [Fact]
    public async Task ByName_NotFound_Throws()
    {
        _server.Given(Request.Create().WithPath("/api/workflows").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowResolver.ResolveAsync(_client, "Missing", CancellationToken.None));
        ex.Message.Should().Contain("No workflow named");
    }

    private static string Single(Guid id, string name) => $$"""
    { "id": "{{id}}", "name": "{{name}}", "description": null,
      "definitionJson": "{}", "version": 1, "isEnabled": true,
      "createdAt": "2026-04-01T00:00:00Z", "updatedAt": "2026-04-01T00:00:00Z",
      "createdBy": null, "updatedBy": null, "activityCount": 0, "triggerTypes": [],
      "lastExecution": null, "successCount": 0, "totalCount": 0, "avgDurationMs": null,
      "checkedOutByUserId": null, "checkedOutByUserName": null, "checkedOutAt": null }
    """;

    private static string List(params (Guid Id, string Name)[] rows)
    {
        var items = rows.Select(r => Single(r.Id, r.Name));
        return "[" + string.Join(",", items) + "]";
    }
}

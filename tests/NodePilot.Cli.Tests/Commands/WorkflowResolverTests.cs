using FluentAssertions;
using NodePilot.Cli.Api;
using NodePilot.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace NodePilot.Cli.Tests.Commands;

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
        _server.LogEntries.Should().NotContain(e => e.RequestMessage!.AbsolutePath == "/api/workflows" && e.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task ByName_UniqueMatch_ResolvesViaByNameEndpoint()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/workflows/by-name/Report").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Report")));

        var w = await WorkflowResolver.ResolveAsync(_client, "Report", CancellationToken.None);
        w.Id.Should().Be(id);
        // The whole workflow list no longer travels over the wire to resolve one name.
        _server.LogEntries.Should().NotContain(e => e.RequestMessage!.AbsolutePath == "/api/workflows" && e.RequestMessage!.Method == "GET");
    }

    [Fact]
    public async Task ByName_EscapesTheName()
    {
        var id = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/workflows/by-name/Nightly Backup").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody(Single(id, "Nightly Backup")));

        var w = await WorkflowResolver.ResolveAsync(_client, "Nightly Backup", CancellationToken.None);
        w.Id.Should().Be(id);
    }

    [Fact]
    public async Task ByName_Ambiguous_TranslatesTheConflict()
    {
        _server.Given(Request.Create().WithPath("/api/workflows/by-name/Build").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(409)
                   .WithBody("""{"message":"Multiple workflows named 'Build' — disambiguate with the GUID."}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkflowResolver.ResolveAsync(_client, "Build", CancellationToken.None));
        ex.Message.Should().Contain("Multiple workflows");
    }

    [Fact]
    public async Task ByName_NotFound_Throws()
    {
        _server.Given(Request.Create().WithPath("/api/workflows/by-name/Missing").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));

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
}

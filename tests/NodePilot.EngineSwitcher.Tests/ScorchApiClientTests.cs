using System.Net;
using System.Net.Http;
using FluentAssertions;
using NodePilot.EngineSwitcher.Configuration;
using NodePilot.EngineSwitcher.Services;
using Xunit;

namespace NodePilot.EngineSwitcher.Tests;

public sealed class ScorchApiClientTests
{
    // A web service that faults while writing the collection sends a truncated body under HTTP 200.
    // The parser message alone does not say which call broke.
    [Fact]
    public async Task ListJobs_WithATruncatedResponse_NamesTheRequestAndTheBody()
    {
        const string truncated = """{"@odata.context":"http://localhost:81/api/$metadata#Jobs","value":[""";
        using var client = Client(new StubHandler(HttpStatusCode.OK, truncated));

        var action = () => client.ListJobsAsync(CancellationToken.None);

        (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("malformed ScorchJob response")
            .And.Contain("api/jobs")
            .And.Contain("\"value\":[");
    }

    [Fact]
    public async Task ListJobs_WithANonJsonResponse_NamesTheRequestAndTheBody()
    {
        using var client = Client(new StubHandler(HttpStatusCode.OK, "<html><body>Server Error</body></html>"));

        var action = () => client.ListJobsAsync(CancellationToken.None);

        (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("api/jobs").And.Contain("Server Error");
    }

    [Fact]
    public async Task ListJobs_WithAJsonObjectInsteadOfACollection_NamesTheRequestAndTheBody()
    {
        using var client = Client(new StubHandler(HttpStatusCode.OK, """{"error":{"code":"filter"}}"""));

        var action = () => client.ListJobsAsync(CancellationToken.None);

        (await action.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("unexpected ScorchJob response")
            .And.Contain("api/jobs")
            .And.Contain("\"code\":\"filter\"");
    }

    [Fact]
    public async Task ListJobs_WithAnOdataCollection_ReturnsTheEntries()
    {
        var id = Guid.NewGuid();
        var runbookId = Guid.NewGuid();
        using var client = Client(new StubHandler(
            HttpStatusCode.OK,
            $$"""{"value":[{"Id":"{{id}}","RunbookId":"{{runbookId}}","Status":"Running"}]}"""));

        var jobs = await client.ListJobsAsync(CancellationToken.None);

        jobs.Should().ContainSingle().Which.Should().Be(new ScorchJob(id, runbookId, "Running"));
    }

    private static ScorchApiClient Client(HttpMessageHandler handler) =>
        new(
            new ScorchWorkloadConfiguration(@"C:\lists\scorch.txt", "http://localhost:81"),
            handler);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
                RequestMessage = request,
            });
    }
}

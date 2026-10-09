using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using NodePilot.Switcher.Configuration;
using NodePilot.Switcher.Services;
using Xunit;

namespace NodePilot.Switcher.Tests;

public sealed class ScorchApiClientTests
{
    [Theory]
    [InlineData("http://outside.example.test/api/runbooks", false)]
    [InlineData("https://outside.example.test/api/runbooks", false)]
    [InlineData("//outside.example.test/api/runbooks", false)]
    [InlineData("http://scorch.example.test/api/runbooks", false)]
    [InlineData("https://scorch.example.test:444/api/runbooks", false)]
    [InlineData("http://outside.example.test/api/runbooks", true)]
    [InlineData("https://outside.example.test/api/runbooks", true)]
    [InlineData("//outside.example.test/api/runbooks", true)]
    [InlineData("http://scorch.example.test/api/runbooks", true)]
    [InlineData("https://scorch.example.test:444/api/runbooks", true)]
    public async Task ListRunbooks_RejectsOffOriginPathBeforeSending(string path, bool pagination)
    {
        var handler = new PagesHandler(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = path }),
            """{"value":[]}""");
        var config = new ScorchWorkloadConfiguration(@"C:\lists\scorch.txt", "https://scorch.example.test",
            RunbooksPath: pagination ? "api/runbooks" : path);
        using var client = new ScorchApiClient(config, handler);

        var action = () => client.ListRunbooksAsync(TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*configured API origin*");
        handler.Requests.Should().HaveCount(pagination ? 1 : 0);
    }

    [Theory]
    [InlineData("api/runbooks?page=2")]
    [InlineData("/api/runbooks?page=2")]
    [InlineData("https://scorch.example.test/api/runbooks?page=2")]
    public async Task ListRunbooks_FollowsSameOriginPagination(string next)
    {
        var first = new ScorchRunbook(Guid.NewGuid(), "First");
        var second = new ScorchRunbook(Guid.NewGuid(), "Second");
        var handler = new PagesHandler(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = new[] { first }, ["@odata.nextLink"] = next }),
            JsonSerializer.Serialize(new { value = new[] { second } }));
        using var client = new ScorchApiClient(
            new ScorchWorkloadConfiguration(@"C:\lists\scorch.txt", "https://scorch.example.test"), handler);

        var result = await client.ListRunbooksAsync(TestContext.Current.CancellationToken);

        result.Should().Equal(first, second);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].Should().Be(new Uri("https://scorch.example.test/api/runbooks?page=2"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredMutationPaths_CannotChangeOrigin(bool stop)
    {
        var handler = new PagesHandler("{}");
        var config = new ScorchWorkloadConfiguration(@"C:\lists\scorch.txt", "https://scorch.example.test",
            JobsPath: "https://outside.example.test/api/jobs",
            StopJobPathTemplate: "https://outside.example.test/api/jobs/{id}");
        using var client = new ScorchApiClient(config, handler);

        var action = () => stop
            ? client.StopJobAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)
            : client.StartRunbookAsync(Guid.NewGuid(), [], TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*configured API origin*");
        handler.Requests.Should().BeEmpty();
    }

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

    [Fact]
    public async Task ListJobs_WithDefaultConfiguration_UsesPortableMinimalQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"value":[]}""");
        using var client = Client(handler);

        await client.ListJobsAsync(CancellationToken.None);

        Uri.UnescapeDataString(handler.RequestUri!.Query).Should().Be(
            "?$select=Id,RunbookId,Status&$filter=Status eq 'Pending' or Status eq 'Running'");
    }

    private static ScorchApiClient Client(HttpMessageHandler handler) =>
        new(
            new ScorchWorkloadConfiguration(@"C:\lists\scorch.txt", "http://localhost:81"),
            handler);

    private sealed class PagesHandler(params string[] pages) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri ?? throw new InvalidOperationException("Request URI missing."));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(pages[Requests.Count - 1]),
                RequestMessage = request,
            });
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
                RequestMessage = request,
            });
        }
    }
}

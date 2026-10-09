using System.Net;
using System.Text.Json;
using NodePilot.Core.Clients;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.LoadTests;
using Xunit;

namespace NodePilot.Engine.Tests.LoadTests;

public sealed class LoadHarnessContractTests
{
    public static IEnumerable<object[]> Templates()
    {
        yield return ["deep", WorkflowTemplates.BuildDeepSequential(50, "test")];
        yield return ["wide", WorkflowTemplates.BuildWideFanout(30, "test")];
        yield return ["mixed", WorkflowTemplates.BuildMixedHeavy("test", "http://localhost:5000")];
        foreach (var (name, json) in WorkflowTemplates.BuildSubworkflowNest(3, "test"))
            yield return [name, json];
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryTemplate_IsValidAndAllActivitiesAreReachable(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        var analysis = WorkflowAnalyzer.Analyze(document.RootElement);
        Assert.True(analysis.Ok, name + ": " + string.Join("; ", analysis.Findings.Select(f => f.Message)));
        Assert.Single(analysis.Roots);
        Assert.DoesNotContain(analysis.Findings, finding => finding.Code == "unreachable-node");
    }

    [Fact]
    public async Task CountRunning_UsesServerFilteredTotalBeyondTheFirstPage()
    {
        string? path = null;
        using var http = new HttpClient(new Handler(request =>
        {
            path = request.RequestUri!.PathAndQuery;
            return Json(new PagedResponse<object>(Array.Empty<object>(), 1, 1, 357, 357));
        })) { BaseAddress = new Uri("http://localhost/") };
        var count = await new NodePilotApiClient(http).CountRunningExecutionsAsync();
        Assert.Equal(357, count);
        Assert.Contains("status=Running", path);
        Assert.Contains("pageSize=1", path);
    }

    [Fact]
    public async Task Seeding_PublishesEveryWorkflowBeforeReturningExecutableIds()
    {
        var created = new Dictionary<Guid, (string Name, string Definition)>();
        var published = new HashSet<Guid>();
        using var http = new HttpClient(new Handler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var name = body.RootElement.GetProperty("name").GetString()!;
            var definition = body.RootElement.GetProperty("definitionJson").GetString()!;
            if (request.RequestUri!.AbsolutePath == "/api/workflows")
            {
                var id = Guid.NewGuid();
                created.Add(id, (name, definition));
                return Json(new { id });
            }
            Assert.EndsWith("/publish", request.RequestUri.AbsolutePath);
            var workflowId = Guid.Parse(request.RequestUri.Segments[^2].Trim('/'));
            Assert.Equal(created[workflowId], (name, definition));
            // A parent may only become executable once its named child is published.
            using var graph = JsonDocument.Parse(definition);
            foreach (var node in graph.RootElement.GetProperty("nodes").EnumerateArray())
            {
                var data = node.GetProperty("data");
                if (data.GetProperty("activityType").GetString() != "startWorkflow") continue;
                var childName = data.GetProperty("config").GetProperty("workflowNameOrId").GetString();
                var child = Assert.Single(created.Where(pair => pair.Value.Name == childName));
                Assert.Contains(child.Key, published);
            }
            published.Add(workflowId);
            return Json(new { id = workflowId, isEnabled = true });
        })) { BaseAddress = new Uri("http://localhost/") };
        var result = await Seeder.SeedAsync(new NodePilotApiClient(http), new LoadTestOptions
        {
            Seed = new SeedOptions { CopiesPerTemplate = 1, DeepSequentialDepth = 2, WideFanoutWidth = 2, SubWorkflowDepth = 3 },
        }, CancellationToken.None);
        Assert.Equal(6, created.Count);
        Assert.Equal(created.Count, published.Count);
        Assert.All(result.PlainWorkflowIds.Concat(result.SubWorkflowRootIds), id => Assert.Contains(id, published));
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}

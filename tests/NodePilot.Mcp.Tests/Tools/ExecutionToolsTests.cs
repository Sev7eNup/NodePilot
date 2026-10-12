using System.Text.Json;
using FluentAssertions;
using NodePilot.Mcp.Tests.Infra;
using NodePilot.Mcp.Tools;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace NodePilot.Mcp.Tests.Tools;

public sealed class ExecutionToolsTests
{
    [Theory]
    [InlineData(50, 50)]
    [InlineData(0, 1)]
    [InlineData(1000, 200)]
    public async Task ListExecutions_LargeHistory_RequestsOnlyBoundedFirstPageAndRetainsTotal(int limit, int expectedSize)
    {
        using var api = new TestApi();
        var wf = Guid.NewGuid();
        var newest = Guid.NewGuid();
        api.Server.Given(Request.Create().WithPath("/api/executions").UsingGet()
                .WithParam("workflowId", wf.ToString()).WithParam("activeOnly", "true")
                .WithParam("page", "1").WithParam("pageSize", expectedSize.ToString()))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                items = new[] { new { id = newest, workflowId = wf, status = "Running", startedAt = DateTime.UtcNow } },
                page = 1, pageSize = expectedSize, total = 1_000_000, totalPages = 1_000_000 / expectedSize,
            }));

        var result = await new ExecutionTools(api.Client()).ListExecutions(wf.ToString(), activeOnly: true,
            limit: limit, cancellationToken: TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        document.RootElement.GetProperty("totalAvailable").GetInt64().Should().Be(1_000_000);
        document.RootElement.GetProperty("count").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("executions")[0].GetProperty("id").GetGuid().Should().Be(newest);
        api.Server.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task ListExecutions_ProjectsSlimRowsAndRespectsLimit()
    {
        using var api = new TestApi();
        var wf = Guid.NewGuid();
        var rows = Enumerable.Range(0, 5).Select(_ => new
        {
            id = Guid.NewGuid(),
            workflowId = wf,
            status = "Succeeded",
            startedAt = DateTime.UtcNow,
            completedAt = (DateTime?)DateTime.UtcNow,
            triggeredBy = "manual",
            errorMessage = (string?)null,
            stepsTotal = 3,
            stepsCompleted = 3,
        }).ToArray();

        api.Server
            .Given(Request.Create().WithPath("/api/executions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                items = rows, page = 1, pageSize = 200, total = rows.Length, totalPages = 1,
            }));

        var tools = new ExecutionTools(api.Client());
        var result = await tools.ListExecutions(workflowId: wf.ToString(), limit: 2);

        var json = JsonSerializer.Serialize(result);
        json.Should().Contain("\"count\":2");
        json.Should().Contain("\"totalAvailable\":5");
    }

    [Fact]
    public async Task GetExecutionSteps_TruncatesLargeOutput()
    {
        var exec = Guid.NewGuid();
        var big = new string('x', 9000);
        using var api = new TestApi();
        api.Server
            .Given(Request.Create().WithPath($"/api/executions/{exec}/steps").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[]
            {
                new
                {
                    id = Guid.NewGuid(), stepId = "step-1", stepName = "Check", stepType = "runScript",
                    targetMachine = (string?)null, status = "Failed",
                    startedAt = (DateTime?)DateTime.UtcNow, completedAt = (DateTime?)DateTime.UtcNow,
                    output = big, errorOutput = "boom", attemptCount = 1,
                    pausedAt = (DateTime?)null, variablesSnapshot = (string?)null, traceOutput = (string?)null,
                },
            }));

        var tools = new ExecutionTools(api.Client());
        var result = await tools.GetExecutionSteps(exec.ToString());

        var json = JsonSerializer.Serialize(result);
        json.Should().Contain("truncated");
        json.Should().Contain("boom");
        json.Length.Should().BeLessThan(big.Length); // proves the 9k blob was cut down
    }
}

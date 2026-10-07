using System.Text.Json;
using NodePilot.Core.Agents;
using NodePilot.Core.WorkflowDefinitions;
using Xunit;

namespace NodePilot.Ai.Tests;

public sealed class AgentConfigurationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    public void ParallelLimitMustBePositiveAndFitMembers(int limit)
    {
        var config = new AgentActivityConfiguration { Task = "Check", MaxParallelMembers = limit,
            Members = [new() { Id = "lead", IsSupervisor = true }, new() { Id = "worker" }] };
        Assert.Throws<ArgumentException>(() => AgentConfiguration.Validate(config, true));
        AgentConfiguration.Validate(config with { MaxParallelMembers = 1 }, true);
    }
    [Fact]
    public void ReviewerIsAnOptionalExplicitNonSupervisorFunction()
    {
        var config = new AgentActivityConfiguration { Task = "Review", Members =
            [new() { Id = "lead", IsSupervisor = true }, new() { Id = "quality", IsReviewer = true }] };
        AgentConfiguration.Validate(config, true);
        Assert.Throws<ArgumentException>(() => AgentConfiguration.Validate(config with
            { Members = [config.Members[0] with { IsReviewer = true }, config.Members[1]] }, true));
        Assert.Throws<ArgumentException>(() => AgentConfiguration.Validate(new() { Task = "Review", Agent = new() { IsReviewer = true } }, false));
        var parsed = AgentConfiguration.Parse(JsonSerializer.SerializeToElement(config, AgentConfiguration.JsonOptions), true);
        Assert.True(parsed.Members[1].IsReviewer);
        Assert.False(new AgentDefinition { Role = "Reviewer" }.IsReviewer);
    }

    [Theory]
    [InlineData("{\"task\":\"x\",\"retry\":{\"maxAttempts\":3}}")]
    [InlineData("{\"task\":\"x\",\"agent\":{\"id\":null}}")]
    [InlineData("{\"task\":\"x\",\"agent\":{\"targetMachineId\":\"11111111-1111-1111-1111-111111111111\"}}")]
    [InlineData("{\"task\":\"x\",\"agent\":{\"instructions\":null}}")]
    [InlineData("{\"task\":\"x\",\"agent\":{\"tools\":[{\"name\":\"files_read\"}]}}")]
    [InlineData("{\"task\":\"x\",\"agent\":{\"tools\":[{\"name\":\"ssh\"}]}}")]
    public void ParseRejectsInvalidPermissionOrRetryConfiguration(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => AgentConfiguration.Parse(doc.RootElement, false));
    }

    [Fact]
    public void HttpDoesNotRequireAHostWhitelist()
    {
        using var doc = JsonDocument.Parse("{\"task\":\"Read status\",\"agent\":{\"tools\":[{\"name\":\"http_request\"}]}}");
        Assert.Empty(AgentConfiguration.Parse(doc.RootElement, false).Agent.Tools.Single().AllowedHosts);
    }

    [Fact]
    public void TeamRequiresExactlyOneSupervisorAndStableUniqueIds()
    {
        var config = new AgentActivityConfiguration { Task = "Coordinate", Members = [new() { Id = "a", IsSupervisor = true }, new() { Id = "b", IsSupervisor = true }] };
        Assert.Throws<ArgumentException>(() => AgentConfiguration.Validate(config, true));
        Assert.Throws<ArgumentException>(() => AgentConfiguration.Validate(config with { Members = [new() { Id = "a", IsSupervisor = true }, new() { Id = "A" }] }, true));
    }

    [Theory]
    [InlineData("aiAgent", "agent", "{\"id\":\"agent\",\"role\":\"Researcher\",\"instructions\":\"opaque secret here\",\"apiKey\":\"abc\"}")]
    [InlineData("aiAgentTeam", "members", "[{\"id\":\"researcher\",\"role\":\"Researcher\",\"instructions\":\"opaque secret here\",\"apiKey\":\"abc\"}]")]
    public void RedactionCoversNestedInstructionsAndApiKeysButPreservesMemberIdentity(string type, string key, string value)
    {
        var json = "{\"nodes\":[{\"id\":\"step\",\"type\":\"activity\",\"data\":{\"activityType\":\"" + type
            + "\",\"config\":{\"task\":\"opaque task\",\"" + key + "\":" + value + "}}}],\"edges\":[]}";
        var redacted = WorkflowSecretRedactor.Redact(JsonDocument.Parse(json).RootElement).ToJsonString();
        Assert.DoesNotContain("opaque secret here", redacted);
        Assert.DoesNotContain("opaque task", redacted);
        Assert.DoesNotContain("abc", redacted);
        Assert.Contains("Researcher", redacted);
    }
}

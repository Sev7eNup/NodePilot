using NodePilot.Ai;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>The chat assistant surfaces enabled custom-activity facts (name, remote, inputs/outputs) in its system prompt.</summary>
public class WorkflowAssistantCustomActivityTests
{
    [Fact]
    public async Task SystemPrompt_IncludesCustomActivityMetadata()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(new CustomActivityDefinitionInput
        {
            Key = "disk_check", Name = "Disk Check", ScriptTemplate = "Get-PSDrive C",
            InputParametersJson = "[{\"name\":\"drive\",\"label\":\"Drive\",\"type\":\"string\"}]",
            OutputParametersJson = "[{\"name\":\"freeGb\",\"type\":\"number\"}]",
        }, "alice", CancellationToken.None);
        await store.SetEnabledAsync(def.Id, true, "admin", CancellationToken.None);

        var fake = new FakeLlmClient().EnqueueStream("ok");
        var svc = new WorkflowAssistantService(fake, new PromptCatalog(), new WorkflowChatToolRegistry(),
            new StaticOptionsMonitor<LlmOptions>(new LlmOptions()), store);

        var workflowJson = "{\"nodes\":[{\"id\":\"s1\",\"type\":\"activity\",\"position\":{\"x\":0,\"y\":0},"
            + "\"data\":{\"label\":\"Disk\",\"activityType\":\"custom:disk_check\","
            + "\"config\":{\"__customDefinitionId\":\"" + def.Id + "\",\"__customKey\":\"disk_check\"}}}],\"edges\":[]}";
        var req = new WorkflowChatRequest("Erkläre", workflowJson, Guid.NewGuid(), "h", Array.Empty<AiChatTurnDto>());

        using var doc = JsonDocument.Parse(workflowJson);
        await foreach (var _ in svc.StreamChatAsync(req, doc.RootElement, allowModify: true, allowExecutionTools: false, CancellationToken.None)) { }

        var systemPrompt = fake.Calls.Should().ContainSingle().Subject.SystemPrompt;
        systemPrompt.Should().Contain("custom:disk_check");
        systemPrompt.Should().Contain("Disk Check");
        systemPrompt.Should().Contain("freeGb");   // declared output
        systemPrompt.Should().Contain("exitCode");  // always-present
    }
}

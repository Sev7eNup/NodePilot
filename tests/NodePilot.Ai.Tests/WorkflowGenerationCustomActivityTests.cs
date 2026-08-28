using FluentAssertions;
using NodePilot.Core.Interfaces;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Ai.Tests;

/// <summary>
/// Workflow generation must know the installation's custom activities. Without this the model only
/// ever sees the built-in catalog, so it rebuilds a user's existing custom node out of raw
/// runScript steps — or ignores the request.
/// </summary>
public class WorkflowGenerationCustomActivityTests
{
    private const string MinimalEnvelope = """
        {
          "name": "Hello",
          "definition": {
            "nodes": [
              { "id": "n1", "type": "activity", "position": { "x": 0, "y": 0 },
                "data": { "label": "Start", "activityType": "manualTrigger", "config": {} } }
            ],
            "edges": []
          }
        }
        """;

    [Fact]
    public async Task GenerateAsync_EnabledCustomActivity_AppearsInTheSystemPrompt()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(new CustomActivityDefinitionInput
        {
            Key = "disk_check",
            Name = "Disk Check",
            Description = "Reports free space on a drive.",
            ScriptTemplate = "Get-PSDrive C",
            InputParametersJson = """[{"name":"drive","label":"Drive","type":"string","required":true}]""",
            OutputParametersJson = """[{"name":"freeGb","type":"number"}]""",
        }, "alice", CancellationToken.None);
        await store.SetEnabledAsync(def.Id, true, "admin", CancellationToken.None);

        var fake = new FakeLlmClient().EnqueueContent(MinimalEnvelope);
        var svc = new WorkflowGenerationService(new FakeLlmClientFactory(fake), new PromptCatalog(), store);

        await svc.GenerateAsync(new GenerateWorkflowRequest("build me something"), CancellationToken.None);

        var systemPrompt = fake.Calls.Should().ContainSingle().Subject.SystemPrompt;
        systemPrompt.Should().Contain("custom:disk_check");
        systemPrompt.Should().Contain("Disk Check");
        systemPrompt.Should().Contain("Reports free space on a drive.");
    }

    [Fact]
    public async Task GenerateAsync_DisabledCustomActivity_IsNotOffered()
    {
        await using var db = TestDbFactory.Create();
        var store = new CustomActivityDefinitionStore(db);
        await store.CreateAsync(new CustomActivityDefinitionInput
        {
            Key = "draft_only", Name = "Draft Only", ScriptTemplate = "Get-Date",
        }, "alice", CancellationToken.None);

        var fake = new FakeLlmClient().EnqueueContent(MinimalEnvelope);
        var svc = new WorkflowGenerationService(new FakeLlmClientFactory(fake), new PromptCatalog(), store);

        await svc.GenerateAsync(new GenerateWorkflowRequest("build me something"), CancellationToken.None);

        fake.Calls.Single().SystemPrompt.Should().NotContain("custom:draft_only",
            "a disabled definition is a draft and cannot be executed");
    }

    [Fact]
    public async Task GenerateAsync_NoStore_StillGenerates()
    {
        // The store is not registered by AddNodePilotAi — generation must degrade to the built-in
        // catalog rather than fail.
        var fake = new FakeLlmClient().EnqueueContent(MinimalEnvelope);
        var svc = new WorkflowGenerationService(new FakeLlmClientFactory(fake), new PromptCatalog());

        var resp = await svc.GenerateAsync(new GenerateWorkflowRequest("build me something"), CancellationToken.None);

        resp.SuggestedName.Should().Be("Hello");
        fake.Calls.Single().SystemPrompt.Should().NotContain("## Custom activities");
    }

    [Fact]
    public async Task GenerateAsync_AlwaysSeesLlmQueryInTheCatalog()
    {
        // Without llmQuery in the prompt, generation would emit a hand-rolled OpenAI POST
        // on a restApi node when asked for an AI call.
        var fake = new FakeLlmClient().EnqueueContent(MinimalEnvelope);
        var svc = new WorkflowGenerationService(new FakeLlmClientFactory(fake), new PromptCatalog());

        await svc.GenerateAsync(new GenerateWorkflowRequest("ask an LLM"), CancellationToken.None);

        fake.Calls.Single().SystemPrompt.Should().Contain("`llmQuery`");
    }
}

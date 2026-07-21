using NodePilot.Ai;
using NodePilot.TestCommons;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NodePilot.Ai.Tests;

public class WorkflowGenerationServiceTests
{
    private static WorkflowGenerationService NewService(FakeLlmClient client)
        => new(client, new PromptCatalog());

    private const string MinimalEnvelope = """
        {
          "name": "Hello",
          "description": "Smoke",
          "definition": {
            "nodes": [
              { "id": "n1", "type": "activity", "position": { "x": 0, "y": 0 },
                "data": { "label": "Start", "activityType": "manualTrigger", "config": {} } }
            ],
            "edges": []
          }
        }
        """;

    // ---- Happy path -----------------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_HappyPath_ReturnsValidatedDefinition()
    {
        var fake = new FakeLlmClient().EnqueueContent(MinimalEnvelope);
        var svc = NewService(fake);

        var resp = await svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None);

        resp.SuggestedName.Should().Be("Hello");
        resp.SuggestedDescription.Should().Be("Smoke");
        resp.NodeCount.Should().Be(1);
        resp.EdgeCount.Should().Be(0);
        resp.Retried.Should().BeFalse();
        // DefinitionJson is re-serialized and must still parse as valid JSON
        using var doc = JsonDocument.Parse(resp.DefinitionJson);
        doc.RootElement.GetProperty("nodes").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GenerateAsync_HappyPath_PassesJsonModeFlag()
    {
        var fake = new FakeLlmClient().EnqueueContent(MinimalEnvelope);
        var svc = NewService(fake);

        await svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None);

        fake.Calls[0].JsonMode.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_SystemPromptIncludesExampleAndEnvelope()
    {
        var fake = new FakeLlmClient().EnqueueContent(MinimalEnvelope);
        var svc = NewService(fake);

        await svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None);

        fake.Calls[0].SystemPrompt.Should().Contain("Reference example workflow");
        fake.Calls[0].SystemPrompt.Should().Contain("Output envelope");
        fake.Calls[0].SystemPrompt.Should().Contain("\"definition\"");
    }

    // ---- Tolerant parser ------------------------------------------------------------

    [Theory]
    [InlineData("Sure! Here's the workflow you asked for:\n\n```json\n{ENV}\n```\n\nLet me know if you need changes.")]
    [InlineData("```\n{ENV}\n```")]
    [InlineData("{ENV}")]
    public async Task GenerateAsync_TolerantParser_StripsFencesAndProse(string template)
    {
        var raw = template.Replace("{ENV}", MinimalEnvelope);
        var fake = new FakeLlmClient().EnqueueContent(raw);
        var svc = NewService(fake);

        var resp = await svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None);

        resp.NodeCount.Should().Be(1);
        resp.Retried.Should().BeFalse();
    }

    // ---- Retry path -------------------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_FirstResponseUnparseable_RetriesAndSucceeds()
    {
        var fake = new FakeLlmClient()
            .EnqueueContent("I cannot help with that.")  // not JSON
            .EnqueueContent(MinimalEnvelope);
        var svc = NewService(fake);

        var resp = await svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None);

        resp.Retried.Should().BeTrue();
        resp.NodeCount.Should().Be(1);
        fake.Calls.Should().HaveCount(2);
        fake.Calls[1].UserPrompt.Should().Contain("retry",
            "the second call should mention the prior failure to nudge the model");
    }

    [Fact]
    public async Task GenerateAsync_BothAttemptsFail_ThrowsMalformedResponse()
    {
        var fake = new FakeLlmClient()
            .EnqueueContent("nope")
            .EnqueueContent("still nope");
        var svc = NewService(fake);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None));
        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
        fake.Calls.Should().HaveCount(LlmOptions.MaxJsonRetries + 1);
    }

    // ---- Schema validation ------------------------------------------------------------

    [Fact]
    public async Task GenerateAsync_MissingNodes_ThrowsMalformed()
    {
        var fake = new FakeLlmClient()
            .EnqueueContent("""{ "name":"x", "definition": { "edges": [] } }""")
            .EnqueueContent("""{ "name":"x", "definition": { "edges": [] } }""");
        var svc = NewService(fake);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None));
        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task GenerateAsync_MissingEdges_ThrowsMalformed()
    {
        var json = """
            { "name":"x", "definition": { "nodes": [
                { "id":"n1", "data": { "activityType":"manualTrigger" } }
            ] } }
            """;
        var fake = new FakeLlmClient().EnqueueContent(json).EnqueueContent(json);
        var svc = NewService(fake);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None));
        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task GenerateAsync_UnknownActivityType_ThrowsMalformed()
    {
        var json = """
            { "name":"x", "definition": { "nodes": [
                { "id":"n1", "data": { "activityType":"definitelyNotARealActivity" } }
            ], "edges": [] } }
            """;
        var fake = new FakeLlmClient().EnqueueContent(json).EnqueueContent(json);
        var svc = NewService(fake);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None));
        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
        ex.Message.Should().Contain("definitelyNotARealActivity");
    }

    [Fact]
    public async Task GenerateAsync_DuplicateNodeId_ThrowsMalformed()
    {
        var json = """
            { "name":"x", "definition": { "nodes": [
                { "id":"n1", "data": { "activityType":"manualTrigger" } },
                { "id":"n1", "data": { "activityType":"log" } }
            ], "edges": [] } }
            """;
        var fake = new FakeLlmClient().EnqueueContent(json).EnqueueContent(json);
        var svc = NewService(fake);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None));
        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
        ex.Message.Should().Contain("Duplicate");
    }

    [Fact]
    public async Task GenerateAsync_EdgeReferencesUnknownNode_ThrowsMalformed()
    {
        var json = """
            { "name":"x", "definition": { "nodes": [
                { "id":"n1", "data": { "activityType":"manualTrigger" } }
            ], "edges": [
                { "id":"e1", "source":"n1", "target":"ghost" }
            ] } }
            """;
        var fake = new FakeLlmClient().EnqueueContent(json).EnqueueContent(json);
        var svc = NewService(fake);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None));
        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
        ex.Message.Should().Contain("ghost");
    }

    [Fact]
    public async Task GenerateAsync_ZeroNodes_ThrowsMalformed()
    {
        var json = """{ "name":"x", "definition": { "nodes": [], "edges": [] } }""";
        var fake = new FakeLlmClient().EnqueueContent(json).EnqueueContent(json);
        var svc = NewService(fake);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None));
        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
        ex.Message.Should().Contain("zero nodes");
    }

    [Fact]
    public async Task GenerateAsync_MissingDefinition_ThrowsMalformed()
    {
        var json = """{ "name":"x", "definition": "not an object" }""";
        var fake = new FakeLlmClient().EnqueueContent(json).EnqueueContent(json);
        var svc = NewService(fake);

        var ex = await Assert.ThrowsAsync<LlmException>(() =>
            svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None));
        ex.Kind.Should().Be(LlmErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task GenerateAsync_DefaultsName_WhenLlmOmitsIt()
    {
        var json = """
            { "definition": { "nodes": [
                { "id":"n1", "data": { "activityType":"manualTrigger" } }
            ], "edges": [] } }
            """;
        var fake = new FakeLlmClient().EnqueueContent(json);
        var svc = NewService(fake);

        var resp = await svc.GenerateAsync(new GenerateWorkflowRequest("smoke"), CancellationToken.None);

        resp.SuggestedName.Should().NotBeNullOrEmpty();
        resp.SuggestedDescription.Should().BeNull();
    }

    // ---- ExtractJsonObject as an isolated unit -----------------------------------------

    [Theory]
    [InlineData("{}", "{}")]
    [InlineData("prefix {} suffix", "{}")]
    [InlineData("```json\n{\"a\":1}\n```", "{\"a\":1}")]
    [InlineData("nested { \"x\": { \"y\": 1 } } trailing", "{ \"x\": { \"y\": 1 } }")]
    [InlineData("escaped string { \"a\": \"\\\"} not-end\" } end", "{ \"a\": \"\\\"} not-end\" }")]
    public void ExtractJsonObject_FindsBalancedOuter(string input, string expected)
    {
        WorkflowDefinitionJsonHelper.ExtractJsonObject(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no braces at all")]
    [InlineData("{ unclosed")]
    public void ExtractJsonObject_NoBalancedObject_ReturnsNull(string input)
    {
        WorkflowDefinitionJsonHelper.ExtractJsonObject(input).Should().BeNull();
    }
}

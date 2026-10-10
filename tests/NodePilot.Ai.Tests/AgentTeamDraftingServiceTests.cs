using FluentAssertions;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Ai.Tests;

public class AgentTeamDraftingServiceTests
{
    private static readonly AgentTeamInventory Inventory = new(
        [new AgentTeamInventoryMachine("SRV-01", "srv01.corp.local")],
        ["svc-diag"],
        [new AgentTeamInventorySkill("windows-diagnostics", "1.0.0", "Diagnoses Windows hosts")],
        [new AgentTeamInventoryMcpServer("docs", ["search"])],
        ["Collect logs"],
        ServiceIdentityAvailable: false);

    private const string ValidTeam = """
        {
          "task": "Find out why the service fails",
          "maxParallelMembers": 1,
          "members": [
            { "id": "lead", "role": "Lead", "instructions": "Coordinate.", "isSupervisor": true, "isReviewer": true,
              "machine": null, "credential": null, "serviceIdentity": false, "skills": [], "tools": [] },
            { "id": "analyst", "role": "Analyst", "instructions": "Read the logs.", "isSupervisor": false,
              "machine": "SRV-01", "credential": "svc-diag", "serviceIdentity": false,
              "skills": [ { "name": "windows-diagnostics", "version": "1.0.0" } ],
              "tools": [ { "name": "files_read", "paths": ["C:\\Logs"], "hosts": [], "workflows": [], "mcpServer": null, "mcpTool": null } ] }
          ]
        }
        """;

    private static AgentTeamDraftingService NewService(FakeLlmClient client)
        => new(new FakeLlmClientFactory(client), new PromptCatalog());

    [Fact]
    public async Task DraftAsync_ValidTeam_ParsesMembersAndReferences()
    {
        var llm = new FakeLlmClient().EnqueueContent(ValidTeam);

        var result = await NewService(llm).DraftAsync("a team", Inventory, CancellationToken.None);

        result.Retried.Should().BeFalse();
        result.Draft.Task.Should().Be("Find out why the service fails");
        result.Draft.MaxParallelMembers.Should().Be(1);
        var analyst = result.Draft.Members.Single(m => m.Id == "analyst");
        analyst.Machine.Should().Be("SRV-01");
        analyst.Credential.Should().Be("svc-diag");
        analyst.Skills.Single().Name.Should().Be("windows-diagnostics");
        analyst.Tools.Single().Paths.Should().Equal("C:\\Logs");
    }

    [Fact]
    public async Task DraftAsync_SupervisorMarkedReviewer_DropsReviewerFlag()
    {
        var llm = new FakeLlmClient().EnqueueContent(ValidTeam);

        var result = await NewService(llm).DraftAsync("a team", Inventory, CancellationToken.None);

        var lead = result.Draft.Members.Single(m => m.IsSupervisor);
        lead.IsReviewer.Should().BeFalse();
    }

    [Fact]
    public async Task DraftAsync_SendsJsonModeAndInventoryNamesOnly()
    {
        var llm = new FakeLlmClient().EnqueueContent(ValidTeam);

        await NewService(llm).DraftAsync("build a team", Inventory, CancellationToken.None);

        llm.Calls.Should().ContainSingle();
        llm.Calls[0].JsonMode.Should().BeTrue();
        llm.Calls[0].UserPrompt.Should().Contain("build a team")
            .And.Contain("SRV-01").And.Contain("svc-diag").And.Contain("Collect logs")
            .And.Contain("Service identity: not available");
    }

    [Fact]
    public void SystemPrompt_RequiresOneMemberPerMachine()
    {
        var prompt = new PromptCatalog().AgentTeamSystemPrompt;

        prompt.Should().Contain("**one member per machine**").And.Contain("exactly one machine");
    }

    [Fact]
    public async Task DraftAsync_GarbageThenValid_RetriesOnce()
    {
        var llm = new FakeLlmClient().EnqueueContent("sorry, no json").EnqueueContent(ValidTeam);

        var result = await NewService(llm).DraftAsync("a team", Inventory, CancellationToken.None);

        result.Retried.Should().BeTrue();
        llm.Calls.Should().HaveCount(2);
        llm.Calls[1].UserPrompt.Should().Contain("retry");
    }

    [Theory]
    [InlineData("{ \"members\": [ { \"id\": \"a\", \"role\": \"A\", \"isSupervisor\": true } ] }")]
    [InlineData("{ \"members\": [ { \"id\": \"a\", \"role\": \"A\" }, { \"id\": \"b\", \"role\": \"B\" } ] }")]
    [InlineData("{ \"members\": [ { \"id\": \"a\", \"role\": \"A\", \"isSupervisor\": true }, { \"id\": \"b\", \"role\": \"B\", \"isSupervisor\": true } ] }")]
    [InlineData("{ \"members\": [ { \"id\": \"a\", \"role\": \"A\", \"isSupervisor\": true }, { \"id\": \"b\" } ] }")]
    public async Task DraftAsync_UnusableTeamShape_FailsAsMalformedAfterRetry(string content)
    {
        var llm = new FakeLlmClient().EnqueueContent(content).EnqueueContent(content);

        var act = () => NewService(llm).DraftAsync("a team", Inventory, CancellationToken.None);

        (await act.Should().ThrowAsync<LlmException>()).Which.Kind.Should().Be(LlmErrorKind.MalformedResponse);
    }

    [Fact]
    public async Task DraftAsync_TokenCountsAreSummedAcrossRetry()
    {
        var llm = new FakeLlmClient()
            .EnqueueResponse(new LlmResponse("nope", "m", PromptTokens: 10, CompletionTokens: 2, TotalTokens: 12))
            .EnqueueResponse(new LlmResponse(ValidTeam, "m", PromptTokens: 11, CompletionTokens: 3, TotalTokens: 14));

        var result = await NewService(llm).DraftAsync("a team", Inventory, CancellationToken.None);

        result.PromptTokens.Should().Be(21);
        result.CompletionTokens.Should().Be(5);
        result.TotalTokens.Should().Be(26);
    }
}

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Ai;
using NodePilot.Api.Ai;
using NodePilot.Api.Controllers;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine.Agents;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class AiAgentTeamControllerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly NodePilotDbContext _db;
    private readonly Guid _machineId = Guid.NewGuid();
    private readonly Guid _credentialId = Guid.NewGuid();
    private readonly Guid _deniedFolder = Guid.NewGuid();

    public AiAgentTeamControllerTests()
    {
        _connection.Open();
        _db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = _deniedFolder, ParentFolderId = SharedWorkflowFolder.RootFolderId, Name = "Denied", Path = "/Denied", Depth = 1
        });
        _db.ManagedMachines.Add(new ManagedMachine { Id = _machineId, Name = "SRV-01", Hostname = "srv01.corp.local" });
        _db.Credentials.Add(new Credential { Id = _credentialId, Name = "svc-diag", Username = "u", EncryptedPassword = [1] });
        _db.Workflows.AddRange(
            Workflow("Collect logs", published: true, folder: SharedWorkflowFolder.RootFolderId),
            Workflow("Locked one", published: true, folder: SharedWorkflowFolder.RootFolderId, locked: true),
            Workflow("Draft one", published: false, folder: SharedWorkflowFolder.RootFolderId),
            Workflow("Hidden one", published: true, folder: _deniedFolder));
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static Workflow Workflow(string name, bool published, Guid folder, bool locked = false) => new()
    {
        Id = Guid.NewGuid(), Name = name, FolderId = folder, IsEnabled = true,
        PublishedByUserId = published ? Guid.NewGuid() : null,
        CheckedOutByUserId = locked ? Guid.NewGuid() : null
    };

    private (AiAgentTeamController controller, CapturingAuditWriter audit, FakeLlmClient llm) NewController(
        bool enabled = true, string role = "Operator", AgentOptions? agentOptions = null)
    {
        var llmOptions = new StaticOptionsMonitor<LlmOptions>(LlmTestOptions.WithProfile(
            enabled: enabled, baseUrl: "http://localhost/v1", model: "test-model", maxTokens: 100, timeoutSeconds: 30));
        var llm = new FakeLlmClient();
        var drafting = new AgentTeamDraftingService(new FakeLlmClientFactory(llm), new PromptCatalog());

        var authz = new Mock<IResourceAuthorizationService>();
        authz.Setup(a => a.CanAccessWorkflowAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<Guid>(), ResourceOp.Run, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ClaimsPrincipal _, Guid folder, ResourceOp _, CancellationToken _) => folder != _deniedFolder);
        var agents = new StaticOptionsMonitor<AgentOptions>(agentOptions ?? new AgentOptions());
        var builder = new AgentTeamInventoryBuilder(_db, authz.Object, new AgentExternalReadPolicy(agents), agents);

        var audit = new CapturingAuditWriter();
        var controller = new AiAgentTeamController(llmOptions, drafting, builder, audit, NullLogger<AiAgentTeamController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.Name, "tester")], "TestAuth"))
                }
            }
        };
        return (controller, audit, llm);
    }

    private static string Team(string machine = "SRV-01", string credential = "svc-diag", string workflow = "Collect logs") => $$"""
        { "task": "Diagnose", "members": [
          { "id": "lead", "role": "Lead", "instructions": "Coordinate.", "isSupervisor": true },
          { "id": "analyst", "role": "Analyst", "instructions": "Read.", "machine": "{{machine}}", "credential": "{{credential}}",
            "tools": [ { "name": "workflow_run", "workflows": ["{{workflow}}"] },
                       { "name": "files_read", "paths": ["C:\\Logs"] } ] } ] }
        """;

    private const string Prompt = "Analyst reads C:\\Logs on SRV-01 with svc-diag and may run Collect logs";

    [Fact]
    public async Task Generate_WhenLlmDisabled_Returns503()
    {
        var (controller, _, _) = NewController(enabled: false);

        var result = await controller.GenerateAgentTeam(new GenerateAgentTeamRequest(Prompt, null), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Generate_EmptyPrompt_Returns400(string prompt)
    {
        var (controller, _, _) = NewController();

        var result = await controller.GenerateAgentTeam(new GenerateAgentTeamRequest(prompt, null), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Generate_TooLongPrompt_Returns400()
    {
        var (controller, _, _) = NewController();

        var result = await controller.GenerateAgentTeam(new GenerateAgentTeamRequest(new string('x', 8_001), null), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Generate_HappyPath_ResolvesIdsAndWritesAuditWithoutPrompt()
    {
        var (controller, audit, llm) = NewController();
        llm.EnqueueContent(Team());

        var result = await controller.GenerateAgentTeam(new GenerateAgentTeamRequest(Prompt, null), CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<GenerateAgentTeamResponse>().Subject;
        response.Issues.Should().BeEmpty();
        var analyst = response.Patch.Members.Single(m => m.Id == "analyst");
        analyst.TargetMachineId.Should().Be(_machineId);
        analyst.CredentialId.Should().Be(_credentialId);
        analyst.Tools.Select(t => t.Name).Should().BeEquivalentTo("workflow_run", "files_read");
        analyst.Tools.Single(t => t.Name == "workflow_run").WorkflowIds.Should().ContainSingle();

        var call = audit.Calls.Should().ContainSingle(c => c.Action == "AI_AGENT_TEAM_GENERATED").Subject;
        call.Details.Should().Contain("memberCount").And.NotContain("svc-diag").And.NotContain("SRV-01");
    }

    [Fact]
    public async Task Generate_PromptToModel_ListsOnlyRunnableWorkflowsAndNoSecrets()
    {
        var (controller, _, llm) = NewController();
        llm.EnqueueContent(Team());

        await controller.GenerateAgentTeam(new GenerateAgentTeamRequest(Prompt, null), CancellationToken.None);

        var user = llm.Calls.Single().UserPrompt;
        user.Should().Contain("Collect logs").And.NotContain("Locked one").And.NotContain("Draft one").And.NotContain("Hidden one");
        user.Should().Contain("svc-diag").And.NotContain("Username").And.NotContain(_credentialId.ToString());
    }

    [Fact]
    public async Task Generate_WorkflowInDeniedFolder_IsNotBound()
    {
        var (controller, _, llm) = NewController();
        llm.EnqueueContent(Team(workflow: "Hidden one"));

        var result = await controller.GenerateAgentTeam(
            new GenerateAgentTeamRequest(Prompt.Replace("Collect logs", "Hidden one"), null), CancellationToken.None);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<GenerateAgentTeamResponse>().Subject;
        response.Patch.Members.Single(m => m.Id == "analyst").Tools.Select(t => t.Name).Should().NotContain("workflow_run");
        response.Issues.Should().Contain(i => i.Code == "unresolved");
    }

    [Fact]
    public async Task Generate_UnknownMachine_ReturnsBlockingIssueNotAnError()
    {
        var (controller, _, llm) = NewController();
        llm.EnqueueContent(Team(machine: "SRV-77"));

        var result = await controller.GenerateAgentTeam(
            new GenerateAgentTeamRequest(Prompt.Replace("SRV-01", "SRV-77"), null), CancellationToken.None);

        var response = ((OkObjectResult)result.Result!).Value.Should().BeOfType<GenerateAgentTeamResponse>().Subject;
        response.Issues.Should().ContainSingle(i => i.Severity == "blocking" && i.Field == "machine");
        response.Patch.Members.Single(m => m.Id == "analyst").TargetMachineId.Should().BeNull();
    }

    [Fact]
    public async Task Generate_UnusableModelOutput_Returns502()
    {
        var (controller, audit, llm) = NewController();
        llm.EnqueueContent("nope").EnqueueContent("still nope");

        var result = await controller.GenerateAgentTeam(new GenerateAgentTeamRequest(Prompt, null), CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        audit.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Generate_McpApproval_OnlyCurrentRevisionIsOffered()
    {
        var server = new AgentMcpServer { Id = Guid.NewGuid(), Name = "docs", Enabled = true, UpdatedAt = DateTime.UtcNow };
        _db.AgentMcpServers.Add(server);
        await _db.SaveChangesAsync();
        var options = new AgentOptions
        {
            ReadOnlyMcpTools =
            [
                new AgentMcpReadGrant { ServerId = server.Id, ToolName = "search", ServerUpdatedAt = DateTime.SpecifyKind(server.UpdatedAt, DateTimeKind.Utc).ToString("O"), ContractSha256 = "x" },
                new AgentMcpReadGrant { ServerId = server.Id, ToolName = "stale", ServerUpdatedAt = DateTime.UtcNow.AddDays(-3).ToString("O"), ContractSha256 = "x" }
            ]
        };
        var (controller, _, llm) = NewController(agentOptions: options);
        llm.EnqueueContent(Team());

        await controller.GenerateAgentTeam(new GenerateAgentTeamRequest(Prompt, null), CancellationToken.None);

        var user = llm.Calls.Single().UserPrompt;
        user.Should().Contain("docs: search").And.NotContain("stale");
    }

    [Theory]
    [InlineData("Operator", false)]
    [InlineData("Admin", true)]
    public async Task Generate_ServiceIdentityIsOfferedToAdminsOnly(string role, bool offered)
    {
        var (controller, _, llm) = NewController(role: role, agentOptions: new AgentOptions { AllowServiceIdentity = true });
        llm.EnqueueContent(Team());

        await controller.GenerateAgentTeam(new GenerateAgentTeamRequest(Prompt, null), CancellationToken.None);

        llm.Calls.Single().UserPrompt.Should().Contain(offered ? "Service identity: available" : "Service identity: not available");
    }
}

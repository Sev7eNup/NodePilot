using System.Text.Json;
using FluentAssertions;
using NodePilot.Ai;
using NodePilot.Api.Ai;
using Xunit;

namespace NodePilot.Api.Tests.Ai;

public class AgentTeamDraftResolverTests
{
    private static readonly Guid Srv01 = Guid.NewGuid();
    private static readonly Guid Srv02 = Guid.NewGuid();
    private static readonly Guid Diag = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();
    private static readonly Guid Skill = Guid.NewGuid();
    private static readonly Guid Wf = Guid.NewGuid();
    private static readonly Guid Docs = Guid.NewGuid();

    private static AgentTeamResolvableInventory Inventory(bool serviceIdentity = false, params InventoryMachine[] extraMachines)
        => new(
            [new(Srv01, "SRV-01", "srv01.corp.local"), new(Srv02, "SRV-02", "srv02.corp.local"), .. extraMachines],
            [new(Diag, "svc-diag"), new(Admin, "svc-admin")],
            [new(Skill, "windows-diagnostics", "1.0.0", "d")],
            [new(Docs, "docs", ["search"])],
            [new(Wf, "Collect logs")],
            serviceIdentity);

    private static AgentTeamDraftMember Lead() => new("lead", "Lead", "Coordinate.", true, false, null, null, false, [], []);

    private static AgentTeamDraftMember Member(string? machine = null, string? credential = null, bool identity = false,
        IReadOnlyList<AgentTeamDraftTool>? tools = null, IReadOnlyList<AgentTeamDraftSkill>? skills = null, string id = "analyst")
        => new(id, "Analyst", "Read.", false, false, machine, credential, identity, skills ?? [], tools ?? []);

    private static AgentTeamDraftTool Tool(string name, string[]? paths = null, string[]? hosts = null,
        string[]? workflows = null, string? server = null, string? tool = null)
        => new(name, paths ?? [], hosts ?? [], workflows ?? [], server, tool);

    private static GenerateAgentTeamResponse Resolve(string prompt, AgentTeamDraftMember member,
        AgentTeamResolvableInventory? inventory = null, JsonElement? current = null, int? parallel = null, string? task = null,
        params AgentTeamDraftMember[] more)
    {
        var members = new List<AgentTeamDraftMember> { Lead(), member };
        members.AddRange(more);
        return AgentTeamDraftResolver.Resolve(
            new AgentTeamDraftResult(new AgentTeamDraft(task, parallel, members), false, 5, "m", null, null, null),
            prompt, inventory ?? Inventory(), current);
    }

    private static NodePilot.Core.Agents.AgentDefinition Def(GenerateAgentTeamResponse r, string id = "analyst")
        => r.Patch.Members.Single(m => m.Id == id);

    // ---- machine / credential binding ----------------------------------------------

    [Fact]
    public void Resolve_ExactMachineNameInRequest_BindsMachine()
    {
        var r = Resolve("Analyst works on SRV-01", Member(machine: "SRV-01"));

        Def(r).TargetMachineId.Should().Be(Srv01);
        r.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_HostnameInRequest_BindsMachine()
    {
        var r = Resolve("Analyst works on srv02.corp.local", Member(machine: "srv02.corp.local"));

        Def(r).TargetMachineId.Should().Be(Srv02);
    }

    [Fact]
    public void Resolve_UnknownMachine_IsBlockingAndDoesNotFallBackToAnotherMachine()
    {
        var r = Resolve("Analyst works on SRV-09", Member(machine: "SRV-09"));

        Def(r).TargetMachineId.Should().BeNull();
        var issue = r.Issues.Should().ContainSingle(i => i.Severity == "blocking").Subject;
        issue.Field.Should().Be("machine");
        issue.Code.Should().Be("unresolved");
        issue.MemberId.Should().Be("analyst");
        issue.Candidates.Should().NotBeEmpty();
    }

    [Fact]
    public void Resolve_AmbiguousMachine_IsBlockingWithOnlyTheMatchesAsCandidates()
    {
        var inventory = Inventory(false, new InventoryMachine(Guid.NewGuid(), "Shared", "srv01.corp.local"));

        var r = Resolve("Analyst works on srv01.corp.local", Member(machine: "srv01.corp.local"), inventory);

        var issue = r.Issues.Should().ContainSingle(i => i.Severity == "blocking").Subject;
        issue.Code.Should().Be("ambiguous");
        issue.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public void Resolve_MachineNotNamedInRequest_IsIgnoredWithWarning()
    {
        var r = Resolve("Analyst reads some logs", Member(machine: "SRV-01"));

        Def(r).TargetMachineId.Should().BeNull();
        r.Issues.Should().ContainSingle().Which.Should().Match<AgentTeamIssue>(i => i.Severity == "warning" && i.Code == "not_in_request");
    }

    [Fact]
    public void Resolve_ExistingCredentialNotNamedInRequest_IsNotBound()
    {
        var r = Resolve("Analyst works on SRV-01", Member(machine: "SRV-01", credential: "svc-admin"));

        Def(r).CredentialId.Should().BeNull();
        Def(r).TargetMachineId.Should().Be(Srv01);
        r.Issues.Should().ContainSingle(i => i.Field == "credential" && i.Severity == "warning");
    }

    [Fact]
    public void Resolve_CredentialNamedInRequest_BindsOnlyThatMember()
    {
        var second = Member(machine: "SRV-02", credential: "svc-admin", id: "second");

        var r = Resolve("analyst on SRV-01 with svc-diag, second on SRV-02 with svc-admin",
            Member(machine: "SRV-01", credential: "svc-diag"), more: second);

        Def(r).CredentialId.Should().Be(Diag);
        Def(r, "second").CredentialId.Should().Be(Admin);
        Def(r, "second").TargetMachineId.Should().Be(Srv02);
    }

    [Fact]
    public void Resolve_UnknownCredential_IsBlocking()
    {
        var r = Resolve("Analyst uses svc-nope", Member(credential: "svc-nope"));

        Def(r).CredentialId.Should().BeNull();
        r.Issues.Should().ContainSingle(i => i.Severity == "blocking" && i.Field == "credential");
    }

    [Fact]
    public void Resolve_NegatedCredentialStillNamed_IsBoundAndReviewedInPreview()
    {
        // Negation is semantic; the host cannot see it. This pins the documented limit: the
        // name appears in the request, so it resolves, and the preview is the control.
        var r = Resolve("Analyst on SRV-01, but not svc-admin", Member(machine: "SRV-01", credential: "svc-admin"));

        Def(r).CredentialId.Should().Be(Admin);
    }

    // ---- service identity ----------------------------------------------------------

    [Fact]
    public void Resolve_ServiceIdentityWithoutPhraseInRequest_IsNotEnabled()
    {
        var r = Resolve("Analyst on SRV-01", Member(machine: "SRV-01", identity: true), Inventory(serviceIdentity: true));

        Def(r).UseServiceIdentity.Should().BeFalse();
        r.Issues.Should().ContainSingle(i => i.Field == "serviceIdentity" && i.Severity == "warning");
    }

    [Fact]
    public void Resolve_ServiceIdentityRequestedButUnavailable_IsBlocking()
    {
        var r = Resolve("Analyst on SRV-01 using the service identity", Member(machine: "SRV-01", identity: true), Inventory(serviceIdentity: false));

        Def(r).UseServiceIdentity.Should().BeFalse();
        r.Issues.Should().ContainSingle(i => i.Field == "serviceIdentity" && i.Severity == "blocking" && i.Code == "not_available");
    }

    [Fact]
    public void Resolve_ServiceIdentityRequestedAndAvailable_IsEnabledAndDropsCredential()
    {
        var r = Resolve("Analyst on SRV-01 using the Dienstidentität, svc-diag", Member(machine: "SRV-01", credential: "svc-diag", identity: true), Inventory(serviceIdentity: true));

        Def(r).UseServiceIdentity.Should().BeTrue();
        Def(r).CredentialId.Should().BeNull();
    }

    // ---- paths ---------------------------------------------------------------------

    [Theory]
    [InlineData("Read the logs in C:\\Logs please", "C:\\Logs", true)]
    [InlineData("Read the logs in C:\\Logs please", "C:\\Logs\\App\\today.log", true)]
    [InlineData("Read the logs in C:\\Logs.", "C:\\Logs", true)]
    [InlineData("Read C:\\Program Files\\App logs", "C:\\Program Files\\App", true)]
    [InlineData("Read C:\\Program Files\\App logs", "C:\\Program Files\\App\\log.txt", true)]
    [InlineData("Read C:\\Logs", "C:\\", false)]
    [InlineData("Read C:\\Logs\\App", "C:\\Logs", false)]
    [InlineData("Read C:\\Logs", "C:\\Logs\\..\\Windows", false)]
    [InlineData("Read C:\\Logs", "D:\\Logs", false)]
    [InlineData("Read C:\\Logs", "Logs", false)]
    [InlineData("Read C:\\Logsold", "C:\\Logs", false)]
    [InlineData("Read the logs", "C:\\Logs", false)]
    [InlineData("Read C:\\Logs", "C:\\Logs-archive", false)]
    public void PathAllowed_OnlyPathsWrittenInTheRequestOrBelow(string prompt, string path, bool expected)
        => AgentTeamDraftResolver.PathAllowed(path, prompt).Should().Be(expected);

    [Fact]
    public void PathAllowed_DriveRootWrittenByTheUser_IsAllowed()
        => AgentTeamDraftResolver.PathAllowed("C:\\", "Look at C:\\ only").Should().BeTrue();

    [Fact]
    public void PathAllowed_ForwardSlashSpelling_MatchesBackslashRequest()
        => AgentTeamDraftResolver.PathAllowed("C:/Logs/app.log", "Read C:\\Logs").Should().BeTrue();

    [Fact]
    public void Resolve_InventedPathOnly_DropsFileToolWithWarnings()
    {
        var r = Resolve("Analyst reads logs in C:\\Logs", Member(tools: [Tool("files_read", paths: ["C:\\"])]));

        Def(r).Tools.Should().BeEmpty();
        r.Issues.Select(i => i.Code).Should().Contain(["path_rejected", "tool_dropped"]);
    }

    [Fact]
    public void Resolve_FileToolKeepsOnlyWrittenPaths()
    {
        var r = Resolve("Analyst reads C:\\Logs", Member(tools: [Tool("files_read", paths: ["C:\\Logs", "C:\\Windows"])]));

        Def(r).Tools.Single().AllowedPaths.Should().Equal("C:\\Logs");
    }

    // ---- tools ---------------------------------------------------------------------

    [Fact]
    public void Resolve_FilesWrite_IsRemoved()
    {
        var r = Resolve("Write to C:\\Logs", Member(tools: [Tool("files_write", paths: ["C:\\Logs"])]));

        Def(r).Tools.Should().BeEmpty();
        r.Issues.Should().ContainSingle(i => i.Code == "files_write_removed");
    }

    [Fact]
    public void Resolve_UnknownAndDuplicateTools_AreSkipped()
    {
        var r = Resolve("go", Member(tools: [Tool("telepathy"), Tool("powershell"), Tool("powershell")]));

        Def(r).Tools.Select(t => t.Name).Should().Equal("powershell");
        r.Issues.Should().HaveCount(2).And.OnlyContain(i => i.Code == "unknown_tool");
    }

    [Fact]
    public void Resolve_HttpWithoutHosts_IsKeptAndFlaggedUnrestricted()
    {
        var r = Resolve("Check the web", Member(tools: [Tool("http_request")]));

        Def(r).Tools.Single().AllowedHosts.Should().BeEmpty();
        r.Issues.Should().ContainSingle(i => i.Code == "http_unrestricted");
    }

    [Fact]
    public void Resolve_HttpWithOnlyInventedHosts_DropsToolInsteadOfLeavingItUnrestricted()
    {
        var r = Resolve("Check status.example.com", Member(tools: [Tool("http_request", hosts: ["evil.example.net"])]));

        Def(r).Tools.Should().BeEmpty();
        r.Issues.Select(i => i.Code).Should().Contain(["host_rejected", "tool_dropped"]);
    }

    [Fact]
    public void Resolve_HttpHostFromRequest_IsRestrictedToIt()
    {
        var r = Resolve("Check https://status.example.com/health", Member(tools: [Tool("http_request", hosts: ["status.example.com", "other.example.org"])]));

        Def(r).Tools.Single().AllowedHosts.Should().Equal("status.example.com");
    }

    [Fact]
    public void Resolve_WorkflowRunByExactNameInRequest_BindsId()
    {
        var r = Resolve("Run the workflow Collect logs", Member(tools: [Tool("workflow_run", workflows: ["Collect logs"])]));

        Def(r).Tools.Single().WorkflowIds.Should().Equal(Wf);
    }

    [Fact]
    public void Resolve_WorkflowNotRunnable_DropsTool()
    {
        var r = Resolve("Run the workflow Nuke it", Member(tools: [Tool("workflow_run", workflows: ["Nuke it"])]));

        Def(r).Tools.Should().BeEmpty();
        r.Issues.Select(i => i.Code).Should().Contain(["unresolved", "tool_dropped"]);
    }

    [Fact]
    public void Resolve_McpToolWithCurrentApproval_IsBound()
    {
        var r = Resolve("Use docs search", Member(tools: [Tool("mcp", server: "docs", tool: "search")]));

        var t = Def(r).Tools.Single();
        t.McpServerId.Should().Be(Docs);
        t.McpToolName.Should().Be("search");
    }

    [Fact]
    public void Resolve_McpToolWithoutApproval_IsDropped()
    {
        var r = Resolve("Use docs delete", Member(tools: [Tool("mcp", server: "docs", tool: "delete")]));

        Def(r).Tools.Should().BeEmpty();
        r.Issues.Should().ContainSingle(i => i.Code == "no_valid_grant");
    }

    [Fact]
    public void Resolve_Skill_ResolvesByNameAndVersion()
    {
        var r = Resolve("Use windows-diagnostics", Member(skills: [new("windows-diagnostics", "1.0.0")]));

        Def(r).SkillIds.Should().Equal(Skill);
    }

    [Fact]
    public void Resolve_SkillNotInRequest_IsSkippedWithWarning()
    {
        var r = Resolve("Use some skill", Member(skills: [new("windows-diagnostics", null)]));

        Def(r).SkillIds.Should().BeEmpty();
        r.Issues.Should().ContainSingle(i => i.Field == "skill");
    }

    [Fact]
    public void Resolve_ReturnsDisplayNamesForEveryReferencedId()
    {
        var r = Resolve("Analyst on SRV-01 with svc-diag, windows-diagnostics, Collect logs and docs search",
            Member(machine: "SRV-01", credential: "svc-diag", skills: [new("windows-diagnostics", "1.0.0")],
                tools: [Tool("workflow_run", workflows: ["Collect logs"]), Tool("mcp", server: "docs", tool: "search")]));

        r.Names[Srv01].Should().Be("SRV-01");
        r.Names[Diag].Should().Be("svc-diag");
        r.Names[Skill].Should().Be("windows-diagnostics 1.0.0");
        r.Names[Wf].Should().Be("Collect logs");
        r.Names[Docs].Should().Be("docs");
    }

    // ---- structure and merge -------------------------------------------------------

    [Fact]
    public void Resolve_DuplicateAndInvalidIds_AreMadeUniqueAndValid()
    {
        var r = Resolve("go", Member(id: "my agent!"), more: [Member(id: "my agent!"), Member(id: "")]);

        r.Patch.Members.Select(m => m.Id).Should().OnlyHaveUniqueItems()
            .And.OnlyContain(id => System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,64}$"));
    }

    [Fact]
    public void Resolve_NoExistingTask_UsesDraftTaskThenRequest()
    {
        Resolve("the request text", Member(), task: "Draft task").Patch.Task.Should().Be("Draft task");
        Resolve("the request text", Member(), task: null).Patch.Task.Should().Be("the request text");
    }

    [Fact]
    public void Resolve_ExistingTask_IsKept()
    {
        var current = JsonSerializer.SerializeToElement(new { task = "Existing task" });

        Resolve("the request text", Member(), current: current, task: "Draft task").Patch.Task.Should().Be("Existing task");
    }

    [Fact]
    public void Resolve_ParallelLimit_DraftWinsOverExistingAndIsClamped()
    {
        var current = JsonSerializer.SerializeToElement(new { task = "t", maxParallelMembers = 1 });

        Resolve("go", Member(), current: current, parallel: 2).Patch.MaxParallelMembers.Should().Be(1); // 2 members -> at most 1 non-supervisor
        Resolve("go", Member(), current: current).Patch.MaxParallelMembers.Should().Be(1);
        Resolve("go", Member()).Patch.MaxParallelMembers.Should().BeNull();
    }

    [Fact]
    public void Resolve_ExistingBudgets_ArePartOfTheValidatedState()
    {
        var current = JsonSerializer.SerializeToElement(new { task = "t", maxModelCalls = 1 });

        var r = Resolve("go", Member(), current: current);

        r.Issues.Should().ContainSingle(i => i.Severity == "blocking" && i.Code == "invalid_config");
    }

    [Fact]
    public void Resolve_ValidDraft_HasNoIssuesAndMatchesValidatedState()
    {
        var current = JsonSerializer.SerializeToElement(new { task = "t", timeoutSeconds = 0, resultFormat = "text" });

        var r = Resolve("go", Member(), current: current);

        r.Issues.Should().BeEmpty();
        r.Patch.Members.Should().HaveCount(2);
    }
}

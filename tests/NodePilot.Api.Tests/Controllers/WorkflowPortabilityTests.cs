using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Agents;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Core.WorkflowDefinitions;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public sealed class WorkflowPortabilityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static WorkflowExportEnvelope Envelope(WorkflowExportItem item) => new("nodepilot-workflow-export/v1", 1, DateTime.UtcNow, item, null);
    private static WorkflowExportItem Item(string definition, List<WorkflowDependency>? dependencies = null) =>
        new("Imported", null, JsonDocument.Parse(definition).RootElement.Clone(), Dependencies: dependencies);
    private static string Definition(Guid machine, Guid credential, Guid skill, Guid mcp, Guid workflow) => $$$$"""
        {"nodes":[{"id":"team","type":"activity","data":{"activityType":"aiAgentTeam","targetMachineId":"{{{{machine}}}}","credentialId":"{{{{credential}}}}","config":{"task":"Inspect","members":[
        {"id":"supervisor","role":"Supervisor","isSupervisor":true,"tools":[],"skillIds":[]},
        {"id":"worker","role":"Worker","targetMachineId":"{{{{machine}}}}","credentialId":"{{{{credential}}}}","skillIds":["{{{{skill}}}}"],"tools":[{"name":"mcp","mcpServerId":"{{{{mcp}}}}","mcpToolName":"read_status"},{"name":"workflow_run","workflowIds":["{{{{workflow}}}}"]}]}]}}}],"edges":[]}
        """;

    private static async Task<(Guid Machine, Guid Credential, Guid Skill, Guid Mcp, Guid Workflow)> Seed(NodePilotDbContext db)
    {
        var ids = (Machine: Guid.NewGuid(), Credential: Guid.NewGuid(), Skill: Guid.NewGuid(), Mcp: Guid.NewGuid(), Workflow: Guid.NewGuid());
        db.ManagedMachines.Add(new() { Id = ids.Machine, Name = "Target", Hostname = "target.example" });
        db.Credentials.Add(new() { Id = ids.Credential, Name = "Reader", Username = "reader", Domain = "EXAMPLE", EncryptedPassword = [1, 2, 3] });
        db.AgentSkillPackages.Add(new() { Id = ids.Skill, Name = "diagnose", Version = "1.0", Sha256 = new string('a', 64), Package = [4, 5, 6] });
        db.AgentMcpServers.Add(new() { Id = ids.Mcp, Name = "Inventory", Transport = "streamableHttp", Endpoint = "https://private.invalid?token=do-not-export", ProtectedSecrets = [7, 8, 9] });
        db.Workflows.Add(new() { Id = ids.Workflow, Name = "Child", DefinitionJson = "{\"nodes\":[],\"edges\":[]}" });
        await db.SaveChangesAsync(Ct);
        return ids;
    }

    [Fact]
    public async Task ExportImportAcrossDatabases_RemapsAllNestedReferences_WithoutSecrets()
    {
        using var source = NodePilot.TestCommons.TestDbFactory.Create();
        using var target = NodePilot.TestCommons.TestDbFactory.Create();
        var old = await Seed(source);
        var next = await Seed(target);
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Team", DefinitionJson = Definition(old.Machine, old.Credential, old.Skill, old.Mcp, old.Workflow) };
        source.Workflows.Add(workflow);
        await source.SaveChangesAsync(Ct);
        var export = (ContentResult)await WorkflowControllerHarnessFactory.Build(source).ImportExport.ExportOne(workflow.Id, Ct);
        export.Content.Should().NotContain("do-not-export").And.NotContain("encryptedPassword").And.NotContain("protectedSecrets").And.NotContain("package\"");
        var envelope = JsonSerializer.Deserialize<WorkflowExportEnvelope>(export.Content!, Json)!;
        envelope.Workflow!.SourceId.Should().Be(workflow.Id);
        envelope.Workflow.Dependencies.Should().HaveCount(5);
        var h = WorkflowControllerHarnessFactory.Build(target);
        var response = (ImportWorkflowsResponse)((OkObjectResult)(await h.ImportExport.Import(envelope, null, Ct)).Result!).Value!;
        response.Errors.Should().BeEmpty();
        var imported = await target.Workflows.SingleAsync(x => x.Id == response.Workflows[0].Id, Ct);
        imported.IsEnabled.Should().BeFalse();
        var references = WorkflowResourceReferences.Enumerate(JsonNode.Parse(imported.DefinitionJson)).ToList();
        references.Where(x => x.Kind == "machine").Should().OnlyContain(x => x.Id == next.Machine).And.HaveCount(2);
        references.Where(x => x.Kind == "credential").Should().OnlyContain(x => x.Id == next.Credential).And.HaveCount(2);
        references.Single(x => x.Kind == "skill").Id.Should().Be(next.Skill);
        references.Single(x => x.Kind == "mcpServer").Id.Should().Be(next.Mcp);
        references.Single(x => x.Kind == "workflow").Id.Should().Be(next.Workflow);
    }

    [Fact]
    public async Task BulkImport_RelinksToNewChild_EvenWhenExistingNamesCollide()
    {
        using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var ids = await Seed(db);
        var child = Item("{\"nodes\":[],\"edges\":[]}") with { Name = "Child", SourceId = ids.Workflow };
        var parent = Item($$$$"""{"nodes":[{"id":"call","type":"startWorkflow","data":{"config":{"workflowNameOrId":"{{{{ids.Workflow}}}}"}}}],"edges":[]}""",
            [new("workflow", ids.Workflow, "Child")]) with { SourceId = Guid.NewGuid() };
        var envelope = new WorkflowExportEnvelope("nodepilot-workflow-export/v1", 1, DateTime.UtcNow, null, [parent, child]);
        var response = (ImportWorkflowsResponse)((OkObjectResult)(await WorkflowControllerHarnessFactory.Build(db).ImportExport.Import(envelope, null, Ct)).Result!).Value!;
        response.Errors.Should().BeEmpty();
        var newChild = response.Workflows.Single(x => x.OriginalName == "Child");
        var imported = await db.Workflows.SingleAsync(x => x.Id == response.Workflows[0].Id, Ct);
        WorkflowResourceReferences.Enumerate(JsonNode.Parse(imported.DefinitionJson)).Single().Id.Should().Be(newChild.Id).And.NotBe(ids.Workflow);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("legacy")]
    [InlineData("duplicate")]
    [InlineData("wrong-identity")]
    public async Task UnresolvedReferences_AreNotRetained_AndCannotBeEnabled(string scenario)
    {
        using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var oldId = Guid.NewGuid();
        if (scenario != "missing") db.ManagedMachines.Add(new() { Id = oldId, Name = "Server", Hostname = "server" });
        if (scenario == "ambiguous") db.ManagedMachines.Add(new() { Id = Guid.NewGuid(), Name = "Server", Hostname = "server" });
        await db.SaveChangesAsync(Ct);
        var dependency = new WorkflowDependency("machine", oldId, "Server", scenario == "wrong-identity" ? "other:5985:http" : "server:5985:http");
        var item = Item($$$$"""{"nodes":[{"id":"script","type":"runScript","data":{"targetMachineId":"{{{{oldId}}}}","config":{}}}],"edges":[]}""",
            scenario == "legacy" ? null : scenario == "duplicate" ? [dependency, dependency] : [dependency]);
        var h = WorkflowControllerHarnessFactory.Build(db);
        var response = (ImportWorkflowsResponse)((OkObjectResult)(await h.ImportExport.Import(Envelope(item), null, Ct)).Result!).Value!;
        response.Created.Should().Be(1);
        response.Errors.Should().ContainSingle().Which.Should().Contain("unresolved machine");
        var saved = await db.Workflows.SingleAsync(Ct);
        WorkflowResourceReferences.Enumerate(JsonNode.Parse(saved.DefinitionJson)).Single().Id.Should().Be(Guid.Empty);
        (await h.Workflows.Enable(saved.Id, Ct)).Should().BeOfType<BadRequestObjectResult>();
        saved.CheckedOutByUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await db.SaveChangesAsync(Ct);
        var publish = await h.Editing.Publish(saved.Id, new(saved.Name, null, saved.DefinitionJson), Ct);
        publish.Result.Should().BeOfType<BadRequestObjectResult>();
        saved.DefinitionJson = saved.DefinitionJson.Replace(Guid.Empty.ToString(), Guid.NewGuid().ToString());
        WorkflowResourceReferences.UnresolvedError(saved.DefinitionJson).Should().BeNull();
    }

    [Fact]
    public void SkillVersionAndHashMustMatch_ExplicitMappingCanSelectDifferentDestination()
    {
        var old = Guid.NewGuid(); var target = Guid.NewGuid();
        var service = new WorkflowPortability([new("skill", target, "diagnose", Version: "2", Sha256: "bbb")]);
        var definition = Definition(Guid.NewGuid(), Guid.NewGuid(), old, Guid.NewGuid(), Guid.NewGuid());
        var dependency = new WorkflowDependency("skill", old, "diagnose", Version: "1", Sha256: "aaa");
        var errors = new List<string>();
        var output = service.Remap(definition, [dependency], new Dictionary<Guid, Guid>(), errors, "test");
        WorkflowResourceReferences.Enumerate(JsonNode.Parse(output)).Single(x => x.Kind == "skill").Id.Should().Be(Guid.Empty);
        output = service.Remap(definition, [dependency with { TargetId = target }], new Dictionary<Guid, Guid>(), [], "test");
        WorkflowResourceReferences.Enumerate(JsonNode.Parse(output)).Single(x => x.Kind == "skill").Id.Should().Be(target);
    }

    [Fact]
    public async Task InaccessibleWorkflowMetadataAndExplicitMapping_AreDenied()
    {
        using var db = NodePilot.TestCommons.TestDbFactory.Create();
        var ids = await Seed(db);
        var auth = new Mock<IResourceAuthorizationService>();
        auth.Setup(x => x.GetAccessibleFolderIdsAsync(It.IsAny<System.Security.Claims.ClaimsPrincipal>(), It.IsAny<CancellationToken>())).ReturnsAsync(AccessibleFolderSet.None);
        var service = await WorkflowPortability.LoadAsync(db, auth.Object, new(), Ct);
        var definition = Definition(ids.Machine, ids.Credential, ids.Skill, ids.Mcp, ids.Workflow);
        service.Describe(definition).Single(x => x.Kind == "workflow").Name.Should().BeNull();
        var output = service.Remap(definition, [new("workflow", ids.Workflow, "Child", TargetId: ids.Workflow)], new Dictionary<Guid, Guid>(), [], "test");
        WorkflowResourceReferences.Enumerate(JsonNode.Parse(output)).Single(x => x.Kind == "workflow").Id.Should().Be(Guid.Empty);
    }

    [Fact]
    public void ReferenceWalker_DoesNotRewritePromptsOrArbitraryObjects()
    {
        var id = Guid.NewGuid();
        var definition = JsonNode.Parse($$$$"""{"nodes":[{"id":"a","type":"aiAgent","data":{"config":{"task":"{{{{id}}}}","agent":{"tools":[{"name":"http_request","body":{"credentialId":"{{{{id}}}}"}}],"skillIds":["{{{{id}}}}"]}}} }]}""");
        var references = WorkflowResourceReferences.Enumerate(definition).ToList();
        references.Should().ContainSingle().Which.Kind.Should().Be("skill");
        references[0].Replace(Guid.Empty);
        definition!.ToJsonString().Should().Contain($"\"task\":\"{id}\"").And.Contain($"\"credentialId\":\"{id}\"");
    }

    [Fact]
    public void SkippedChild_DoesNotFallBackToAnExistingNamesake()
    {
        var source = Guid.NewGuid();
        var dependency = new WorkflowDependency("workflow", source, "Child");
        var service = new WorkflowPortability([dependency with { SourceId = Guid.NewGuid() }]);
        var definition = $$$$"""{"nodes":[{"id":"each","type":"forEach","data":{"config":{"childWorkflowNameOrId":"{{{{source}}}}"}}}],"edges":[]}""";
        var errors = new List<string>();
        var output = service.Remap(definition, [dependency], new Dictionary<Guid, Guid> { [source] = Guid.Empty }, errors, "parent");
        WorkflowResourceReferences.Enumerate(JsonNode.Parse(output)).Single().Id.Should().Be(Guid.Empty);
        errors.Should().ContainSingle().Which.Should().Contain("was not created");
    }
}

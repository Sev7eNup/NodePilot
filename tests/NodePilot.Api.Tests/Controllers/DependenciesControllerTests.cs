using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class DependenciesControllerTests
{
    private static DependenciesController Controller(NodePilotDbContext db, string role = "Admin",
        IResourceAuthorizationService? authz = null, WorkflowDefinitionFactsCache? cache = null)
        => new(db, authz ?? new AlwaysAllowAuthorizationService(), cache ?? new())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test")) } }
        };
    private static T Value<T>(ActionResult<T> result) => result.Result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<T>().Subject;
    private static Workflow Workflow(string name, Guid machine, Guid? credential = null) => new()
    {
        Id = Guid.NewGuid(), Name = name, FolderId = SharedWorkflowFolder.RootFolderId,
        DefinitionJson = JsonSerializer.Serialize(new { nodes = new[] { new { id = "copy", type = "activity", data = new
        { activityType = "runScript", label = "Copy files", targetMachineId = machine.ToString(), credentialId = credential?.ToString(), config = new { script = "Write-Output 'NEVER_RETURN_SCRIPT'" } } } }, edges = Array.Empty<object>() })
    };

    [Fact]
    public async Task Report_CountsAndEvidence_KeepSecretsAndScriptsOut()
    {
        using var db = TestDbFactory.Create();
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "File server", Hostname = "fs" };
        var credential = new Credential { Id = Guid.NewGuid(), Name = "Service", EncryptedPassword = [1, 2, 3] };
        db.ManagedMachines.Add(machine); db.Credentials.Add(credential);
        var workflow = Workflow("Archive", machine.Id, credential.Id); db.Workflows.Add(workflow); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = Controller(db);
        var report = Value(await controller.Report("machine", machine.Id, TestContext.Current.CancellationToken));
        report.Counts.Direct.Should().Be(1);
        report.Items[0].Paths[0].Links[0].Locations[0].NodeId.Should().Be("copy");
        JsonSerializer.Serialize(report).Should().NotContain("NEVER_RETURN_SCRIPT").And.NotContain("EncryptedPassword");
        var evidence = Value(await controller.Evidence("machine", machine.Id, TestContext.Current.CancellationToken, "workflow", workflow.Id));
        evidence.Total.Should().Be(1); evidence.Items[0].SourceId.Should().Be(workflow.Id);
        evidence.AnalysisRevision.Should().Be(report.AnalysisRevision);
    }

    [Fact]
    public async Task Report_RestrictedUserAfterAdminCache_DoesNotLeakHiddenWorkflows()
    {
        using var db = TestDbFactory.Create(); var cache = new WorkflowDefinitionFactsCache();
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "Host", Hostname = "host" };
        db.ManagedMachines.Add(machine); var hidden = Workflow("Secret workflow", machine.Id); db.Workflows.Add(hidden);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Value(await Controller(db, cache: cache).Report("machine", machine.Id, TestContext.Current.CancellationToken)).Counts.Direct.Should().Be(1);
        var restricted = Controller(db, "Operator", new RestrictedAuthorizationService(AccessibleFolderSet.None), cache);
        var report = Value(await restricted.Report("machine", machine.Id, TestContext.Current.CancellationToken));
        report.Counts.Direct.Should().Be(0); report.Items.Should().BeEmpty();
        JsonSerializer.Serialize(report).Should().NotContain(hidden.Id.ToString()).And.NotContain(hidden.Name);
        (await restricted.Evidence("machine", machine.Id, TestContext.Current.CancellationToken, "workflow", hidden.Id)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Objects_Viewer_CannotDiscoverCredentialsOrVariables()
    {
        using var db = TestDbFactory.Create();
        var credential = new Credential { Id = Guid.NewGuid(), Name = "Private credential" };
        db.Credentials.Add(credential); db.GlobalVariables.Add(new GlobalVariable { Id = Guid.NewGuid(), Name = "SECRET", Value = "private", IsSecret = true });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = Controller(db, "Viewer");
        Value(await controller.Objects(TestContext.Current.CancellationToken)).Items.Should().NotContain(o => o.Object.Kind == "credential" || o.Object.Kind == "variable");
        (await controller.Report("credential", credential.Id, TestContext.Current.CancellationToken)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Report_DefaultCredentialChange_RefreshesWithoutWorkflowSave()
    {
        using var db = TestDbFactory.Create(); var cache = new WorkflowDefinitionFactsCache();
        var first = new Credential { Id = Guid.NewGuid(), Name = "First" }; var second = new Credential { Id = Guid.NewGuid(), Name = "Second" };
        db.Credentials.AddRange(first, second);
        var machine = new ManagedMachine { Id = Guid.NewGuid(), Name = "Host", Hostname = "host", DefaultCredentialId = first.Id };
        db.ManagedMachines.Add(machine); db.Workflows.Add(Workflow("Archive", machine.Id)); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = Controller(db, cache: cache);
        var before = Value(await controller.Report("credential", first.Id, TestContext.Current.CancellationToken));
        before.Counts.Direct.Should().Be(2);
        machine.DefaultCredentialId = second.Id; await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var after = Value(await controller.Report("credential", first.Id, TestContext.Current.CancellationToken));
        after.Counts.Direct.Should().Be(0); after.AnalysisRevision.Should().NotBe(before.AnalysisRevision);
        Value(await controller.Report("credential", second.Id, TestContext.Current.CancellationToken)).Counts.Direct.Should().Be(2);
    }

    [Fact]
    public async Task Objects_PaginationRejectsChangedSnapshotAndMismatchedSearch()
    {
        using var db = TestDbFactory.Create();
        db.ManagedMachines.AddRange(new ManagedMachine { Id = Guid.NewGuid(), Name = "A", Hostname = "a" }, new ManagedMachine { Id = Guid.NewGuid(), Name = "B", Hostname = "b" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken); var controller = Controller(db);
        var first = Value(await controller.Objects(TestContext.Current.CancellationToken, kind: "machine", limit: 1));
        first.NextCursor.Should().NotBeNull();
        var second = Value(await controller.Objects(TestContext.Current.CancellationToken, kind: "machine", cursor: first.NextCursor, limit: 1));
        second.Items.Single().Object.Name.Should().Be("B");
        (await controller.Objects(TestContext.Current.CancellationToken, q: "A", kind: "machine", cursor: first.NextCursor)).Result.Should().BeOfType<ConflictObjectResult>();
        db.ManagedMachines.Add(new ManagedMachine { Id = Guid.NewGuid(), Name = "C", Hostname = "c" }); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await controller.Objects(TestContext.Current.CancellationToken, kind: "machine", cursor: first.NextCursor)).Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Report_InvalidDefinition_IsNotSuccessfulEmptyAnalysis()
    {
        using var db = TestDbFactory.Create();
        var broken = new Workflow { Id = Guid.NewGuid(), Name = "Broken", DefinitionJson = "bad", FolderId = SharedWorkflowFolder.RootFolderId };
        db.Workflows.Add(broken); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var report = Value(await Controller(db).Report("workflow", broken.Id, TestContext.Current.CancellationToken, "uses"));
        report.Problems.Should().Contain(p => p.Status == "invalid"); report.Limitations.Should().Contain("invalidDefinition");
    }

    [Fact]
    public async Task Report_InvalidParameters_AreRejected()
    {
        using var db = TestDbFactory.Create(); var controller = Controller(db);
        (await controller.Report("secret", Guid.NewGuid(), TestContext.Current.CancellationToken)).Result.Should().BeOfType<BadRequestResult>();
        (await controller.Report("machine", Guid.NewGuid(), TestContext.Current.CancellationToken, "wrong")).Result.Should().BeOfType<BadRequestResult>();
        (await controller.Objects(TestContext.Current.CancellationToken, q: new string('x', 201))).Result.Should().BeOfType<BadRequestResult>();
        (await controller.Objects(TestContext.Current.CancellationToken, state: "orphaned")).Result.Should().BeOfType<BadRequestResult>();
        (await controller.Objects(TestContext.Current.CancellationToken, sort: "risk")).Result.Should().BeOfType<BadRequestResult>();
    }

    [Fact]
    public async Task Objects_ReportsUsageAndProblems_AndSortsByUsage()
    {
        using var db = TestDbFactory.Create();
        var busy = new ManagedMachine { Id = Guid.NewGuid(), Name = "B busy", Hostname = "busy" };
        var idle = new ManagedMachine { Id = Guid.NewGuid(), Name = "A idle", Hostname = "idle" };
        db.ManagedMachines.AddRange(busy, idle);
        var child = Workflow("Child", busy.Id); var other = Workflow("Other", busy.Id);
        var parent = new Workflow { Id = Guid.NewGuid(), Name = "Parent", FolderId = SharedWorkflowFolder.RootFolderId,
            DefinitionJson = JsonSerializer.Serialize(new { nodes = new object[] {
                new { id = "call", type = "activity", data = new { activityType = "startWorkflow", label = "Call child", config = new { workflowNameOrId = child.Id.ToString() } } },
                new { id = "lost", type = "activity", data = new { activityType = "startWorkflow", label = "Call missing", config = new { workflowNameOrId = "Does not exist" } } },
            }, edges = Array.Empty<object>() }) };
        db.Workflows.AddRange(child, other, parent); await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var controller = Controller(db);

        var machines = Value(await controller.Objects(TestContext.Current.CancellationToken, kind: "machine", sort: "usage")).Items;
        machines.Select(m => m.Object.Name).Should().Equal("B busy", "A idle");
        machines[0].UsedByDirect.Should().Be(2); machines[0].UsedByIndirect.Should().Be(1);
        machines[1].UsedByDirect.Should().Be(0);
        var byName = Value(await controller.Objects(TestContext.Current.CancellationToken, kind: "machine")).Items;
        byName.Select(m => m.Object.Name).Should().Equal("A idle", "B busy");

        var problems = Value(await controller.Objects(TestContext.Current.CancellationToken, state: "problems")).Items;
        problems.Should().ContainSingle().Which.Should().Match<DependencyObjectSummaryDto>(p => p.Object.Id == parent.Id && p.Problems == 1);
    }

    [Fact]
    public async Task Objects_Unused_ListsResourcesWithoutDirectUseButNeverWorkflows()
    {
        using var db = TestDbFactory.Create();
        var used = new ManagedMachine { Id = Guid.NewGuid(), Name = "Used", Hostname = "used" };
        var unused = new ManagedMachine { Id = Guid.NewGuid(), Name = "Unused", Hostname = "unused" };
        var spare = new Credential { Id = Guid.NewGuid(), Name = "Spare" };
        db.ManagedMachines.AddRange(used, unused); db.Credentials.Add(spare);
        db.Workflows.Add(Workflow("Uncalled", used.Id)); await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var items = Value(await Controller(db).Objects(TestContext.Current.CancellationToken, state: "unused")).Items;
        items.Select(i => i.Object.Id).Should().BeEquivalentTo([unused.Id, spare.Id]);
    }
}

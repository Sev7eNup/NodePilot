using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Models;
using NodePilot.Data;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// End-to-end coverage for the import/export controller. ExportAll/ExportOne and the
/// ImportEnvelope path are critical for migrations and contain edge cases (name
/// uniqueness, validation failures) that aren't touched by the existing
/// WorkflowsController test set.
/// </summary>
public class WorkflowImportExportControllerTests
{
    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static WorkflowControllerHarness NewController(NodePilotDbContext db, string role = "Admin") =>
        WorkflowControllerHarnessFactory.Build(db, role: role);

    private static (WorkflowControllerHarness h, CapturingAuditWriter audit) NewControllerWithAudit(
        NodePilotDbContext db, string role = "Admin")
    {
        var audit = new CapturingAuditWriter();
        var h = WorkflowControllerHarnessFactory.Build(db, audit: audit, role: role);
        return (h, audit);
    }

    private static WorkflowExportItem ItemFor(string name, string definitionJson, bool? enabled = null) =>
        new(
            Name: name,
            Description: null,
            Definition: JsonDocument.Parse(definitionJson).RootElement.Clone(),
            IsEnabled: enabled);

    private static WorkflowExportEnvelope EnvelopeWithSingle(string name, string definitionJson, bool? enabled = null) =>
        new(
            Schema: "nodepilot-workflow-export/v1",
            ExportVersion: 1,
            ExportedAt: DateTime.UtcNow,
            Workflow: ItemFor(name, definitionJson, enabled),
            Workflows: null);

    private static WorkflowExportEnvelope EnvelopeWithMany(params WorkflowExportItem[] items) =>
        new(
            Schema: "nodepilot-workflow-export/v1",
            ExportVersion: 1,
            ExportedAt: DateTime.UtcNow,
            Workflow: null,
            Workflows: items.ToList());

    [Fact]
    public async Task ExportOne_NotFound_Returns404()
    {
        var db = CreateContext();
        var h = NewController(db);

        var result = await h.ImportExport.ExportOne(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ExportOne_ReturnsValidEnvelopeWithSingleWorkflow()
    {
        var db = CreateContext();
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Daily-Report",
            Description = "Sends daily ops digest",
            DefinitionJson = """{"nodes":[],"edges":[]}""",
            IsEnabled = true,
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var result = await NewController(db).ImportExport.ExportOne(wf.Id, CancellationToken.None);

        // ExportEnvelopeResult returns a ContentResult (not FileContentResult) — body is the
        // JSON envelope rendered as application/json. The download-filename hint goes on the
        // Content-Disposition header set on Response inside the helper.
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Contain("json");

        using var doc = JsonDocument.Parse(content.Content!);
        doc.RootElement.GetProperty("schema").GetString().Should().Be("nodepilot-workflow-export/v1");
        doc.RootElement.GetProperty("workflow").GetProperty("name").GetString().Should().Be("Daily-Report");
    }

    [Fact]
    public async Task ExportAll_TwoWorkflows_BundleHasBoth()
    {
        var db = CreateContext();
        db.Workflows.AddRange(
            new Workflow { Id = Guid.NewGuid(), Name = "Alpha", DefinitionJson = "{}" },
            new Workflow { Id = Guid.NewGuid(), Name = "Beta", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var result = await NewController(db).ImportExport.ExportAll(CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        var doc = JsonDocument.Parse(content.Content!);
        doc.RootElement.GetProperty("workflows").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Import_RejectsUnknownExportVersion()
    {
        var db = CreateContext();
        var h = NewController(db);
        var envelope = new WorkflowExportEnvelope(
            Schema: "nodepilot-workflow-export/v1",
            ExportVersion: 99,
            ExportedAt: DateTime.UtcNow,
            Workflow: null,
            Workflows: new List<WorkflowExportItem>());

        var result = await h.ImportExport.Import(envelope, null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Import_EmptyEnvelope_Returns400()
    {
        var db = CreateContext();
        var h = NewController(db);
        var envelope = new WorkflowExportEnvelope(
            Schema: "nodepilot-workflow-export/v1",
            ExportVersion: 1,
            ExportedAt: DateTime.UtcNow,
            Workflow: null,
            Workflows: null);

        var result = await h.ImportExport.Import(envelope, null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Import_TooManyItems_Returns400()
    {
        var db = CreateContext();
        var h = NewController(db);
        var items = Enumerable.Range(0, 501)
            .Select(i => ItemFor($"WF-{i}", """{"nodes":[],"edges":[]}"""))
            .ToArray();

        var result = await h.ImportExport.Import(EnvelopeWithMany(items), null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Import_SingleWorkflow_DefaultsToDisabled()
    {
        var db = CreateContext();
        var h = NewController(db);

        var result = await h.ImportExport.Import(
            EnvelopeWithSingle("Brand-New", """{"nodes":[],"edges":[]}"""),
            null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeOfType<ImportWorkflowsResponse>().Subject;
        resp.Created.Should().Be(1);
        resp.Workflows.Should().HaveCount(1);
        resp.Errors.Should().BeEmpty();

        var saved = await db.Workflows.AsNoTracking().FirstAsync();
        saved.Name.Should().Be("Brand-New");
        // Disabled-by-default: without an explicit `IsEnabled: true` in the envelope, the
        // imported workflow is created in a disabled state so its triggers don't fire
        // immediately, before an operator has had a chance to review the import.
        saved.IsEnabled.Should().BeFalse("Greenfield: imports require explicit enable post-review");
    }

    [Fact]
    public async Task Import_EnvelopeWithIsEnabledTrue_RespectsFlag()
    {
        var db = CreateContext();
        var h = NewController(db);

        var result = await h.ImportExport.Import(
            EnvelopeWithSingle("Pre-Enabled", """{"nodes":[],"edges":[]}""", enabled: true),
            null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ImportWorkflowsResponse>();
        var saved = await db.Workflows.AsNoTracking().FirstAsync();
        saved.IsEnabled.Should().BeTrue(
            "explicit IsEnabled=true in the envelope wins — only the missing-flag case defaults to disabled");
    }

    [Fact]
    public async Task Import_WeakHmacWebhookSecret_ForcesWorkflowDisabled()
    {
        var db = CreateContext();
        const string weakHmacDefinition = """
        {
          "nodes": [
            { "id": "hook", "type": "activity", "data": { "activityType": "webhookTrigger", "config": {
              "path": "hook", "method": "POST", "secret": "short", "signatureMode": "nodepilot-hmac-v2"
            } } }
          ],
          "edges": []
        }
        """;

        var result = await NewController(db).ImportExport.Import(
            EnvelopeWithSingle("Unsafe", weakHmacDefinition, enabled: true),
            null, CancellationToken.None);

        var response = (result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
                        as ImportWorkflowsResponse)!;
        response.Created.Should().Be(1);
        response.Errors.Should().ContainSingle().Which.Should().Contain("at least 32 UTF-8 bytes");
        var saved = await db.Workflows.SingleAsync();
        saved.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Import_NameCollision_AppendsSuffixAndReportsRename()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Daily-Report", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var result = await NewController(db).ImportExport.Import(
            EnvelopeWithSingle("Daily-Report", """{"nodes":[],"edges":[]}"""),
            null, CancellationToken.None);

        var resp = (result.Result.Should().BeOfType<OkObjectResult>().Subject.Value as ImportWorkflowsResponse)!;
        resp.Created.Should().Be(1);
        var created = resp.Workflows[0];
        created.OriginalName.Should().Be("Daily-Report",
            "the import response surfaces the original name when a rename happened");
        created.Name.Should().NotBe("Daily-Report");
        created.Name.Should().Contain("Daily-Report");
    }

    [Fact]
    public async Task Import_RespectsSourceIsEnabledFlag()
    {
        var db = CreateContext();
        var h = NewController(db);

        var result = await h.ImportExport.Import(
            EnvelopeWithSingle("Disabled-By-Source", """{"nodes":[],"edges":[]}""", enabled: false),
            null, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var saved = await db.Workflows.AsNoTracking().FirstAsync();
        saved.IsEnabled.Should().BeFalse("source-IsEnabled=false must round-trip");
    }

    [Fact]
    public async Task Import_MissingName_RecordsErrorAndContinues()
    {
        var db = CreateContext();
        var ok = ItemFor("OK", """{"nodes":[],"edges":[]}""");
        var bad = ok with { Name = "" };

        var result = await NewController(db).ImportExport.Import(
            EnvelopeWithMany(bad, ok),
            null, CancellationToken.None);

        var resp = (result.Result.Should().BeOfType<OkObjectResult>().Subject.Value as ImportWorkflowsResponse)!;
        resp.Created.Should().Be(1, "the well-formed entry must still get imported");
        resp.Errors.Should().ContainSingle().Which.Should().Contain("name is required");
    }

    [Fact]
    public async Task Import_DefinitionNotObject_RecordsError()
    {
        var db = CreateContext();
        var item = ItemFor("Bad", """[1,2]""");

        var result = await NewController(db).ImportExport.Import(
            EnvelopeWithMany(item),
            null, CancellationToken.None);

        var resp = (result.Result.Should().BeOfType<OkObjectResult>().Subject.Value as ImportWorkflowsResponse)!;
        resp.Created.Should().Be(0);
        resp.Errors.Should().ContainSingle().Which.Should().Contain("must be an object");
    }

    [Fact]
    public async Task ImportScorch_EmptyBody_Returns400()
    {
        var db = CreateContext();
        var h = NewController(db);
        h.ImportExport.Request.Body = new MemoryStream(Array.Empty<byte>());

        var result = await h.ImportExport.ImportScorch(null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ExportOne_EmitsWorkflowExportedAudit()
    {
        var db = CreateContext();
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Daily-Report",
            DefinitionJson = """{"nodes":[],"edges":[]}""",
            IsEnabled = true,
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();
        var (h, audit) = NewControllerWithAudit(db);

        await h.ImportExport.ExportOne(wf.Id, CancellationToken.None);

        var call = audit.Calls.Should().ContainSingle().Subject;
        call.Action.Should().Be("WORKFLOW_EXPORTED");
        call.ResourceType.Should().Be("Workflow");
        call.ResourceId.Should().Be(wf.Id);
        call.Details.Should().Contain("Daily-Report");
    }

    [Fact]
    public async Task ExportAll_EmitsBulkAudit_WithCount()
    {
        var db = CreateContext();
        db.Workflows.AddRange(
            new Workflow { Id = Guid.NewGuid(), Name = "A", DefinitionJson = "{}" },
            new Workflow { Id = Guid.NewGuid(), Name = "B", DefinitionJson = "{}" },
            new Workflow { Id = Guid.NewGuid(), Name = "C", DefinitionJson = "{}" });
        await db.SaveChangesAsync();
        var (h, audit) = NewControllerWithAudit(db);

        await h.ImportExport.ExportAll(CancellationToken.None);

        var call = audit.Calls.Should().ContainSingle().Subject;
        call.Action.Should().Be("WORKFLOW_EXPORTED_BULK");
        call.Details.Should().Contain("\"count\":\"3\"");
    }

    [Fact]
    public async Task ExportAll_RestrictedUserWithNoAccessibleFolders_StillEmitsBulkAudit()
    {
        // Regression: the early-return for accessible.FolderIds.Count == 0 used to bypass
        // the audit emission. An attempted catalogue-pull from a viewer who has no folder
        // access is exactly the signal SIEM wants to see (WORKFLOW_EXPORTED_BULK count=0
        // by a restricted principal).
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Hidden", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var restrictedAuthz = new RestrictedAuthorizationService(NodePilot.Core.Interfaces.AccessibleFolderSet.None);
        var controller = new WorkflowImportExportController(
            db, NullLogger<WorkflowImportExportController>.Instance, audit, restrictedAuthz,
            new NodePilot.Data.GlobalVariableStore(db, new NodePilot.Data.Security.DpapiSecretProtector(System.Security.Cryptography.DataProtectionScope.CurrentUser)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("test")) } }
        };

        await controller.ExportAll(CancellationToken.None);

        var call = audit.Calls.Should().ContainSingle().Subject;
        call.Action.Should().Be("WORKFLOW_EXPORTED_BULK");
        call.Details.Should().Contain("\"count\":\"0\"").And.Contain("\"rbacScope\":\"restricted\"");
    }

    [Fact]
    public async Task Import_EmitsAudit_WithCountAndIds()
    {
        var db = CreateContext();
        var (h, audit) = NewControllerWithAudit(db);

        await h.ImportExport.Import(
            EnvelopeWithMany(
                ItemFor("Alpha", """{"nodes":[],"edges":[]}"""),
                ItemFor("Beta", """{"nodes":[],"edges":[]}""")),
            null, CancellationToken.None);

        var call = audit.Calls.Should().ContainSingle(c => c.Action == "WORKFLOW_IMPORTED").Subject;
        call.Details.Should().Contain("\"created\":\"2\"");
        call.Details.Should().Contain("workflowIds");
        call.Details.Should().Contain("folderId");
    }

    [Fact]
    public async Task Import_WithoutFolderId_LandsInRoot()
    {
        var db = CreateContext();
        var h = NewController(db);

        var result = await h.ImportExport.Import(
            EnvelopeWithSingle("Rooted", """{"nodes":[],"edges":[]}"""),
            null, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var saved = await db.Workflows.AsNoTracking().FirstAsync();
        saved.FolderId.Should().Be(SharedWorkflowFolder.RootFolderId);
    }

    [Fact]
    public async Task Import_WithFolderId_LandsInThatFolder()
    {
        var db = CreateContext();
        var folder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(),
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Team-A",
            Path = "/Team-A",
        };
        db.SharedWorkflowFolders.Add(folder);
        await db.SaveChangesAsync();
        var h = NewController(db);

        var result = await h.ImportExport.Import(
            EnvelopeWithSingle("Scoped", """{"nodes":[],"edges":[]}"""),
            folder.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var saved = await db.Workflows.AsNoTracking().FirstAsync();
        saved.FolderId.Should().Be(folder.Id);
    }

    [Fact]
    public async Task Import_UnknownFolderId_ReturnsBadRequest()
    {
        var db = CreateContext();
        var h = NewController(db);

        var result = await h.ImportExport.Import(
            EnvelopeWithSingle("Nowhere", """{"nodes":[],"edges":[]}"""),
            Guid.NewGuid(), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.Workflows.AsNoTracking().CountAsync()).Should().Be(0);
    }
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Api.Tests.TestSupport;
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
    public async Task ExportOne_RedactsUnknownLiteralHttpHeader()
    {
        var db = CreateContext();
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Custom-Auth",
            DefinitionJson =
                """{"nodes":[{"id":"http","data":{"config":{"headers":"Accept: application/json\nX-Tenant-Token: opaque-tenant-credential"}}}],"edges":[]}""",
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var result = await NewController(db).ImportExport.ExportOne(wf.Id, CancellationToken.None);
        var content = result.Should().BeOfType<ContentResult>().Subject;

        content.Content.Should().NotContain("opaque-tenant-credential");
        content.Content.Should().Contain("***");
    }

    [Fact]
    public async Task ExportOne_RedactsOpaqueLegacyFields_WithoutSecretHeuristics()
    {
        var db = CreateContext();
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Legacy",
            DefinitionJson =
                """{"nodes":[{"id":"legacy","data":{"activityType":"restApi","config":{"script":"Write-Output 'plain-looking-literal'","body":"opaque-body","headers":{"Accept":"application/json"},"scorchRaw":{"payload":"raw-migration-value"},"url":"https://example.test/?api_key=plain-looking-secret"}}}],"edges":[]}""",
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var result = await NewController(db).ImportExport.ExportOne(wf.Id, CancellationToken.None);
        var content = result.Should().BeOfType<ContentResult>().Subject.Content!;

        content.Should().NotContain("plain-looking-literal");
        content.Should().NotContain("opaque-body");
        content.Should().NotContain("application/json");
        content.Should().NotContain("raw-migration-value");
        content.Should().NotContain("example.test");
        content.Should().NotContain("api_key");
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
    public async Task Import_SetsPublishedByUserId_ToImportingUser()
    {
        var db = CreateContext();
        var importer = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
        var h = WorkflowControllerHarnessFactory.Build(db, role: "Admin", userId: importer);

        var result = await h.ImportExport.Import(
            EnvelopeWithSingle("Principal-Check", """{"nodes":[],"edges":[]}"""),
            null, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();

        var saved = await db.Workflows.AsNoTracking().FirstAsync();
        // Import establishes runtime authority like Publish does. Without it every automated
        // trigger (schedule/webhook/file-watcher/database/event-log) is cancelled at dispatch with
        // "missing_effective_principal", and cross-folder sub-workflow calls are refused.
        saved.PublishedByUserId.Should().Be(importer,
            "automated triggers resolve their effective principal from Workflow.PublishedByUserId");
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
    public async Task ImportScorch_VariablesOnly_CreatesVariableAndEmitsAudit()
    {
        var db = CreateContext();
        var (h, audit) = NewControllerWithAudit(db);
        var variableId = Guid.NewGuid();
        var xml = $"""
                   <ExportData>
                     <GlobalSettings>
                       <Variables>
                         <Object>
                           <ObjectTypeName>Variable</ObjectTypeName>
                           <UniqueID>{variableId}</UniqueID>
                           <Name>ImportedEndpoint</Name>
                           <Value>https://example.invalid</Value>
                         </Object>
                       </Variables>
                     </GlobalSettings>
                   </ExportData>
                   """;
        h.ImportExport.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = await h.ImportExport.ImportScorch(null, CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ScorchImportResponse>().Subject;
        response.Created.Should().Be(0);
        response.Variables.Should().ContainSingle(v => v.Name == "ImportedEndpoint" && v.CreatedNow);
        db.GlobalVariables.Should().ContainSingle(v => v.Name == "ImportedEndpoint");
        var call = audit.Calls.Should().ContainSingle().Subject;
        call.Action.Should().Be("WORKFLOW_IMPORTED_SCORCH");
        call.Details.Should().Contain("\"created\":0");
        call.Details.Should().Contain("\"variables\":1");
    }

    // Operators migrate Orchestrator runbooks themselves, globals included. This is a deliberate
    // product decision, not an oversight: an Operator may already run arbitrary script under the
    // service identity, so gating the variable would split every migration into two passes
    // without taking away a capability.
    [Fact]
    public async Task ImportScorch_OperatorImportsWorkflow_CreatesGlobalVariable()
    {
        var db = CreateContext();
        var h = NewController(db, role: "Operator");
        var workflowId = Guid.NewGuid();
        var variableId = Guid.NewGuid();
        var xml = $$"""
                    <ExportData>
                      <Policies>
                        <Folder>
                          <Policy>
                            <UniqueID>{{workflowId}}</UniqueID>
                            <Name>Operator Migration</Name>
                            <Description>Imported by an Operator, globals included.</Description>
                          </Policy>
                        </Folder>
                      </Policies>
                      <GlobalSettings>
                        <Variables>
                          <Object>
                            <ObjectTypeName>Variable</ObjectTypeName>
                            <UniqueID>{{variableId}}</UniqueID>
                            <Name>MissingGlobal</Name>
                            <Value>migration-value</Value>
                          </Object>
                        </Variables>
                      </GlobalSettings>
                    </ExportData>
                    """;
        h.ImportExport.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = await h.ImportExport.ImportScorch(null, CancellationToken.None);

        var response = result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ScorchImportResponse>().Subject;
        response.Created.Should().Be(1);
        response.Workflows.Should().ContainSingle(w => w.Name == "Operator Migration");
        response.Variables.Should().ContainSingle(v =>
            v.Name == "MissingGlobal" && v.CreatedNow && !v.Skipped);
        response.Warnings.Should().NotContain(w =>
            w.Contains("Admin approval", StringComparison.OrdinalIgnoreCase));
        db.Workflows.Should().ContainSingle(w => w.Name == "Operator Migration");
        db.GlobalVariables.Should().ContainSingle(g => g.Name == "MissingGlobal");
    }

    [Fact]
    public async Task ImportScorch_CombinedWorkflowsAndVariablesOverLimit_IsRejectedBeforeWrites()
    {
        var db = CreateContext();
        var h = NewController(db);
        var variables = string.Join("", Enumerable.Range(0, 501).Select(i => $$"""
            <Object>
              <ObjectTypeName>Variable</ObjectTypeName>
              <UniqueID>{{Guid.NewGuid()}}</UniqueID>
              <Name>Var_{{i}}</Name>
              <Value>value</Value>
            </Object>
            """));
        var xml = $"""
                  <ExportData>
                    <GlobalSettings><Variables>{variables}</Variables></GlobalSettings>
                  </ExportData>
                  """;
        h.ImportExport.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var result = await h.ImportExport.ImportScorch(null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        db.Workflows.Should().BeEmpty();
        db.GlobalVariables.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportScorch_LaterVariableFailure_RollsBackEarlierVariableAndWorkflow()
    {
        var db = CreateContext();
        var realStore = new NodePilot.Data.GlobalVariableStore(
            db,
            new NodePilot.Data.Security.DpapiSecretProtector(
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
        var createCalls = 0;
        var failingStore = new Mock<IGlobalVariableStore>(MockBehavior.Strict);
        failingStore.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => realStore.GetAllAsync(ct));
        failingStore.Setup(s => s.CreateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(),
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns((string name, string value, bool isSecret, string? description,
                Guid folderId, string? updatedBy, CancellationToken ct) =>
            {
                createCalls++;
                if (createCalls == 2)
                    throw new InvalidOperationException("injected second-variable failure");
                return realStore.CreateAsync(
                    name, value, isSecret, description, folderId, updatedBy, ct);
            });

        var principal = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[]
                {
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin"),
                    new System.Security.Claims.Claim(
                        System.Security.Claims.ClaimTypes.NameIdentifier,
                        Guid.NewGuid().ToString()),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "test-admin"),
                },
                "TestAuth"));
        var controller = new WorkflowImportExportController(
            db,
            NullLogger<WorkflowImportExportController>.Instance,
            new CapturingAuditWriter(),
            new AlwaysAllowAuthorizationService(),
            failingStore.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal },
            },
        };
        var xml = $$"""
                    <ExportData>
                      <Policies>
                        <Folder>
                          <Policy>
                            <UniqueID>{{Guid.NewGuid()}}</UniqueID>
                            <Name>Atomic Migration</Name>
                          </Policy>
                        </Folder>
                      </Policies>
                      <GlobalSettings>
                        <Variables>
                          <Object>
                            <ObjectTypeName>Variable</ObjectTypeName>
                            <UniqueID>{{Guid.NewGuid()}}</UniqueID>
                            <Name>FirstGlobal</Name>
                            <Value>first-value</Value>
                          </Object>
                          <Object>
                            <ObjectTypeName>Variable</ObjectTypeName>
                            <UniqueID>{{Guid.NewGuid()}}</UniqueID>
                            <Name>SecondGlobal</Name>
                            <Value>second-value</Value>
                          </Object>
                        </Variables>
                      </GlobalSettings>
                    </ExportData>
                    """;
        controller.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));

        var act = () => controller.ImportScorch(null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("injected second-variable failure");
        db.ChangeTracker.Clear();
        (await db.GlobalVariables.AsNoTracking().ToListAsync()).Should().BeEmpty();
        (await db.Workflows.AsNoTracking().ToListAsync()).Should().BeEmpty();
    }

    // ---------- the folder trees a SCOrch export carries ----------

    /// <summary>
    /// An export from a whole SCOrch estate carries the folder tree its console showed, for both
    /// runbooks and global variables. Re-filing a few hundred imported workflows by hand is the
    /// work a migration should not create, so the tree is rebuilt below the chosen destination.
    /// The export's own root folder stands for that destination and is not reproduced as a level.
    /// </summary>
    private static string FolderTreeExport(string runbookFolders, string variableFolders) => $$"""
        <ExportData>
          <Policies>
            <Folder>
              <Name>Policies</Name>
              {{runbookFolders}}
            </Folder>
          </Policies>
          <GlobalSettings>
            <Variables>
              <Folder>
                <Name>Variables</Name>
                {{variableFolders}}
              </Folder>
            </Variables>
          </GlobalSettings>
        </ExportData>
        """;

    private static string RunbookIn(string name, params string[] folders)
    {
        var policy = $"<Policy><UniqueID>{Guid.NewGuid()}</UniqueID><Name>{name}</Name></Policy>";
        for (var i = folders.Length - 1; i >= 0; i--)
            policy = $"<Folder><Name>{folders[i]}</Name>{policy}</Folder>";
        return policy;
    }

    private static string VariableIn(string name, params string[] folders)
    {
        var obj = $"<Object><ObjectTypeName>Variable</ObjectTypeName>"
                + $"<UniqueID>{Guid.NewGuid()}</UniqueID><Name>{name}</Name><Value>v</Value></Object>";
        for (var i = folders.Length - 1; i >= 0; i--)
            obj = $"<Folder><Name>{folders[i]}</Name>{obj}</Folder>";
        return obj;
    }

    private static async Task<ScorchImportResponse> ImportXmlAsync(
        WorkflowControllerHarness h, string xml, Guid? folderId = null)
    {
        h.ImportExport.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        var result = await h.ImportExport.ImportScorch(folderId, CancellationToken.None);
        return result.Result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ScorchImportResponse>().Subject;
    }

    [Fact]
    public async Task ImportScorch_ExportWithFolderTree_RebuildsItForWorkflowsAndVariables()
    {
        var db = CreateContext();
        var h = NewController(db);
        var xml = FolderTreeExport(
            RunbookIn("Root Level") + RunbookIn("Nested", "Shared", "Logging"),
            VariableIn("RootVar") + VariableIn("NestedVar", "Shared", "Tools"));

        var response = await ImportXmlAsync(h, xml);

        var logging = db.SharedWorkflowFolders.Single(f => f.Name == "Logging");
        var shared = db.SharedWorkflowFolders.Single(f => f.Name == "Shared");
        logging.ParentFolderId.Should().Be(shared.Id);
        shared.ParentFolderId.Should().Be(SharedWorkflowFolder.RootFolderId);
        logging.Path.Should().Be("/Shared/Logging");
        logging.Depth.Should().Be(2);

        db.Workflows.Single(w => w.Name == "Nested").FolderId.Should().Be(logging.Id);
        db.Workflows.Single(w => w.Name == "Root Level").FolderId
            .Should().Be(SharedWorkflowFolder.RootFolderId, "the export's own root is the destination");

        var tools = db.GlobalVariableFolders.Single(f => f.Name == "Tools");
        tools.Path.Should().Be("/Shared/Tools");
        db.GlobalVariables.Single(v => v.Name == "NestedVar").FolderId.Should().Be(tools.Id);
        db.GlobalVariables.Single(v => v.Name == "RootVar").FolderId
            .Should().Be(GlobalVariableFolder.RootFolderId);

        response.Workflows.Single(w => w.Name == "Nested").FolderPath.Should().Be("/Shared/Logging");
        response.Variables.Single(v => v.Name == "NestedVar").FolderPath.Should().Be("/Shared/Tools");
    }

    /// <summary>
    /// Importing into a chosen destination nests the export's tree below it, rather than beside it.
    /// </summary>
    [Fact]
    public async Task ImportScorch_IntoAChosenFolder_NestsTheExportTreeBelowIt()
    {
        var db = CreateContext();
        var destination = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(),
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Migration",
            Path = "/Migration",
            Depth = 1,
        };
        db.SharedWorkflowFolders.Add(destination);
        await db.SaveChangesAsync();
        var h = NewController(db);

        await ImportXmlAsync(h, FolderTreeExport(RunbookIn("Nested", "Shared"), ""), destination.Id);

        var shared = db.SharedWorkflowFolders.Single(f => f.Name == "Shared");
        shared.ParentFolderId.Should().Be(destination.Id);
        shared.Path.Should().Be("/Migration/Shared");
        shared.Depth.Should().Be(2);
    }

    /// <summary>
    /// A second import of the same estate must land in the folders the first one made, not beside
    /// them — and a name that differs only in case is the same folder, or an import would quietly
    /// produce <c>SCCM</c> next to <c>sccm</c>.
    /// </summary>
    [Fact]
    public async Task ImportScorch_FolderThatAlreadyExists_IsReusedRegardlessOfCase()
    {
        var db = CreateContext();
        db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(),
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "SCCM",
            Path = "/SCCM",
            Depth = 1,
        });
        await db.SaveChangesAsync();
        var h = NewController(db);

        await ImportXmlAsync(h, FolderTreeExport(RunbookIn("Second Pass", "sccm", "Packaging"), ""));

        db.SharedWorkflowFolders.Where(f => f.Name == "SCCM" || f.Name == "sccm")
            .Should().ContainSingle("a case variant is the same folder");
        var packaging = db.SharedWorkflowFolders.Single(f => f.Name == "Packaging");
        packaging.Path.Should().Be("/SCCM/Packaging");
        db.Workflows.Single().FolderId.Should().Be(packaging.Id);
    }

    /// <summary>
    /// SCOrch has no depth limit; NodePilot caps folders at
    /// <see cref="SharedWorkflowFolder.MaxDepth"/> so permission traversal stays bounded. The
    /// levels that do not fit are merged into the deepest one that does — reported, not silent.
    /// </summary>
    [Fact]
    public async Task ImportScorch_FolderTreeDeeperThanTheLimit_IsFlattenedAndReported()
    {
        var db = CreateContext();
        var h = NewController(db);
        var tooDeep = Enumerable.Range(1, SharedWorkflowFolder.MaxDepth + 3)
            .Select(i => $"L{i}").ToArray();

        var response = await ImportXmlAsync(h, FolderTreeExport(RunbookIn("Deep", tooDeep), ""));

        db.SharedWorkflowFolders.Where(f => f.Id != SharedWorkflowFolder.RootFolderId)
            .Should().HaveCount(SharedWorkflowFolder.MaxDepth);
        db.SharedWorkflowFolders.Max(f => f.Depth).Should().Be(SharedWorkflowFolder.MaxDepth);
        db.Workflows.Single().FolderId.Should().Be(
            db.SharedWorkflowFolders.Single(f => f.Depth == SharedWorkflowFolder.MaxDepth).Id);
        response.Warnings.Should().Contain(w => w.Contains("deeper than NodePilot's limit"));
    }

    /// <summary>
    /// A shared folder is an RBAC boundary, so one the import minted has to be findable in the
    /// audit log the same way a hand-created one is. Variable folders are cosmetic and stay inside
    /// the import's own summary entry.
    /// </summary>
    [Fact]
    public async Task ImportScorch_CreatedWorkflowFolders_AreAudited()
    {
        var db = CreateContext();
        var (h, audit) = NewControllerWithAudit(db);

        await ImportXmlAsync(h, FolderTreeExport(
            RunbookIn("Nested", "Shared"), VariableIn("NestedVar", "Cosmetic")));

        audit.Calls.Where(c => c.Action == "FOLDER_CREATED").Should()
            .ContainSingle().Which.Details.Should().Contain("/Shared").And.Contain("scorch-import");
        audit.Calls.Should().ContainSingle(c => c.Action == "WORKFLOW_IMPORTED_SCORCH")
            .Which.Details.Should().Contain("\"workflowFoldersCreated\":1")
            .And.Contain("\"variableFoldersCreated\":1");
    }

    /// <summary>
    /// A variable skipped because its name is taken must not leave an empty folder behind — the
    /// tree is planned from what actually gets created, not from what the file contains.
    /// </summary>
    [Fact]
    public async Task ImportScorch_VariableSkippedAsDuplicate_DoesNotCreateItsFolder()
    {
        var db = CreateContext();
        db.GlobalVariables.Add(new GlobalVariable { Id = Guid.NewGuid(), Name = "Taken", Value = "existing" });
        await db.SaveChangesAsync();
        var h = NewController(db);

        var response = await ImportXmlAsync(h, FolderTreeExport("", VariableIn("Taken", "Orphan")));

        db.GlobalVariableFolders.Should().NotContain(f => f.Name == "Orphan");
        response.Variables.Should().ContainSingle(v => v.Skipped && v.FolderPath == null);
    }

    /// <summary>
    /// The endpoint's body limit and the importer's document limit have to agree, and they live in
    /// different projects. Raise one without the other and a body the controller happily accepts
    /// dies inside <see cref="NodePilot.Engine.Scorch.ScorchImporter"/> with a flat "Failed to
    /// parse
    /// XML" — which is precisely what happened when the endpoint went from 50 to 300 MiB.
    /// </summary>
    [Fact]
    public void ImportScorch_BodyLimit_DoesNotExceedWhatTheImporterWillParse()
    {
        var limit = typeof(WorkflowImportExportController)
            .GetMethod(nameof(WorkflowImportExportController.ImportScorch))!
            .GetCustomAttributes(typeof(RequestSizeLimitAttribute), inherit: false)
            .Cast<RequestSizeLimitAttribute>()
            .Single();

        // The attribute keeps the limit in a private field whose name is framework-internal, so it
        // is read by shape rather than by name: it holds exactly one 64-bit number.
        var bytes = limit.GetType()
            .GetFields(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Select(f => f.GetValue(limit))
            .OfType<long>()
            .Single();

        ((long)NodePilot.Engine.Scorch.ScorchImporter.MaxCharactersInScorchXml)
            .Should().BeGreaterThanOrEqualTo(bytes,
                "a body the endpoint accepts must not then be refused by the XML reader");
    }

    // ---------- sub-runbook calls at estate scale ----------

    /// <summary>
    /// A runbook that calls another, addressed the way SCOrch addresses it: by full path.
    /// </summary>
    private static string RunbookCalling(string name, string childPath, params string[] folders)
    {
        var policy =
            $"<Policy><UniqueID>{Guid.NewGuid()}</UniqueID><Name>{name}</Name>"
          + $"<Object><UniqueID>{Guid.NewGuid()}</UniqueID><Name>Invoke {childPath.Split('\\').Last()}</Name>"
          + $"<ObjectTypeName>Trigger Policy</ObjectTypeName><Enabled>TRUE</Enabled>"
          + $"<PolicyPath>{childPath}</PolicyPath></Object></Policy>";
        for (var i = folders.Length - 1; i >= 0; i--)
            policy = $"<Folder><Name>{folders[i]}</Name>{policy}</Folder>";
        return policy;
    }

    private static string ChildNameOf(NodePilotDbContext db, string parentName) =>
        JsonDocument.Parse(db.Workflows.Single(w => w.Name == parentName).DefinitionJson)
            .RootElement.GetProperty("nodes").EnumerateArray()
            .Where(n => n.GetProperty("data").GetProperty("activityType").GetString() == "startWorkflow")
            .Select(n => n.GetProperty("data").GetProperty("config")
                .GetProperty("workflowNameOrId").GetString()!)
            .Single();

    /// <summary>
    /// SCOrch scopes runbook names per folder; NodePilot's are global. A whole-estate export
    /// therefore routinely holds two runbooks with the same name in different folders — one gets
    /// renamed on the way in, and the call into it still carried the original name. It would then
    /// resolve to the OTHER runbook, silently, at run time, in a workflow that looks correct.
    /// </summary>
    [Fact]
    public async Task ImportScorch_TwoChildRunbooksShareAName_EachCallFollowsItsOwnChild()
    {
        var db = CreateContext();
        var h = NewController(db);
        var xml = FolderTreeExport(
            RunbookIn("Cleanup", "SCCM")
          + RunbookIn("Cleanup", "Maintenance")
          + RunbookCalling("Patch Run", @"Policies\SCCM\Cleanup")
          + RunbookCalling("Nightly", @"Policies\Maintenance\Cleanup"),
            "");

        var response = await ImportXmlAsync(h, xml);

        // One kept the name, the other was renamed — and each caller points at its OWN child.
        var sccmChild = db.Workflows.Single(w => w.FolderId ==
            db.SharedWorkflowFolders.Single(f => f.Name == "SCCM").Id).Name;
        var maintenanceChild = db.Workflows.Single(w => w.FolderId ==
            db.SharedWorkflowFolders.Single(f => f.Name == "Maintenance").Id).Name;
        sccmChild.Should().NotBe(maintenanceChild, "global names force one of them to be renamed");

        ChildNameOf(db, "Patch Run").Should().Be(sccmChild);
        ChildNameOf(db, "Nightly").Should().Be(maintenanceChild);
        response.Warnings.Should().Contain(w => w.Contains("was already taken") && w.Contains("re-pointed"));
    }

    /// <summary>
    /// Covers a collision with a runbook that already exists in NodePilot.
    /// </summary>
    [Fact]
    public async Task ImportScorch_ChildNameTakenByAnExistingWorkflow_CallFollowsTheImportedOne()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Cleanup",
            DefinitionJson = """{"nodes":[],"edges":[]}""",
        });
        await db.SaveChangesAsync();
        var h = NewController(db);

        await ImportXmlAsync(h, FolderTreeExport(
            RunbookIn("Cleanup", "SCCM") + RunbookCalling("Patch Run", @"Policies\SCCM\Cleanup"), ""));

        var imported = db.Workflows.Single(w => w.FolderId ==
            db.SharedWorkflowFolders.Single(f => f.Name == "SCCM").Id);
        imported.Name.Should().NotBe("Cleanup");
        ChildNameOf(db, "Patch Run").Should().Be(imported.Name,
            "the call must follow the runbook it came in with, not the one that was already here");
    }

    /// <summary>
    /// Nothing to follow means nothing is touched — a call whose child keeps its own name is left
    /// exactly as the mapper wrote it, and produces no noise in the report.
    /// </summary>
    [Fact]
    public async Task ImportScorch_ChildKeepsItsName_CallIsLeftAlone()
    {
        var db = CreateContext();
        var h = NewController(db);

        var response = await ImportXmlAsync(h, FolderTreeExport(
            RunbookIn("Cleanup", "SCCM") + RunbookCalling("Patch Run", @"Policies\SCCM\Cleanup"), ""));

        ChildNameOf(db, "Patch Run").Should().Be("Cleanup");
        response.Warnings.Should().NotContain(w => w.Contains("re-pointed"));
    }

    /// <summary>
    /// A call into a runbook that is in neither the file nor the database fails at run time with
    /// nothing to go on, so the report says so. Only the import knows both halves of that.
    /// </summary>
    [Fact]
    public async Task ImportScorch_ChildRunbookNotInTheExportOrTheDatabase_IsReported()
    {
        var db = CreateContext();
        var h = NewController(db);

        var response = await ImportXmlAsync(h, FolderTreeExport(
            RunbookCalling("Orphan Caller", @"Policies\Elsewhere\Not Here"), ""));

        response.Warnings.Should().Contain(w =>
            w.Contains("'Not Here'") && w.Contains("neither in this export nor"));
    }

    [Fact]
    public async Task ImportScorch_ChildRunbookAlreadyInNodePilot_IsNotReportedAsMissing()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Log Error",
            DefinitionJson = """{"nodes":[],"edges":[]}""",
        });
        await db.SaveChangesAsync();
        var h = NewController(db);

        var response = await ImportXmlAsync(h, FolderTreeExport(
            RunbookCalling("Caller", @"Policies\Shared\Log Error"), ""));

        response.Warnings.Should().NotContain(w => w.Contains("neither in this export nor"));
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
        // Emit a zero-count bulk audit event when the caller has no accessible folders.
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

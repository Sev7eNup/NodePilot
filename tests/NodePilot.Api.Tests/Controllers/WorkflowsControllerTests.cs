using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using NodePilot.Api.Audit;
using NodePilot.Core.Audit;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

public class WorkflowsControllerTests
{
    /// <summary>
    /// Stable user-id baked into every test principal — used by edit-lock tests so
    /// `GetCurrentUserId()` resolves deterministically and tests can pre-seed
    /// <c>workflow.CheckedOutByUserId = TestUserId</c> to satisfy the write-lock guard.
    /// </summary>
    internal static readonly Guid TestUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static WorkflowControllerHarness NewController(
        NodePilotDbContext db, IAuditWriter? audit = null, string role = "Admin", Guid? userId = null)
        => WorkflowControllerHarnessFactory.Build(db, audit, role, userId ?? TestUserId);

    [Fact]
    public async Task GetAll_ReturnsWorkflowsOrderedByUpdatedAt()
    {
        // Arrange
        var db = CreateContext();
        var older = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Older",
            DefinitionJson = "{}",
            UpdatedAt = DateTime.UtcNow.AddHours(-2)
        };
        var newer = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Newer",
            DefinitionJson = "{}",
            UpdatedAt = DateTime.UtcNow.AddHours(-1)
        };
        db.Workflows.AddRange(older, newer);
        await db.SaveChangesAsync();

        var h = NewController(db);

        // Act
        var result = await h.Workflows.GetAll(CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var workflows = ok.Value.Should().BeAssignableTo<List<WorkflowResponse>>().Subject;
        workflows.Should().HaveCount(2);
        workflows[0].Name.Should().Be("Newer");
        workflows[1].Name.Should().Be("Older");
    }

    /// <summary>
    /// Refactoring-finding 2.2 (the controller's raw-SQL stats query): the ROW_NUMBER
    /// raw-SQL replacement for the old correlated subquery must
    /// PARTITION BY WorkflowId. If the partitioning regresses to a single global window,
    /// a workflow with many executions would steal the entire top-20 slots and other
    /// workflows would show empty stats. This test seeds two workflows with very
    /// different execution counts (30 vs 5) and verifies that each workflow gets its own
    /// independent windowed slice.
    /// </summary>
    [Fact]
    public async Task GetAll_RowNumberWindow_PartitionsExecutionsPerWorkflow()
    {
        var db = CreateContext();

        var hot = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Hot",
            DefinitionJson = "{}",
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
        };
        var cool = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Cool",
            DefinitionJson = "{}",
            UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.AddRange(hot, cool);

        var t0 = DateTime.UtcNow;

        // 30 executions for "Hot" — all Succeeded so the rank cap is the only thing that
        // can hold the count down to 20.
        for (var i = 0; i < 30; i++)
        {
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowId = hot.Id,
                Status = ExecutionStatus.Succeeded,
                StartedAt = t0.AddMinutes(-i),
                CompletedAt = t0.AddMinutes(-i).AddSeconds(1),
            });
        }

        // 5 executions for "Cool" — all Failed. If PARTITION BY regresses to a global
        // window, these would be evicted by the 30 Succeeded rows of "Hot".
        for (var i = 0; i < 5; i++)
        {
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowId = cool.Id,
                Status = ExecutionStatus.Failed,
                StartedAt = t0.AddSeconds(-i),
                CompletedAt = t0.AddSeconds(-i).AddSeconds(1),
            });
        }

        await db.SaveChangesAsync();

        var h = NewController(db);

        var result = await h.Workflows.GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var responses = ok.Value.Should().BeAssignableTo<List<WorkflowResponse>>().Subject;
        var hotResp = responses.Single(r => r.Name == "Hot");
        var coolResp = responses.Single(r => r.Name == "Cool");

        hotResp.TotalCount.Should().Be(20, "the 30-execution workflow must be windowed to its own top-20");
        hotResp.SuccessCount.Should().Be(20);

        coolResp.TotalCount.Should().Be(5, "the 5-execution workflow must keep all rows — PARTITION BY isolates it from Hot's 30");
        coolResp.SuccessCount.Should().Be(0, "all of Cool's runs are Failed");
    }

    /// <summary>
    /// Refactoring-finding 2.2: GetAll must not crash when there are no workflows at all —
    /// the ROW_NUMBER raw-SQL path is only entered when wfIds is non-empty, so this
    /// exercises the short-circuit branch.
    /// </summary>
    [Fact]
    public async Task GetAll_NoWorkflows_ReturnsEmptyList()
    {
        var db = CreateContext();
        var h = NewController(db);

        var result = await h.Workflows.GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var responses = ok.Value.Should().BeAssignableTo<List<WorkflowResponse>>().Subject;
        responses.Should().BeEmpty();
    }

    /// <summary>
    /// Hardening: GetAll must cap result size so an unconstrained list endpoint cannot turn into
    /// a DoS vector when an org grows past tens of thousands of workflows. The cap is hardcoded
    /// at 500; the UI paginates and large-export consumers use /api/workflows/export instead.
    /// </summary>
    [Fact]
    public async Task GetAll_CapsResultSetAt500_EvenWhenMoreWorkflowsExist()
    {
        var db = CreateContext();
        for (var i = 0; i < 525; i++)
        {
            db.Workflows.Add(new Workflow
            {
                Id = Guid.NewGuid(),
                Name = $"W{i:D4}",
                DefinitionJson = "{}",
                UpdatedAt = DateTime.UtcNow.AddSeconds(-i),
            });
        }
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Workflows.GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var responses = ok.Value.Should().BeAssignableTo<List<WorkflowResponse>>().Subject;
        responses.Count.Should().Be(500);
    }

    /// <summary>
    /// Refactoring-finding 2.2: a workflow with zero executions must produce a response
    /// with empty stats (LastExecution=null, TotalCount=0). The ROW_NUMBER query simply
    /// returns no rows for that workflow id.
    /// </summary>
    [Fact]
    public async Task GetAll_WorkflowWithoutExecutions_ReturnsZeroStats()
    {
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Empty",
            DefinitionJson = "{}",
            UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Workflows.GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeAssignableTo<List<WorkflowResponse>>().Subject.Single();
        resp.LastExecution.Should().BeNull();
        resp.TotalCount.Should().Be(0);
        resp.SuccessCount.Should().Be(0);
        resp.AvgDurationMs.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_UsesLatestExecutionAndLastTwentyWindowForStats()
    {
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Stats",
            DefinitionJson = SampleDefinition,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);

        var now = DateTime.UtcNow;
        Guid latestId = Guid.Empty;
        for (var i = 0; i < 25; i++)
        {
            var started = now.AddMinutes(-i);
            var status = i switch
            {
                0 => ExecutionStatus.Failed,
                <= 10 => ExecutionStatus.Succeeded,
                <= 15 => ExecutionStatus.Failed,
                <= 19 => ExecutionStatus.Cancelled,
                _ => ExecutionStatus.Succeeded,
            };

            var execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflow.Id,
                Status = status,
                StartedAt = started,
                CompletedAt = started.AddMilliseconds(i == 0 ? 2_000 : 1_000),
            };
            if (i == 0) latestId = execution.Id;
            db.WorkflowExecutions.Add(execution);
        }
        await db.SaveChangesAsync();

        var h = NewController(db);

        var result = await h.Workflows.GetAll(CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeAssignableTo<List<WorkflowResponse>>().Subject.Single();
        response.LastExecution.Should().NotBeNull();
        response.LastExecution!.Id.Should().Be(latestId);
        response.LastExecution.Status.Should().Be(nameof(ExecutionStatus.Failed));
        response.LastExecution.DurationMs.Should().Be(2_000);
        response.SuccessCount.Should().Be(10);
        response.TotalCount.Should().Be(20);
        response.AvgDurationMs.Should().Be(1_000);
    }

    [Fact]
    public async Task GetById_Exists_ReturnsWorkflow()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Test Workflow",
            Description = "A test",
            DefinitionJson = "{\"steps\":[]}"
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var h = NewController(db);

        // Act
        var result = await h.Workflows.GetById(workflow.Id, CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<WorkflowResponse>().Subject;
        response.Id.Should().Be(workflow.Id);
        response.Name.Should().Be("Test Workflow");
        response.Description.Should().Be("A test");
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        // Arrange
        var db = CreateContext();
        var h = NewController(db);

        // Act
        var result = await h.Workflows.GetById(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetByName_Exists_ReturnsWorkflow()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Daily-Report",
            DefinitionJson = "{}"
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var h = NewController(db);

        // Act
        var result = await h.Workflows.GetByName("Daily-Report", CancellationToken.None);

        // Assert
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<WorkflowResponse>().Subject;
        response.Id.Should().Be(workflow.Id);
        response.Name.Should().Be("Daily-Report");
    }

    [Fact]
    public async Task GetByName_CaseInsensitive_ReturnsWorkflow()
    {
        // Arrange — name lookups are case-insensitive so the user can paste a
        // workflowNameOrId like "daily-report" without worrying about the canonical casing.
        var db = CreateContext();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Daily-Report",
            DefinitionJson = "{}"
        });
        await db.SaveChangesAsync();

        var h = NewController(db);

        // Act
        var result = await h.Workflows.GetByName("daily-report", CancellationToken.None);

        // Assert
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetByName_AmbiguousCaseVariants_Returns409()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Daily", DefinitionJson = "{}" });
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "DAILY", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Workflows.GetByName("daily", CancellationToken.None);
        result.Result.Should().BeOfType<ConflictObjectResult>("two case-insensitive candidates and no exact match must not resolve silently");
    }

    [Fact]
    public async Task GetByName_ExactCaseBeatsCaseInsensitiveDuplicates()
    {
        var db = CreateContext();
        var wanted = new Workflow { Id = Guid.NewGuid(), Name = "Daily", DefinitionJson = "{}" };
        db.Workflows.Add(wanted);
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "DAILY", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Workflows.GetByName("Daily", CancellationToken.None);
        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<WorkflowResponse>().Which.Id.Should().Be(wanted.Id);
    }

    [Fact]
    public async Task GetByName_NotFound_Returns404()
    {
        var db = CreateContext();
        var h = NewController(db);
        var result = await h.Workflows.GetByName("nonexistent", CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetByName_EmptyOrWhitespace_Returns404()
    {
        var db = CreateContext();
        var h = NewController(db);
        var result = await h.Workflows.GetByName("   ", CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201()
    {
        // Arrange
        var db = CreateContext();
        var h = NewController(db);
        var request = new CreateWorkflowRequest("New Workflow", "Description", "{\"steps\":[]}");

        // Act
        var result = await h.Workflows.Create(request, CancellationToken.None);

        // Assert
        var created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        created.StatusCode.Should().Be(201);
        var response = created.Value.Should().BeOfType<WorkflowResponse>().Subject;
        response.Name.Should().Be("New Workflow");
        response.Description.Should().Be("Description");
        response.DefinitionJson.Should().Be("{\"steps\":[]}");
        response.Version.Should().Be(1);

        // Verify persisted
        var saved = await db.Workflows.FindAsync(response.Id);
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_InvalidNodeShape_Returns400_AndDoesNotPersist()
    {
        var db = CreateContext();
        var h = NewController(db);
        var invalidDefinition = """{"nodes":[{"id":"n1","type":"activity"}],"edges":[]}""";

        var result = await h.Workflows.Create(
            new CreateWorkflowRequest("Broken", null, invalidDefinition),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await db.Workflows.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Update_Exists_Returns204_IncrementsVersion()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            DefinitionJson = "{}",
            Version = 1,
            CheckedOutByUserId = TestUserId,
            CheckedOutAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var h = NewController(db);
        var request = new UpdateWorkflowRequest("Updated", "New desc", "{\"updated\":true}");

        // Act
        var result = await h.Workflows.Update(workflow.Id, request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        var updated = await db.Workflows.FindAsync(workflow.Id);
        updated!.Name.Should().Be("Updated");
        updated.Version.Should().Be(2);
    }

    [Fact]
    public async Task Update_InvalidEdgeReference_Returns400_AndLeavesWorkflowUnchanged()
    {
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            DefinitionJson = "{}",
            Version = 1,
            CheckedOutByUserId = TestUserId,
            CheckedOutAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var invalidDefinition = """
        {
            "nodes": [{"id":"n1","type":"activity","data":{"activityType":"manualTrigger"}}],
            "edges": [{"id":"e1","source":"n1","target":"missing"}]
        }
        """;
        var h = NewController(db);

        var result = await h.Workflows.Update(
            workflow.Id,
            new UpdateWorkflowRequest("Updated", null, invalidDefinition),
            CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        var saved = await db.Workflows.FindAsync(workflow.Id);
        saved!.Name.Should().Be("Original");
        saved.DefinitionJson.Should().Be("{}");
        saved.Version.Should().Be(1);
    }

    [Fact]
    public async Task Update_NotFound_Returns404()
    {
        // Arrange
        var db = CreateContext();
        var h = NewController(db);
        var request = new UpdateWorkflowRequest("Updated", null, "{}");

        // Act
        var result = await h.Workflows.Update(Guid.NewGuid(), request, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Delete_Exists_Returns204()
    {
        // Arrange
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "To Delete",
            DefinitionJson = "{}"
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var h = NewController(db);

        // Act
        var result = await h.Workflows.Delete(workflow.Id, CancellationToken.None);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        // Delete now removes the row via an atomic ExecuteDelete (security-audit finding
        // M-3, a fix for a lock-check/delete race), which bypasses the change tracker —
        // query the DB directly rather than the (stale) identity-map cache.
        (await db.Workflows.AnyAsync(w => w.Id == workflow.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        // Arrange
        var db = CreateContext();
        var h = NewController(db);

        // Act
        var result = await h.Workflows.Delete(Guid.NewGuid(), CancellationToken.None);

        // Assert
        result.Should().BeOfType<NotFoundResult>();
    }

    // --- Export / Import ---------------------------------------------------------------

    private static WorkflowControllerHarness CreateControllerWithHttpContext(NodePilotDbContext db)
        => NewController(db); // NewController already wires an Admin HttpContext.

    private const string SampleDefinition = """
        {"nodes":[{"id":"t","type":"activity","position":{"x":0,"y":0},"data":{"label":"T","activityType":"scheduleTrigger","config":{"cronExpression":"0 0 * * * ?"}}}],"edges":[]}
        """;

    [Fact]
    public async Task ExportOne_ReturnsEnvelopeWithParsedDefinition()
    {
        var db = CreateContext();
        var wf = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Exportable",
            Description = "desc",
            DefinitionJson = SampleDefinition
        };
        db.Workflows.Add(wf);
        await db.SaveChangesAsync();

        var h = CreateControllerWithHttpContext(db);

        var result = await h.ImportExport.ExportOne(wf.Id, CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/json");
        h.ImportExport.Response.Headers.ContentDisposition.ToString()
            .Should().Contain("Exportable.workflow.json");

        var env = JsonSerializer.Deserialize<WorkflowExportEnvelope>(content.Content!, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        })!;
        env.ExportVersion.Should().Be(1);
        env.Workflow.Should().NotBeNull();
        env.Workflow!.Name.Should().Be("Exportable");
        env.Workflow.Definition.GetProperty("nodes").GetArrayLength().Should().Be(1);
        env.Workflows.Should().BeNull();
    }

    [Fact]
    public async Task ExportOne_NotFound_Returns404()
    {
        var db = CreateContext();
        var h = CreateControllerWithHttpContext(db);
        var result = await h.ImportExport.ExportOne(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ExportAll_ReturnsAllWorkflowsInEnvelope()
    {
        var db = CreateContext();
        db.Workflows.AddRange(
            new Workflow { Id = Guid.NewGuid(), Name = "Beta",  DefinitionJson = SampleDefinition },
            new Workflow { Id = Guid.NewGuid(), Name = "Alpha", DefinitionJson = SampleDefinition });
        await db.SaveChangesAsync();

        var h = CreateControllerWithHttpContext(db);
        var result = await h.ImportExport.ExportAll(CancellationToken.None);

        var content = result.Should().BeOfType<ContentResult>().Subject;
        h.ImportExport.Response.Headers.ContentDisposition.ToString()
            .Should().Contain("nodepilot-workflows-");

        var env = JsonSerializer.Deserialize<WorkflowExportEnvelope>(content.Content!, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        })!;
        env.ExportVersion.Should().Be(1);
        env.Workflow.Should().BeNull();
        env.Workflows.Should().NotBeNull();
        env.Workflows!.Select(w => w.Name).Should().Equal("Alpha", "Beta"); // ordered by name
    }

    private static JsonElement DefinitionElement() =>
        JsonDocument.Parse(SampleDefinition).RootElement;

    [Fact]
    public async Task Import_SingleWorkflow_Creates_WithNewGuid()
    {
        var db = CreateContext();
        var h = CreateControllerWithHttpContext(db);

        var envelope = new WorkflowExportEnvelope(
            Schema: "nodepilot-workflow-export/v1",
            ExportVersion: 1,
            ExportedAt: DateTime.UtcNow,
            Workflow: new WorkflowExportItem("Imported One", "from-file", DefinitionElement()),
            Workflows: null);

        var result = await h.ImportExport.Import(envelope, null, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var resp = ok.Value.Should().BeOfType<ImportWorkflowsResponse>().Subject;
        resp.Created.Should().Be(1);
        resp.Workflows[0].Name.Should().Be("Imported One");
        resp.Workflows[0].OriginalName.Should().BeNull();

        (await db.Workflows.CountAsync()).Should().Be(1);
        var stored = await db.Workflows.FirstAsync();
        stored.Name.Should().Be("Imported One");
        stored.Description.Should().Be("from-file");
        stored.Version.Should().Be(1);
        JsonDocument.Parse(stored.DefinitionJson).RootElement.GetProperty("nodes").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Import_NameConflict_RenamesToImportedSuffix()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Dup", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var h = CreateControllerWithHttpContext(db);
        var envelope = new WorkflowExportEnvelope(
            "nodepilot-workflow-export/v1", 1, DateTime.UtcNow,
            new WorkflowExportItem("Dup", null, DefinitionElement()),
            null);

        var result = await h.ImportExport.Import(envelope, null, CancellationToken.None);
        var resp = (result.Result as OkObjectResult)!.Value as ImportWorkflowsResponse;

        resp!.Created.Should().Be(1);
        resp.Workflows[0].Name.Should().Be("Dup (Imported 2)");
        resp.Workflows[0].OriginalName.Should().Be("Dup");
    }

    [Fact]
    public async Task Import_Bulk_CreatesAll()
    {
        var db = CreateContext();
        var h = CreateControllerWithHttpContext(db);
        var envelope = new WorkflowExportEnvelope(
            "nodepilot-workflow-export/v1", 1, DateTime.UtcNow, null,
            new List<WorkflowExportItem>
            {
                new("A", null, DefinitionElement()),
                new("B", null, DefinitionElement()),
                new("C", null, DefinitionElement()),
            });

        var result = await h.ImportExport.Import(envelope, null, CancellationToken.None);
        var resp = (result.Result as OkObjectResult)!.Value as ImportWorkflowsResponse;

        resp!.Created.Should().Be(3);
        (await db.Workflows.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Import_UnsupportedVersion_Returns400()
    {
        var db = CreateContext();
        var h = CreateControllerWithHttpContext(db);
        var envelope = new WorkflowExportEnvelope(
            "whatever", 99, DateTime.UtcNow,
            new WorkflowExportItem("X", null, DefinitionElement()), null);

        var result = await h.ImportExport.Import(envelope, null, CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Import_EmptyEnvelope_Returns400()
    {
        var db = CreateContext();
        var h = CreateControllerWithHttpContext(db);
        var envelope = new WorkflowExportEnvelope(
            "nodepilot-workflow-export/v1", 1, DateTime.UtcNow, null, null);

        var result = await h.ImportExport.Import(envelope, null, CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Import_ItemWithEmptyName_ReportsError_ButStillCreatesOthers()
    {
        var db = CreateContext();
        var h = CreateControllerWithHttpContext(db);
        var envelope = new WorkflowExportEnvelope(
            "nodepilot-workflow-export/v1", 1, DateTime.UtcNow, null,
            new List<WorkflowExportItem>
            {
                new("", null, DefinitionElement()),
                new("Valid", null, DefinitionElement()),
            });

        var result = await h.ImportExport.Import(envelope, null, CancellationToken.None);
        var resp = (result.Result as OkObjectResult)!.Value as ImportWorkflowsResponse;

        resp!.Created.Should().Be(1);
        resp.Workflows[0].Name.Should().Be("Valid");
        resp.Errors.Should().ContainSingle(e => e.Contains("name is required"));
    }

    // --- Audit + Version History -------------------------------------------------------

    private const string OneNodeDef = """{"nodes":[{"id":"s","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"delay","config":{"seconds":1}}}],"edges":[]}""";

    [Fact]
    public async Task Create_EmitsWorkflowCreatedAuditEntry()
    {
        var db = CreateContext();
        var audit = new CapturingAuditWriter();
        var h = NewController(db, audit);

        var result = await h.Workflows.Create(
            new CreateWorkflowRequest("My WF", null, OneNodeDef),
            CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        audit.Calls.Should().ContainSingle(c => c.Action == "WORKFLOW_CREATED");
    }

    [Fact]
    public async Task Update_SnapshotsOldDefinition_AndEmitsAudit()
    {
        var db = CreateContext();
        var audit = new CapturingAuditWriter();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            DefinitionJson = OneNodeDef,
            Version = 1,
            // Edit-lock pre-claimed by the test user — Update requires the caller to hold
            // the write-lock since the SCOrch-style edit-lock feature shipped.
            CheckedOutByUserId = TestUserId,
            CheckedOutAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var h = NewController(db, audit);

        await h.Workflows.Update(
            workflow.Id,
            new UpdateWorkflowRequest("Original v2", "updated", OneNodeDef),
            CancellationToken.None);

        // Live row advanced.
        var updated = await db.Workflows.FindAsync(workflow.Id);
        updated!.Version.Should().Be(2);
        updated.Name.Should().Be("Original v2");

        // History row captured the PRE-update state (v1, old name).
        var history = await db.WorkflowVersions.Where(v => v.WorkflowId == workflow.Id).ToListAsync();
        history.Should().ContainSingle();
        history[0].Version.Should().Be(1);
        history[0].Name.Should().Be("Original");

        audit.Calls.Should().ContainSingle(c => c.Action == "WORKFLOW_UPDATED");
    }

    [Fact]
    public async Task Update_WhenVersionSnapshotAlreadyExists_Returns409()
    {
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Original",
            DefinitionJson = OneNodeDef,
            Version = 1,
            CheckedOutByUserId = TestUserId,
            CheckedOutAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(new WorkflowVersion
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Version = 1,
            Name = "Original",
            DefinitionJson = OneNodeDef,
        });
        await db.SaveChangesAsync();

        var h = NewController(db);

        var result = await h.Workflows.Update(
            workflow.Id,
            new UpdateWorkflowRequest("Updated", null, OneNodeDef),
            CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        await db.Entry(workflow).ReloadAsync();
        workflow.Name.Should().Be("Original");
        workflow.Version.Should().Be(1);
    }

    [Fact]
    public async Task GetVersions_ReturnsCurrentFirst_ThenHistoricDescending()
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "W", DefinitionJson = OneNodeDef, Version = 3 };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.AddRange(
            new WorkflowVersion { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Version = 1, Name = "W", DefinitionJson = OneNodeDef, CreatedAt = DateTime.UtcNow.AddHours(-2) },
            new WorkflowVersion { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Version = 2, Name = "W", DefinitionJson = OneNodeDef, CreatedAt = DateTime.UtcNow.AddHours(-1) });
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Editing.GetVersions(workflow.Id, CancellationToken.None);

        var list = (result.Result as OkObjectResult)!.Value as List<WorkflowVersionInfo>;
        list!.Should().HaveCount(3);
        list[0].Version.Should().Be(3); list[0].IsCurrent.Should().BeTrue();
        list[1].Version.Should().Be(2); list[1].IsCurrent.Should().BeFalse();
        list[2].Version.Should().Be(1); list[2].IsCurrent.Should().BeFalse();
    }

    [Fact]
    public async Task GetVersion_Historic_ReturnsStoredDefinition()
    {
        var db = CreateContext();
        var workflow = new Workflow { Id = Guid.NewGuid(), Name = "Now", DefinitionJson = "{}", Version = 2 };
        var row = new WorkflowVersion { Id = Guid.NewGuid(), WorkflowId = workflow.Id, Version = 1, Name = "Was", DefinitionJson = OneNodeDef };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(row);
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Editing.GetVersion(workflow.Id, 1, CancellationToken.None);

        var detail = (result.Result as OkObjectResult)!.Value as WorkflowVersionDetail;
        detail!.Version.Should().Be(1);
        detail.Name.Should().Be("Was");
        detail.IsCurrent.Should().BeFalse();
    }

    [Fact]
    public async Task Rollback_AppliesHistoricDefinition_AndWritesNewVersion()
    {
        var db = CreateContext();
        var audit = new CapturingAuditWriter();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Current",
            DefinitionJson = """{"nodes":[],"edges":[]}""",
            Version = 3,
            CheckedOutByUserId = TestUserId,
            CheckedOutAt = DateTime.UtcNow,
        };
        var target = new WorkflowVersion
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Version = 1,
            Name = "Old",
            DefinitionJson = OneNodeDef,
        };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(target);
        await db.SaveChangesAsync();

        var h = NewController(db, audit);

        var result = await h.Editing.Rollback(workflow.Id, 1, new RollbackRequest("needed-for-incident"), CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<WorkflowResponse>().Subject;
        response.Version.Should().Be(4, "rollback must advance the version counter, not reset it");
        response.Name.Should().Be("Old");

        // Original v3 was captured before the rollback.
        var history = await db.WorkflowVersions.Where(v => v.WorkflowId == workflow.Id).OrderBy(v => v.Version).ToListAsync();
        history.Select(v => v.Version).Should().Equal(1, 3);
        history.First(v => v.Version == 3).ChangeNote.Should().Contain("rollback");

        audit.Calls.Should().ContainSingle(c => c.Action == "WORKFLOW_ROLLED_BACK");
    }

    [Fact]
    public async Task Rollback_ToCurrentVersion_Returns400()
    {
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "W", DefinitionJson = "{}", Version = 2,
            CheckedOutByUserId = TestUserId, CheckedOutAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var h = NewController(db);

        var result = await h.Editing.Rollback(workflow.Id, 2, null, CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Rollback_ToNonexistentVersion_Returns404()
    {
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), Name = "W", DefinitionJson = "{}", Version = 2,
            CheckedOutByUserId = TestUserId, CheckedOutAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var h = NewController(db);

        var result = await h.Editing.Rollback(workflow.Id, 99, null, CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Rollback_HistoricVersionWithInvalidDefinition_Returns400()
    {
        // Hardening: a historic WorkflowVersion can carry a DefinitionJson that has since become
        // structurally invalid (schema tightened, required field added, etc.). Rolling forward
        // would push a definition the engine refuses to load at fire time, surfacing the failure
        // as a mysterious runtime error instead of an actionable HTTP response. Validation at
        // rollback time turns that into a clean 400 against the operator who initiated the roll.
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "Current",
            DefinitionJson = OneNodeDef,
            Version = 3,
            CheckedOutByUserId = TestUserId,
            CheckedOutAt = DateTime.UtcNow,
        };
        var corruptTarget = new WorkflowVersion
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Version = 1,
            Name = "Old",
            // `nodes` is required to be an array when present — passing a string fails
            // the structural validator that ValidateDefinitionJson chains into.
            DefinitionJson = "{\"nodes\":\"not-an-array\"}",
        };
        db.Workflows.Add(workflow);
        db.WorkflowVersions.Add(corruptTarget);
        await db.SaveChangesAsync();

        var h = NewController(db);

        var result = await h.Editing.Rollback(workflow.Id, 1, null, CancellationToken.None);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---- Contract Endpoints (Sub-Workflow Contracts V1) ----

    [Fact]
    public async Task GetContract_ById_ExistingWorkflow_ReturnsContract()
    {
        var db = CreateContext();
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            Name = "ChildWorkflow",
            DefinitionJson = """
            {"nodes":[
              {"id":"t","data":{"activityType":"manualTrigger","config":{"parameters":[
                {"name":"server","type":"string","required":true}
              ]}}},
              {"id":"r","data":{"activityType":"returnData","config":{"data":{"result":"x"}}}}
            ],"edges":[]}
            """,
        };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Workflows.GetContract(workflow.Id, CancellationToken.None);

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var contract = ok.Value.Should().BeAssignableTo<WorkflowContractResponse>().Subject;
        contract.WorkflowName.Should().Be("ChildWorkflow");
        contract.HasManualTrigger.Should().BeTrue();
        contract.HasReturnData.Should().BeTrue();
        contract.Inputs.Should().ContainSingle(i => i.Name == "server" && i.Required);
        contract.Outputs.Should().Contain(o => o.Name == "result");
    }

    [Fact]
    public async Task GetContract_ById_UnknownId_Returns404()
    {
        var db = CreateContext();
        var h = NewController(db);
        var result = await h.Workflows.GetContract(Guid.NewGuid(), CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetContractByName_ExactCase_Match_ReturnsContract()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "Daily-Report",
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"manualTrigger","config":{}}}],"edges":[]}""",
        });
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Workflows.GetContractByName("Daily-Report", CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetContractByName_DifferentCase_ReturnsContract()
    {
        // Mirrors the engine's WorkflowNameResolver semantics (exact-case wins, else
        // case-insensitive) so the UI shows exactly the contracts the runtime resolves.
        var db = CreateContext();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "Daily-Report",
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"manualTrigger","config":{}}}],"edges":[]}""",
        });
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Workflows.GetContractByName("daily-report", CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetContractByName_AmbiguousCaseVariants_Returns409()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Daily", DefinitionJson = "{}" });
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "DAILY", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var h = NewController(db);
        // No exact-case match for "daily" and two case-insensitive candidates → 409.
        var result = await h.Workflows.GetContractByName("daily", CancellationToken.None);
        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task GetContractByName_ExactCaseBeatsCaseInsensitiveDuplicates()
    {
        var db = CreateContext();
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "Daily",
            DefinitionJson = """{"nodes":[{"id":"t","data":{"activityType":"manualTrigger","config":{}}}],"edges":[]}""",
        });
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "DAILY", DefinitionJson = "{}" });
        await db.SaveChangesAsync();

        var h = NewController(db);
        var result = await h.Workflows.GetContractByName("Daily", CancellationToken.None);
        result.Result.Should().BeOfType<OkObjectResult>("the exact-case match resolves unambiguously");
    }

    [Fact]
    public async Task GetContractByName_EmptyName_Returns404()
    {
        var db = CreateContext();
        var h = NewController(db);
        var result = await h.Workflows.GetContractByName("   ", CancellationToken.None);
        result.Result.Should().BeOfType<NotFoundResult>();
    }
}

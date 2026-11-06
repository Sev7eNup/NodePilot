using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodePilot.Core.Audit;
using NodePilot.Api.Dtos;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// Coverage for the SCOrch-style edit-lock endpoints (lock / unlock / publish / force-unlock)
/// and the lock-guard behavior on Update / Rollback / Enable / Delete. Each test uses an
/// in-memory SQLite DB and a controller wired with a deterministic test user-id so the
/// "is this the lock owner?" check resolves consistently.
/// </summary>
public class WorkflowsEditLockTests
{
    private static readonly Guid OwnerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherUserId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static NodePilotDbContext CreateContext() => NodePilot.TestCommons.TestDbFactory.Create();

    private static WorkflowControllerHarness NewHarness(
        NodePilotDbContext db, IAuditWriter? audit = null, string role = "Admin", Guid? userId = null,
        IResourceAuthorizationService? authz = null)
        => WorkflowControllerHarnessFactory.Build(db, audit, role, userId ?? OwnerId, authz);

    /// <summary>Persists a User row so lock-owner-name resolution returns a real value in
    /// tests.</summary>
    private static async Task SeedUserAsync(NodePilotDbContext db, Guid userId, string username)
    {
        if (await db.Users.AnyAsync(u => u.Id == userId)) return;
        db.Users.Add(new User
        {
            Id = userId,
            Username = username,
            PasswordHash = "x",
            Role = NodePilot.Core.Enums.UserRole.Admin,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static Workflow NewWorkflow(bool enabled = true, Guid? lockedBy = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "W",
        DefinitionJson = "{}",
        Version = 1,
        IsEnabled = enabled,
        CheckedOutByUserId = lockedBy,
        CheckedOutAt = lockedBy.HasValue ? DateTime.UtcNow : null,
    };

    // --- Lock --------------------------------------------------------------------------------

    [Fact]
    public async Task Lock_WhenUnlocked_LocksAndDisablesAtomically()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(enabled: true, lockedBy: null);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit);

        var result = await h.Editing.Lock(w.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        await db.Entry(w).ReloadAsync();
        w.IsEnabled.Should().BeFalse("lock atomically disables the workflow");
        w.CheckedOutByUserId.Should().Be(OwnerId);
        w.CheckedOutAt.Should().NotBeNull();
        audit.Calls.Should().ContainSingle(c => c.Action == "WORKFLOW_LOCKED");
    }

    [Fact]
    public async Task Lock_WhenAlreadyLockedByOther_Returns409()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OtherUserId, "bob");
        var w = NewWorkflow(enabled: false, lockedBy: OtherUserId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);

        var result = await h.Editing.Lock(w.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        // Original lock state untouched.
        var saved = await db.Workflows.FindAsync(w.Id);
        saved!.CheckedOutByUserId.Should().Be(OtherUserId);
    }

    [Fact]
    public async Task Lock_WhenLockedByMe_IsIdempotent()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit);

        var result = await h.Editing.Lock(w.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        audit.Calls.Should().NotContain(c => c.Action == "WORKFLOW_LOCKED",
            "idempotent re-claim must not re-audit");
    }

    // --- Update / Rollback / Enable / Delete guards ------------------------------------------

    [Fact]
    public async Task Update_WithoutAnyLock_Returns423()
    {
        var db = CreateContext();
        var w = NewWorkflow();
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);
        var result = await h.Workflows.Update(w.Id, new UpdateWorkflowRequest("X", null, "{}", ExpectedVersion: 1), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status423Locked);
    }

    [Fact]
    public async Task Update_WithForeignLock_Returns423()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OtherUserId, "bob");
        var w = NewWorkflow(lockedBy: OtherUserId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);  // caller is OwnerId
        var result = await h.Workflows.Update(w.Id, new UpdateWorkflowRequest("X", null, "{}", ExpectedVersion: 1), CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status423Locked);
    }

    [Fact]
    public async Task Update_WhenWorkflowMovesAfterAuthorization_Returns409AndDoesNotWriteIntoNewFolder()
    {
        var db = CreateContext();
        var originalFolder = SharedWorkflowFolder.RootFolderId;
        var concurrentFolder = Guid.NewGuid();
        db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = concurrentFolder,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "Concurrent",
            Path = "/Concurrent",
            Depth = 1,
        });
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);
        w.FolderId = originalFolder;
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var moved = false;
        var authz = new CallbackAuthorizationService(async (folderId, op, ct) =>
        {
            if (!moved && folderId == originalFolder && op == ResourceOp.Edit)
            {
                moved = true;
                await db.Workflows.Where(x => x.Id == w.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.FolderId, concurrentFolder), ct);
            }
        });
        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit, authz: authz);

        var result = await h.Workflows.Update(
            w.Id, new UpdateWorkflowRequest("must-not-land", null, "{}", ExpectedVersion: 1), CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        db.ChangeTracker.Clear();
        var saved = (await db.Workflows.FindAsync(w.Id))!;
        saved.FolderId.Should().Be(concurrentFolder);
        saved.Name.Should().Be("W");
        audit.Calls.Should().NotContain(c => c.Action == "WORKFLOW_UPDATED");
    }

    [Fact]
    public async Task Enable_WhileLocked_Returns423()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);  // even own lock blocks Enable
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);
        var result = await h.Workflows.Enable(w.Id, CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status423Locked);
    }

    [Fact]
    public async Task Enable_LegacyWeakHmacWebhookSecret_IsRejected()
    {
        var db = CreateContext();
        var workflow = NewWorkflow(enabled: false);
        workflow.DefinitionJson = """
        {
          "nodes": [
            { "id": "hook", "type": "activity", "data": { "activityType": "webhookTrigger", "config": {
              "path": "hook", "method": "POST", "secret": "short", "signatureMode": "nodepilot-hmac-v2"
            } } }
          ],
          "edges": []
        }
        """;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var result = await NewHarness(db).Workflows.Enable(workflow.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        await db.Entry(workflow).ReloadAsync();
        workflow.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Enable_InvalidAgentConfiguration_IsRejectedLikePublish()
    {
        var db = CreateContext();
        var workflow = NewWorkflow(enabled: false);
        workflow.DefinitionJson = """
        {"nodes":[{"id":"agent","type":"activity","data":{"activityType":"aiAgent","config":{}}}],"edges":[]}
        """;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var result = await NewHarness(db).Workflows.Enable(workflow.Id, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value!.ToString().Should().Contain("invalid_agent_configuration");
        await db.Entry(workflow).ReloadAsync();
        workflow.IsEnabled.Should().BeFalse();
    }

    // --- Enable establishes the runtime principal ---------------------------------------------

    private const string ManualTriggerDefinition =
        """{"nodes":[{"id":"t","type":"activity","data":{"activityType":"manualTrigger"}}],"edges":[]}""";

    [Fact]
    public async Task Enable_WorkflowWithoutPublisher_StampsEnablingUserAsEffectivePrincipal()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit);

        var created = await h.Workflows.Create(
            new CreateWorkflowRequest("W", null, ManualTriggerDefinition), CancellationToken.None);
        var id = ((WorkflowResponse)((CreatedAtActionResult)created.Result!).Value!).Id;

        db.ChangeTracker.Clear();
        var draft = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == id);
        draft.PublishedByUserId.Should().BeNull("create is a draft and must not claim runtime authority");
        draft.CheckedOutByUserId.Should().Be(OwnerId);

        (await h.Editing.Unlock(id, CancellationToken.None)).Result.Should().BeOfType<OkObjectResult>();
        (await h.Workflows.Enable(id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        db.ChangeTracker.Clear();
        var saved = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == id);
        saved.IsEnabled.Should().BeTrue();
        saved.PublishedByUserId.Should().Be(OwnerId,
            "an enabled workflow without an effective principal has every automated fire "
            + "terminalised as missing_effective_principal");
        audit.Calls.Should().Contain(c => c.Action == "WORKFLOW_ENABLED"
                                          && c.Details!.Contains("\"effectivePrincipalEstablished\":true"));
    }

    [Fact]
    public async Task Enable_WorkflowWithExistingPublisher_KeepsOriginalEffectivePrincipal()
    {
        var db = CreateContext();
        var publisher = Guid.NewGuid();
        var w = NewWorkflow(enabled: false, lockedBy: null);
        w.PublishedByUserId = publisher;
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit, userId: OtherUserId);
        var result = await h.Workflows.Enable(w.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        var saved = await db.Workflows.AsNoTracking().SingleAsync(x => x.Id == w.Id);
        saved.IsEnabled.Should().BeTrue();
        saved.PublishedByUserId.Should().Be(publisher,
            "re-enabling is an operational action and must not move runtime authority to the enabling user");
        audit.Calls.Should().Contain(c => c.Action == "WORKFLOW_ENABLED"
                                          && c.Details!.Contains("\"effectivePrincipalEstablished\":false"));
    }

    [Fact]
    public async Task Enable_WorkflowWithoutPublisherAndNoCallerId_ReturnsUnauthorized()
    {
        var db = CreateContext();
        var w = NewWorkflow(enabled: false, lockedBy: null);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);
        // Principal without a NameIdentifier claim: GetCurrentUserId() is null, so no principal
        // could be stamped. Fail closed instead of enabling a workflow that can never fire.
        h.Workflows.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity(
                        new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Admin") },
                        "TestAuth")),
            },
        };

        var result = await h.Workflows.Enable(w.Id, CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        db.ChangeTracker.Clear();
        var saved = await db.Workflows.AsNoTracking().SingleAsync(x => x.Id == w.Id);
        saved.IsEnabled.Should().BeFalse();
        saved.PublishedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task Disable_WorkflowWithoutPublisher_LeavesEffectivePrincipalUnset()
    {
        var db = CreateContext();
        var w = NewWorkflow(enabled: true, lockedBy: null);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);
        var result = await h.Workflows.Disable(w.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
        db.ChangeTracker.Clear();
        var saved = await db.Workflows.AsNoTracking().SingleAsync(x => x.Id == w.Id);
        saved.IsEnabled.Should().BeFalse();
        saved.PublishedByUserId.Should().BeNull("the kill switch never stamps runtime authority");
    }

    [Fact]
    public async Task Disable_WhenWorkflowMovesAfterAuthorization_Returns409AndKeepsCurrentState()
    {
        var db = CreateContext();
        var originalFolder = SharedWorkflowFolder.RootFolderId;
        var concurrentFolder = Guid.NewGuid();
        db.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = concurrentFolder,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "DisableConcurrent",
            Path = "/DisableConcurrent",
            Depth = 1,
        });
        var w = NewWorkflow(enabled: true);
        w.FolderId = originalFolder;
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var moved = false;
        var authz = new CallbackAuthorizationService(async (folderId, op, ct) =>
        {
            if (!moved && folderId == originalFolder && op == ResourceOp.Edit)
            {
                moved = true;
                await db.Workflows.Where(x => x.Id == w.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.FolderId, concurrentFolder), ct);
            }
        });
        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit, authz: authz);

        var result = await h.Workflows.Disable(w.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        db.ChangeTracker.Clear();
        var saved = (await db.Workflows.FindAsync(w.Id))!;
        saved.FolderId.Should().Be(concurrentFolder);
        saved.IsEnabled.Should().BeTrue();
        audit.Calls.Should().NotContain(c => c.Action == "WORKFLOW_DISABLED");
    }

    [Fact]
    public async Task Disable_WhileLockedByOther_StillSucceeds()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OtherUserId, "bob");
        var w = NewWorkflow(enabled: true, lockedBy: OtherUserId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);  // caller is OwnerId, NOT lock owner
        var result = await h.Workflows.Disable(w.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>("Disable is the kill-switch — must work through any lock");
        var saved = await db.Workflows.FindAsync(w.Id);
        saved!.IsEnabled.Should().BeFalse();
        saved.CheckedOutByUserId.Should().Be(OtherUserId, "Disable must not touch the lock");
    }

    [Fact]
    public async Task Delete_WithForeignLock_Returns423()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OtherUserId, "bob");
        var w = NewWorkflow(lockedBy: OtherUserId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);  // caller != lock owner
        var result = await h.Workflows.Delete(w.Id, CancellationToken.None);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status423Locked);
    }

    [Fact]
    public async Task Delete_WhenUnlocked_Succeeds()
    {
        var db = CreateContext();
        var w = NewWorkflow(lockedBy: null);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);
        var result = await h.Workflows.Delete(w.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    // --- Unlock ------------------------------------------------------------------------------

    [Fact]
    public async Task Unlock_AsOwner_RemovesLockButLeavesIsEnabledFalse()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit);

        var result = await h.Editing.Unlock(w.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        // Unlock clears the lock via an atomic ExecuteUpdate, which bypasses the change
        // tracker — reload to read the persisted row rather than the stale tracked instance.
        await db.Entry(w).ReloadAsync();
        w.CheckedOutByUserId.Should().BeNull();
        w.CheckedOutAt.Should().BeNull();
        w.IsEnabled.Should().BeFalse("unlock must not auto-enable; user re-enables explicitly");
        audit.Calls.Should().ContainSingle(c => c.Action == "WORKFLOW_UNLOCKED");
    }

    [Fact]
    public async Task Unlock_WhenNotOwner_Returns423()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OtherUserId, "bob");
        var w = NewWorkflow(enabled: false, lockedBy: OtherUserId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);  // caller is OwnerId, NOT lock owner
        var result = await h.Editing.Unlock(w.Id, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status423Locked);
    }

    // --- Publish -----------------------------------------------------------------------------

    [Fact]
    public async Task Publish_AsOwner_AtomicallySavesEnablesAndUnlocks()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit);
        var newDef = """{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"manualTrigger"}}],"edges":[]}""";

        var result = await h.Editing.Publish(
            w.Id,
            new PublishWorkflowRequest("Renamed", "new desc", newDef, ExpectedVersion: 1),
            CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        var saved = await db.Workflows.FindAsync(w.Id);
        saved!.IsEnabled.Should().BeTrue();
        saved.CheckedOutByUserId.Should().BeNull();
        saved.CheckedOutAt.Should().BeNull();
        saved.Name.Should().Be("Renamed");
        saved.DefinitionJson.Should().Be(newDef);
        saved.Version.Should().Be(2);
        saved.PublishedByUserId.Should().Be(OwnerId,
            "trigger-driven runtime authority is pinned to the publisher");
        audit.Calls.Should().ContainSingle(c => c.Action == "WORKFLOW_PUBLISHED");
    }

    [Fact]
    public async Task Update_FromStaleTab_Returns409AndKeepsTheEarlierSave()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();
        var h = NewHarness(db);
        const string tabADefinition = """{"nodes":[{"id":"log","type":"activity","position":{"x":0,"y":0},"data":{"activityType":"log","config":{"message":"from tab A"}}}],"edges":[]}""";

        // Both tabs loaded version 1. Tab A saves first, tab B then saves its old snapshot.
        var tabA = await h.Workflows.Update(
            w.Id, new UpdateWorkflowRequest("W", null, tabADefinition, ExpectedVersion: 1), CancellationToken.None);
        var tabB = await h.Workflows.Update(
            w.Id, new UpdateWorkflowRequest("Renamed in tab B", null, "{}", ExpectedVersion: 1), CancellationToken.None);

        tabA.Should().BeOfType<NoContentResult>();
        var conflict = tabB.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value.Should().BeEquivalentTo(new { code = "workflow_version_conflict", currentVersion = 2 });
        db.ChangeTracker.Clear();
        var saved = (await db.Workflows.FindAsync(w.Id))!;
        saved.DefinitionJson.Should().Be(tabADefinition);
        saved.Name.Should().Be("W");
        saved.Version.Should().Be(2);
        (await db.WorkflowVersions.CountAsync(v => v.WorkflowId == w.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Update_WithStaleVersionAndForeignLock_Returns423()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OtherUserId, "bob");
        var w = NewWorkflow(lockedBy: OtherUserId);
        w.Version = 3;
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var result = await NewHarness(db).Workflows.Update(
            w.Id, new UpdateWorkflowRequest("X", null, "{}", ExpectedVersion: 1), CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status423Locked);
    }

    [Fact]
    public async Task Publish_FromStaleDraft_Returns409AndLeavesWorkflowLockedAndUnchanged()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);
        w.Version = 2;
        db.Workflows.Add(w);
        await db.SaveChangesAsync();
        var audit = new CapturingAuditWriter();

        var result = await NewHarness(db, audit).Editing.Publish(
            w.Id, new PublishWorkflowRequest("Stale", null, """{"nodes":[],"edges":[]}""", ExpectedVersion: 1),
            CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>()
            .Which.Value.Should().BeEquivalentTo(new { code = "workflow_version_conflict", currentVersion = 2 });
        db.ChangeTracker.Clear();
        var saved = (await db.Workflows.FindAsync(w.Id))!;
        saved.Name.Should().Be("W");
        saved.Version.Should().Be(2);
        saved.IsEnabled.Should().BeFalse();
        saved.CheckedOutByUserId.Should().Be(OwnerId);
        audit.Calls.Should().NotContain(c => c.Action == "WORKFLOW_PUBLISHED");
    }

    [Theory]
    [InlineData(typeof(UpdateWorkflowRequest))]
    [InlineData(typeof(PublishWorkflowRequest))]
    public void SaveRequests_WithoutExpectedVersion_DoNotDeserialize(Type requestType)
    {
        var act = () => System.Text.Json.JsonSerializer.Deserialize(
            """{"name":"W","description":null,"definitionJson":"{}"}""", requestType,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        act.Should().Throw<System.Text.Json.JsonException>().WithMessage("*expectedVersion*");
    }

    [Fact]
    public async Task Publish_WeakHmacWebhookSecret_IsRejectedAndRemainsLocked()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var workflow = NewWorkflow(enabled: false, lockedBy: OwnerId);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

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

        var result = await NewHarness(db).Editing.Publish(
            workflow.Id,
            new PublishWorkflowRequest("Unsafe", null, weakHmacDefinition, ExpectedVersion: 1),
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value!.ToString().Should().Contain("weak_webhook_hmac_secret");
        await db.Entry(workflow).ReloadAsync();
        workflow.IsEnabled.Should().BeFalse();
        workflow.CheckedOutByUserId.Should().Be(OwnerId);
    }

    private const string UnsatisfiableJunctionDefinition = """
    {
      "nodes": [
        { "id": "t", "type": "activity", "data": { "activityType": "manualTrigger", "config": {} } },
        { "id": "a", "type": "activity", "data": { "activityType": "log", "config": {} } },
        { "id": "b", "type": "activity", "data": { "activityType": "log", "config": {} } },
        { "id": "j", "type": "junction", "data": { "activityType": "junction", "config": { "mode": "waitNofM", "requiredCount": 3 } } }
      ],
      "edges": [
        { "id": "e1", "source": "t", "target": "a" },
        { "id": "e2", "source": "t", "target": "b" },
        { "id": "e3", "source": "a", "target": "j" },
        { "id": "e4", "source": "b", "target": "j" }
      ]
    }
    """;

    [Fact]
    public async Task Publish_JunctionRequiringMoreInputsThanItHas_IsRejectedAndRemainsLocked()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var workflow = NewWorkflow(enabled: false, lockedBy: OwnerId);
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var result = await NewHarness(db).Editing.Publish(
            workflow.Id,
            new PublishWorkflowRequest("NofM", null, UnsatisfiableJunctionDefinition, ExpectedVersion: 1),
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value!.ToString().Should().Contain("invalid_junction_required_count");
        await db.Entry(workflow).ReloadAsync();
        workflow.IsEnabled.Should().BeFalse();
        workflow.CheckedOutByUserId.Should().Be(OwnerId);
    }

    [Fact]
    public async Task Enable_JunctionRequiringMoreInputsThanItHas_IsRejectedLikePublish()
    {
        var db = CreateContext();
        var workflow = NewWorkflow(enabled: false);
        workflow.DefinitionJson = UnsatisfiableJunctionDefinition;
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync();

        var result = await NewHarness(db).Workflows.Enable(workflow.Id, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value!.ToString().Should().Contain("invalid_junction_required_count");
        await db.Entry(workflow).ReloadAsync();
        workflow.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Publish_WhenVersionSnapshotAlreadyExists_Returns409()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);
        db.Workflows.Add(w);
        db.WorkflowVersions.Add(new WorkflowVersion
        {
            Id = Guid.NewGuid(),
            WorkflowId = w.Id,
            Version = w.Version,
            Name = w.Name,
            DefinitionJson = w.DefinitionJson,
        });
        await db.SaveChangesAsync();

        var h = NewHarness(db);

        var result = await h.Editing.Publish(
            w.Id,
            new PublishWorkflowRequest("Renamed", "new desc", "{}", ExpectedVersion: 1),
            CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        await db.Entry(w).ReloadAsync();
        w.CheckedOutByUserId.Should().Be(OwnerId);
        w.Version.Should().Be(1);
    }

    [Fact]
    public async Task Publish_UniqueViolationWithNonEnglishProviderMessage_Returns409()
    {
        // The unique-violation check reads the provider error code, not the message text, so a
        // database server running in another locale still maps the racing insert to 409.
        var (connection, seed) = NodePilot.TestCommons.TestDbFactory.CreateWithConnection();
        await using var _ = connection;
        await SeedUserAsync(seed, OwnerId, "alice");
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);
        seed.Workflows.Add(w);
        await seed.SaveChangesAsync();
        await seed.DisposeAsync();

        await using var db = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new LocalizedUniqueViolationOnVersionInsert())
            .Options);

        var result = await NewHarness(db).Editing.Publish(
            w.Id, new PublishWorkflowRequest("Renamed", null, "{}", ExpectedVersion: 1), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    private sealed class LocalizedUniqueViolationOnVersionInsert
        : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<WorkflowVersion>().Any(e => e.State == EntityState.Added))
            {
                throw new DbUpdateException("save failed", new Npgsql.PostgresException(
                    "повторяющееся значение ключа нарушает ограничение уникальности", "ОШИБКА", "ERROR", "23505"));
            }
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Publish_WithoutLock_Returns423()
    {
        var db = CreateContext();
        var w = NewWorkflow();
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);
        var result = await h.Editing.Publish(
            w.Id,
            new PublishWorkflowRequest("X", null, "{}", ExpectedVersion: 1),
            CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(StatusCodes.Status423Locked);
    }

    // --- Force-unlock ------------------------------------------------------------------------

    [Fact]
    public async Task ForceUnlock_AsAdmin_BreaksForeignLock_AndAuditsPreviousOwner()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OtherUserId, "bob");
        var w = NewWorkflow(enabled: false, lockedBy: OtherUserId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit, role: "Admin");  // caller != lock owner

        var result = await h.Editing.ForceUnlock(w.Id, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        var saved = await db.Workflows.FindAsync(w.Id);
        saved!.CheckedOutByUserId.Should().BeNull();

        var entry = audit.Calls.Should().ContainSingle(c => c.Action == "WORKFLOW_FORCE_UNLOCKED").Subject;
        entry.Details.Should().Contain(OtherUserId.ToString(), "audit details must capture the previous lock owner");
    }

    [Fact]
    public async Task ForceUnlock_WhenLockChangesAfterAuthorization_Returns409AndDoesNotClearOrAuditNewLock()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OtherUserId, "bob");
        var replacementOwner = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var replacementLockedAt = DateTime.UtcNow.AddSeconds(1);
        var w = NewWorkflow(enabled: false, lockedBy: OtherUserId);
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var replaced = false;
        var authz = new CallbackAuthorizationService(async (_, op, ct) =>
        {
            if (!replaced && op == ResourceOp.Admin)
            {
                replaced = true;
                await db.Workflows.Where(x => x.Id == w.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.CheckedOutByUserId, replacementOwner)
                        .SetProperty(x => x.CheckedOutAt, replacementLockedAt), ct);
            }
        });
        var audit = new CapturingAuditWriter();
        var h = NewHarness(db, audit, role: "Admin", authz: authz);

        var result = await h.Editing.ForceUnlock(w.Id, CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        db.ChangeTracker.Clear();
        var saved = (await db.Workflows.FindAsync(w.Id))!;
        saved.CheckedOutByUserId.Should().Be(replacementOwner);
        saved.CheckedOutAt.Should().Be(replacementLockedAt);
        audit.Calls.Should().NotContain(c => c.Action == "WORKFLOW_FORCE_UNLOCKED");
    }

    // --- Atomic delete / rollback and duplicate-safety regressions -----------------------

    [Fact]
    public async Task Delete_AsLockOwner_Succeeds()
    {
        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(lockedBy: OwnerId);  // caller IS the lock owner
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);  // caller is OwnerId
        var result = await h.Workflows.Delete(w.Id, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>("the lock owner may delete their own checkout");
        (await db.Workflows.AnyAsync(x => x.Id == w.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Rollback_AsOwner_AppliesTargetRetainsLockAndSnapshotsCurrent()
    {
        const string currentDef = """{"nodes":[],"edges":[]}""";
        const string oldDef = """{"nodes":[{"id":"n1","type":"activity","data":{"activityType":"manualTrigger"}}],"edges":[]}""";

        var db = CreateContext();
        await SeedUserAsync(db, OwnerId, "alice");
        var w = NewWorkflow(enabled: false, lockedBy: OwnerId);
        w.Version = 2;
        w.DefinitionJson = currentDef;
        var originalPublisher = Guid.NewGuid();
        w.PublishedByUserId = originalPublisher;
        db.Workflows.Add(w);
        db.WorkflowVersions.Add(new WorkflowVersion
        {
            Id = Guid.NewGuid(), WorkflowId = w.Id, Version = 1,
            Name = "old-name", DefinitionJson = oldDef,
        });
        await db.SaveChangesAsync();

        var h = NewHarness(db);
        var result = await h.Editing.Rollback(w.Id, 1, null, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        await db.Entry(w).ReloadAsync();
        w.Version.Should().Be(3, "rollback rolls forward — the version bumps rather than reverting in place");
        w.Name.Should().Be("old-name");
        w.DefinitionJson.Should().Be(oldDef);
        w.CheckedOutByUserId.Should().Be(OwnerId, "rollback retains the edit-lock (unlike publish)");
        w.PublishedByUserId.Should().Be(originalPublisher,
            "rollback is an edit operation and must not change trigger runtime authority");
        (await db.WorkflowVersions.AnyAsync(v => v.WorkflowId == w.Id && v.Version == 2))
            .Should().BeTrue("the pre-rollback state is snapshotted into history");
    }

    [Fact]
    public async Task Duplicate_OfEnabledWorkflow_CreatesDisabledCopy()
    {
        var db = CreateContext();
        var w = NewWorkflow(enabled: true, lockedBy: null);
        w.DefinitionJson =
            """{"nodes":[{"id":"t","data":{"activityType":"scheduleTrigger","config":{"cron":"0 * * * *"}}},{"id":"a","data":{"activityType":"log","config":{}}}],"edges":[]}""";
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);
        var result = await h.Workflows.Duplicate(w.Id, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var copy = await db.Workflows.SingleAsync(x => x.Id != w.Id);
        copy.IsEnabled.Should().BeFalse(
            "L-6: a duplicate is always born disabled so cloning a locked/under-review workflow cannot bypass the edit-lock");
        copy.Name.Should().Be("W (Copy)");
        var expectedMetadata = WorkflowMetadata.Compute(w.DefinitionJson);
        copy.ActivityCount.Should().Be(expectedMetadata.ActivityCount);
        copy.TriggerTypesJson.Should().Be(expectedMetadata.TriggerTypesJson);
        copy.Version.Should().Be(1, "a duplicate starts a distinct monotonically-versioned history");
        copy.PublishedByUserId.Should().NotBeNull(
            "every automated dispatch resolves its principal from this column; stamping the "
            + "duplicating user keeps the copy's runtime authority off whoever later enables it");
    }

    private sealed class CallbackAuthorizationService(
        Func<Guid, ResourceOp, CancellationToken, Task> onWorkflowCheck)
        : IResourceAuthorizationService
    {
        public async Task<bool> CanAccessWorkflowAsync(
            System.Security.Claims.ClaimsPrincipal user, Guid folderId, ResourceOp op,
            CancellationToken ct = default)
        {
            await onWorkflowCheck(folderId, op, ct);
            return true;
        }

        public Task<bool> CanAccessFolderAsync(
            System.Security.Claims.ClaimsPrincipal user, Guid folderId, ResourceOp op,
            CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<AccessibleFolderSet> GetAccessibleFolderIdsAsync(
            System.Security.Claims.ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(AccessibleFolderSet.Unrestricted);

        public Task<ResourceCapabilities> GetWorkflowCapabilitiesAsync(
            System.Security.Claims.ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.All);

        public Task<ResourceCapabilities> GetFolderCapabilitiesAsync(
            System.Security.Claims.ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult(ResourceCapabilities.All);

        public Task<SharedFolderRole?> GetEffectiveFolderRoleAsync(
            System.Security.Claims.ClaimsPrincipal user, Guid folderId, CancellationToken ct = default)
            => Task.FromResult<SharedFolderRole?>(SharedFolderRole.FolderAdmin);

        public void InvalidateAll() { }
    }
}

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
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Engine;
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

    /// <summary>Persists a User row so lock-owner-name resolution returns a real value in tests.</summary>
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
        var result = await h.Workflows.Update(w.Id, new UpdateWorkflowRequest("X", null, "{}"), CancellationToken.None);

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
        var result = await h.Workflows.Update(w.Id, new UpdateWorkflowRequest("X", null, "{}"), CancellationToken.None);

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
            w.Id, new UpdateWorkflowRequest("must-not-land", null, "{}"), CancellationToken.None);

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
        // Unlock now clears the lock via an atomic ExecuteUpdate (security-audit finding M-3,
        // a fix for a lock-check/lock-clear race), which bypasses the change tracker —
        // reload to read the persisted row rather than the stale tracked instance.
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
            new PublishWorkflowRequest("Renamed", "new desc", newDef),
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
            new PublishWorkflowRequest("Unsafe", null, weakHmacDefinition),
            CancellationToken.None);

        var badRequest = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.Value!.ToString().Should().Contain("weak_webhook_hmac_secret");
        await db.Entry(workflow).ReloadAsync();
        workflow.IsEnabled.Should().BeFalse();
        workflow.CheckedOutByUserId.Should().Be(OwnerId);
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
            new PublishWorkflowRequest("Renamed", "new desc", "{}"),
            CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        await db.Entry(w).ReloadAsync();
        w.CheckedOutByUserId.Should().Be(OwnerId);
        w.Version.Should().Be(1);
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
            new PublishWorkflowRequest("X", null, "{}"),
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

    // --- Security-hardening regressions: M-3 (atomic delete / rollback) + L-6 (duplicate) ---

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
        db.Workflows.Add(w);
        await db.SaveChangesAsync();

        var h = NewHarness(db);
        var result = await h.Workflows.Duplicate(w.Id, CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var copy = await db.Workflows.SingleAsync(x => x.Id != w.Id);
        copy.IsEnabled.Should().BeFalse(
            "L-6: a duplicate is always born disabled so cloning a locked/under-review workflow cannot bypass the edit-lock");
        copy.Name.Should().Be("W (Copy)");
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

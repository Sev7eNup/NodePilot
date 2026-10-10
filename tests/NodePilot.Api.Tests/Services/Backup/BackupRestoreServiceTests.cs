using System.Text;
using System.Data.Common;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Configuration;
using NodePilot.Api.Services.Backup;
using NodePilot.Api.Services.Backup.Parts;
using NodePilot.Api.Hubs;
using NodePilot.Api.Security;
using NodePilot.Api.Tests.TestSupport;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Services.Backup;

/// <summary>
/// Restore behaviour of <see cref="BackupRestoreService"/> (ADR 0001 Phase 2 — the
/// system-configuration backup/restore feature): full round-trip into a fresh DB with secret +
/// GUID-reference fidelity, conflict policies, a guard that blocks a restore that would leave
/// zero active Admins (K11), an abort when a workflow references a GUID that isn't in the
/// backup (K12), and rejection of an unauthenticated encrypted payload (K5).
/// </summary>
[Collection(NodePilot.Api.Tests.Hubs.ExecutionHubStaticStateCollection.Name)]
public sealed class BackupRestoreServiceTests : IDisposable
{
    private const string Passphrase = "a-strong-backup-pass";

    /// <summary>The admin performing the restore; becomes the runtime principal of every
    /// restored workflow, the same rule Publish and Import follow.</summary>
    private static readonly Guid RestoreActor = Guid.Parse("0bd7f0a1-4c3e-4a6b-9f21-6c2f5d8e4b70");
    private static readonly List<string> AllSections =
    [
        BackupSections.Folders, BackupSections.Users, BackupSections.Credentials,
        BackupSections.Machines, BackupSections.GlobalVariables, BackupSections.Workflows,
    ];

    private readonly AesGcmSecretProtector _atRest = new(Key());
    private readonly List<string> _tempFiles = [];

    private static byte[] Key()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 11);
        return k;
    }

    private string TempPath()
    {
        var p = Path.Combine(Path.GetTempPath(), "np-restore-test-" + Guid.NewGuid().ToString("N") + ".json");
        _tempFiles.Add(p);
        return p;
    }

    private IBackupPart[] Parts(NodePilotDbContext db) =>
    [
        new FolderBackupPart(db), new UserBackupPart(db), new CredentialBackupPart(db, _atRest),
        new MachineBackupPart(db), new GlobalVariableFolderBackupPart(db), new GlobalVariableBackupPart(new GlobalVariableStore(db, _atRest)),
        new CustomActivityBackupPart(new CustomActivityDefinitionStore(db)),
        new WorkflowBackupPart(db), new SettingsBackupPart(new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance), _atRest),
    ];

    private BackupRestoreService Restore(NodePilotDbContext db) =>
        new(db, _atRest, new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance),
            NullLogger<BackupRestoreService>.Instance, VersionProtector());

    private NodePilot.Api.Services.WorkflowVersionDefinitionProtector VersionProtector() =>
        new(_atRest, NullLogger<NodePilot.Api.Services.WorkflowVersionDefinitionProtector>.Instance);

    private async Task<byte[]> ExportAsync(NodePilotDbContext db, List<string> sections)
        => (await new BackupService(Parts(db)).ExportAsync(sections, Passphrase, "admin", CancellationToken.None)).Content;

    private static byte[] MutatePayload(byte[] backup, Action<JsonObject> mutate)
    {
        var outer = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(backup))!;
        var salt = Convert.FromBase64String(outer["crypto"]!["salt"]!.GetValue<string>());
        var protector = PassphraseSecretProtector.Derive(Passphrase, salt);
        var payload = (JsonObject)JsonNode.Parse(
            protector.Unprotect(Convert.FromBase64String(outer["payload"]!.GetValue<string>())))!;
        mutate(payload);
        outer["payload"] = Convert.ToBase64String(protector.Protect(payload.ToJsonString()));
        return Encoding.UTF8.GetBytes(outer.ToJsonString());
    }

    // Seeds a full source DB; returns the machine id (referenced from the workflow definition).
    private static async Task<(Guid machineId, Guid credId, Guid childFolderId)> SeedFullAsync(NodePilotDbContext db)
    {
        var child = new SharedWorkflowFolder { Id = Guid.NewGuid(), ParentFolderId = SharedWorkflowFolder.RootFolderId, Name = "team", Path = "/team", Depth = 1 };
        db.SharedWorkflowFolders.Add(child);
        db.Users.Add(new User { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true });

        var cred = new Credential
        {
            Id = Guid.NewGuid(), Name = "svc", Username = "svc", Domain = "D",
            EncryptedPassword = new AesGcmSecretProtector(Key()).Protect("the-password"),
            ExpiresAt = new DateTime(2026, 12, 31, 18, 0, 0, DateTimeKind.Utc),
        };
        db.Credentials.Add(cred);
        var machineId = Guid.NewGuid();
        db.ManagedMachines.Add(new ManagedMachine { Id = machineId, Name = "web01", Hostname = "web01.local", DefaultCredentialId = cred.Id });

        db.GlobalVariables.Add(new GlobalVariable { Id = Guid.NewGuid(), Name = "API_URL", Value = "https://api.local", IsSecret = false });
        db.GlobalVariables.Add(new GlobalVariable { Id = Guid.NewGuid(), Name = "API_TOKEN", Value = Convert.ToBase64String(new AesGcmSecretProtector(Key()).Protect("tok-123")), IsSecret = true });

        var def = "{\"nodes\":[{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"restApi\","
            + "\"targetMachineId\":\"" + machineId + "\",\"credentialId\":\"" + cred.Id + "\","
            + "\"config\":{\"apiKey\":\"super-secret-key\"}}}],\"edges\":[]}";
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "wf1", DefinitionJson = def, FolderId = child.Id, IsEnabled = false });
        await db.SaveChangesAsync();
        return (machineId, cred.Id, child.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverwriteRoundTrip_MovedWorkflowRevokesLiveSubscriptionsAndFolderProjection(bool cancelAfterCommit)
    {
        var ct = TestContext.Current.CancellationToken;
        using var src = TestDbFactory.Create();
        var privateFolder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), Name = "Private", Path = "/Private", Depth = 1,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
        };
        var workflowId = Guid.NewGuid();
        src.AddRange(privateFolder, new Workflow
        {
            Id = workflowId, Name = "Restored private", DefinitionJson = EmptyDefinition, FolderId = privateFolder.Id,
        });
        await src.SaveChangesAsync(ct);
        var backup = await ExportAsync(src, [BackupSections.Workflows]);
        using var dst = TestDbFactory.Create();
        var executionId = Guid.NewGuid();
        dst.AddRange(new Workflow { Id = workflowId, Name = "Currently public", DefinitionJson = EmptyDefinition },
            new WorkflowExecution { Id = executionId, WorkflowId = workflowId, Status = ExecutionStatus.Running });
        await dst.SaveChangesAsync(ct);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var restoringDb = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(dst.Database.GetDbConnection())
            .AddInterceptors(new CancelAfterRestoreCommit(cancelAfterCommit ? request : null)).Options);
        var hub = new RecordingHubContext();
        var projection = new RecordingFolderProjection();
        ExecutionHub.RegisterGroupForTest("viewer", $"workflow-{workflowId}");
        ExecutionHub.RegisterGroupForTest("viewer", executionId.ToString());
        ExecutionHub.RegisterOpsFeedForTest("viewer", false, [SharedWorkflowFolder.RootFolderId]);
        try
        {
            var restore = new BackupRestoreService(restoringDb, _atRest,
                new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance),
                NullLogger<BackupRestoreService>.Instance, VersionProtector(), hub, projection,
                new ResourceAuthorizationService(restoringDb));
            await restore.RestoreAsync(backup, Passphrase,
                new Dictionary<string, RestoreConflictPolicy> { [BackupSections.Workflows] = RestoreConflictPolicy.Overwrite },
                RestoreActor, request.Token);

            dst.ChangeTracker.Clear();
            (await dst.Workflows.SingleAsync(w => w.Id == workflowId, ct)).FolderId.Should().Be(privateFolder.Id);
            hub.Removed.Should().Contain(("viewer", $"workflow-{workflowId}"));
            hub.Removed.Should().Contain(("viewer", executionId.ToString()));
            projection.Invalidated.Should().Contain(workflowId);
            ExecutionHub.GetOpsFeedConnections(SharedWorkflowFolder.RootFolderId).Should().BeEmpty();
            request.IsCancellationRequested.Should().Be(cancelAfterCommit);
        }
        finally
        {
            ExecutionHub.ClearGroupsForTest();
            ExecutionHub.ClearOpsFeedForTest();
        }
    }

    private sealed class CancelAfterRestoreCommit(CancellationTokenSource? request) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            request?.Cancel();
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Restore_CommitFailure_PreservesSettingsAndLiveScopesAccordingToDurableOutcome(
        bool committed, bool verificationUnavailable)
    {
        var ct = TestContext.Current.CancellationToken;
        using var src = TestDbFactory.Create();
        var privateFolder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), Name = "Private", Path = "/Private", Depth = 1,
            ParentFolderId = SharedWorkflowFolder.RootFolderId,
        };
        var workflowId = Guid.NewGuid();
        src.AddRange(privateFolder, new Workflow
        {
            Id = workflowId, Name = "Restored", DefinitionJson = EmptyDefinition, FolderId = privateFolder.Id,
        });
        await src.SaveChangesAsync(ct);
        var sourceSettings = new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance);
        sourceSettings.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Port"] = 2525 });
        var backup = (await new BackupService(Parts(src)
            .Where(part => part is not SettingsBackupPart)
            .Append(new SettingsBackupPart(sourceSettings, _atRest)))
            .ExportAsync([BackupSections.Workflows, BackupSections.Settings], Passphrase, "admin", ct)).Content;

        using var dst = TestDbFactory.Create();
        dst.Workflows.Add(new Workflow { Id = workflowId, Name = "Public", DefinitionJson = EmptyDefinition });
        await dst.SaveChangesAsync(ct);
        var targetSettings = new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance);
        targetSettings.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Port"] = 25 });
        using var restoringDb = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
            .UseSqlite(dst.Database.GetDbConnection())
            .AddInterceptors(new FailRestoreCommit(committed), new RejectCommitVerification(verificationUnavailable)).Options);
        var hub = new RecordingHubContext();
        var projection = new RecordingFolderProjection();
        ExecutionHub.RegisterGroupForTest("viewer", $"workflow-{workflowId}");
        try
        {
            var restore = new BackupRestoreService(restoringDb, _atRest, targetSettings,
                NullLogger<BackupRestoreService>.Instance, VersionProtector(), hub, projection,
                new ResourceAuthorizationService(restoringDb));
            var failure = await Record.ExceptionAsync(() => restore.RestoreAsync(backup, Passphrase,
                Policy(BackupSections.Workflows, RestoreConflictPolicy.Overwrite), RestoreActor, ct));

            dst.ChangeTracker.Clear();
            (await dst.Workflows.SingleAsync(w => w.Id == workflowId, ct)).FolderId.Should()
                .Be(committed ? privateFolder.Id : SharedWorkflowFolder.RootFolderId);
            targetSettings.ReadOrEmpty()["Smtp"]?["Port"]?.GetValue<int>().Should()
                .Be(committed ? 2525 : 25, "settings compensation must follow the durable database outcome");
            if (committed)
            {
                hub.Removed.Should().Contain(("viewer", $"workflow-{workflowId}"));
                projection.Invalidated.Should().Contain(workflowId);
                if (verificationUnavailable)
                    failure.Should().BeOfType<BackupRestoreException>().Which.Message.Should()
                        .Contain("Do not repeat the restore");
                else
                    failure.Should().BeNull("a verified committed restore must finish its postcommit work");
            }
            else
            {
                hub.Removed.Should().BeEmpty();
                failure.Should().BeOfType<IOException>();
            }
        }
        finally { ExecutionHub.ClearGroupsForTest(); }
    }

    private sealed class FailRestoreCommit(bool committed) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
            => committed ? ValueTask.FromResult(result)
                : ValueTask.FromException<InterceptionResult>(new IOException("Database commit was not applied."));

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
            => Task.FromException(new IOException("Database committed; its acknowledgement was lost."));
    }

    private sealed class RejectCommitVerification(bool unavailable) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
            => unavailable && command.CommandText.Contains("FROM \"AuditLog\"", StringComparison.Ordinal)
                ? ValueTask.FromException<InterceptionResult<DbDataReader>>(new IOException("Database temporarily unavailable."))
                : ValueTask.FromResult(result);
    }

    [Theory]
    [InlineData("startWorkflow", "workflowNameOrId")]
    [InlineData("forEach", "childWorkflowNameOrId")]
    [InlineData("aiAgent", "agent")]
    [InlineData("aiAgentTeam", "members")]
    public async Task RenameRoundTrip_RemapsChildToRestoredWorkflow(string activity, string targetKey)
    {
        var ct = TestContext.Current.CancellationToken;
        using var src = TestDbFactory.Create();
        var childId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var agent = new JsonObject { ["tools"] = new JsonArray(new JsonObject
            { ["name"] = "workflow_run", ["workflowIds"] = new JsonArray(childId.ToString()) }) };
        var config = activity switch
        {
            "aiAgent" => new JsonObject { ["agent"] = agent },
            "aiAgentTeam" => new JsonObject { ["members"] = new JsonArray(agent) },
            _ => new JsonObject { [targetKey] = childId.ToString() },
        };
        src.Workflows.AddRange(
            new Workflow { Id = childId, Name = "Child", DefinitionJson = EmptyDefinition },
            new Workflow { Id = parentId, Name = "Parent", DefinitionJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                nodes = new[] { new { id = "call", type = activity, data = new
                    { config } } },
                edges = Array.Empty<object>(),
            }) });
        await src.SaveChangesAsync(ct);
        var backup = await ExportAsync(src, [BackupSections.Workflows]);
        using var dst = TestDbFactory.Create();
        dst.Workflows.AddRange(
            new Workflow { Id = childId, Name = "Child", DefinitionJson = EmptyDefinition },
            new Workflow { Id = parentId, Name = "Parent", DefinitionJson = EmptyDefinition });
        await dst.SaveChangesAsync(ct);

        await Restore(dst).RestoreAsync(backup, Passphrase,
            new Dictionary<string, RestoreConflictPolicy> { [BackupSections.Workflows] = RestoreConflictPolicy.Rename },
            RestoreActor, ct);

        dst.ChangeTracker.Clear();
        var restoredChild = await dst.Workflows.SingleAsync(w => w.Name == "Child (Restored 2)", ct);
        var restoredParent = await dst.Workflows.SingleAsync(w => w.Name == "Parent (Restored 2)", ct);
        NodePilot.Core.WorkflowDefinitions.WorkflowResourceReferences.Enumerate(JsonNode.Parse(restoredParent.DefinitionJson))
            .Where(r => r.Kind == "workflow").Should().ContainSingle().Which.Id.Should().Be(restoredChild.Id);
        (await dst.Workflows.SingleAsync(w => w.Id == parentId, ct)).DefinitionJson.Should().Be(EmptyDefinition);
    }

    [Fact]
    public async Task Restore_DanglingChildWorkflowReference_KeepsReferenceAsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var src = TestDbFactory.Create();
        var deletedChildId = Guid.NewGuid();
        src.Workflows.AddRange(
            new Workflow { Id = Guid.NewGuid(), Name = "Imported", DefinitionJson = StartWorkflowDefinition(Guid.Empty) },
            new Workflow { Id = Guid.NewGuid(), Name = "Orphaned", DefinitionJson = StartWorkflowDefinition(deletedChildId) });
        await src.SaveChangesAsync(ct);
        var backup = await ExportAsync(src, [BackupSections.Workflows]);
        using var dst = TestDbFactory.Create();

        await Restore(dst).RestoreAsync(backup, Passphrase,
            new Dictionary<string, RestoreConflictPolicy> { [BackupSections.Workflows] = RestoreConflictPolicy.Skip },
            RestoreActor, ct);

        ChildWorkflowIds(await dst.Workflows.SingleAsync(w => w.Name == "Imported", ct)).Should().Equal(Guid.Empty);
        ChildWorkflowIds(await dst.Workflows.SingleAsync(w => w.Name == "Orphaned", ct)).Should().Equal(deletedChildId);

        static string StartWorkflowDefinition(Guid childId) => System.Text.Json.JsonSerializer.Serialize(new
        {
            nodes = new[] { new { id = "call", type = "startWorkflow", data = new
                { config = new { workflowNameOrId = childId.ToString() } } } },
            edges = Array.Empty<object>(),
        });
        static IEnumerable<Guid> ChildWorkflowIds(Workflow w)
            => NodePilot.Core.WorkflowDefinitions.WorkflowResourceReferences.Enumerate(JsonNode.Parse(w.DefinitionJson))
                .Where(r => r.Kind == "workflow").Select(r => r.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameRoundTrip_RemapsAgentInfrastructureBindings(bool team)
    {
        using var src = TestDbFactory.Create();
        var machineId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var agent = new JsonObject
        {
            ["targetMachineId"] = machineId.ToString(), ["credentialId"] = credentialId.ToString(),
            ["tools"] = new JsonArray()
        };
        var config = team ? new JsonObject { ["members"] = new JsonArray(agent) }
            : new JsonObject { ["agent"] = agent };
        src.Credentials.Add(new Credential { Id = credentialId, Name = "svc", Username = "svc", EncryptedPassword = _atRest.Protect("secret") });
        src.ManagedMachines.Add(new ManagedMachine { Id = machineId, Name = "host", Hostname = "source.example" });
        src.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "Agent", DefinitionJson = new JsonObject
        {
            ["nodes"] = new JsonArray(new JsonObject { ["id"] = "agent", ["type"] = team ? "aiAgentTeam" : "aiAgent",
                ["data"] = new JsonObject { ["config"] = config } }), ["edges"] = new JsonArray()
        }.ToJsonString() });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Workflows, BackupSections.Credentials, BackupSections.Machines]);
        using var dst = TestDbFactory.Create();
        dst.Credentials.Add(new Credential { Id = credentialId, Name = "svc", Username = "old", EncryptedPassword = _atRest.Protect("old-secret") });
        dst.ManagedMachines.Add(new ManagedMachine { Id = machineId, Name = "host", Hostname = "original.example" });
        await dst.SaveChangesAsync();
        await Restore(dst).RestoreAsync(backup, Passphrase, new Dictionary<string, RestoreConflictPolicy>
        {
            [BackupSections.Credentials] = RestoreConflictPolicy.Rename,
            [BackupSections.Machines] = RestoreConflictPolicy.Rename,
        }, RestoreActor, CancellationToken.None);
        var machine = await dst.ManagedMachines.SingleAsync(m => m.Id != machineId);
        var credential = await dst.Credentials.SingleAsync(c => c.Id != credentialId);
        var restored = JsonNode.Parse((await dst.Workflows.SingleAsync()).DefinitionJson);
        var refs = NodePilot.Core.WorkflowDefinitions.WorkflowResourceReferences.Enumerate(restored).ToList();
        refs.Single(r => r.Kind == "machine").Id.Should().Be(machine.Id);
        refs.Single(r => r.Kind == "credential").Id.Should().Be(credential.Id);
        (await dst.ManagedMachines.FindAsync(machineId))!.Hostname.Should().Be("original.example");
    }

    [Fact]
    public async Task FullRoundTrip_IntoEmptyDb_RestoresEverythingWithFidelity()
    {
        using var src = TestDbFactory.Create();
        var (machineId, credId, _) = await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        var result = await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        result.Sections.Should().Contain(r => r.Section == BackupSections.Workflows && r.Created == 1);

        // Credential password rewrapped under the target's at-rest protector to decrypts to
        // original.
        var cred = dst.Credentials.Single(c => c.Name == "svc");
        _atRest.Unprotect(cred.EncryptedPassword).Should().Be("the-password");
        cred.Id.Should().Be(credId, "a fresh DB reuses the backup sourceId");
        cred.ExpiresAt.Should().Be(new DateTime(2026, 12, 31, 18, 0, 0, DateTimeKind.Utc),
            "the expiry date must round-trip or CredentialExpiring alerts go dark after DR");

        // Machine.DefaultCredentialId remapped onto the restored credential (K3/K4).
        dst.ManagedMachines.Single(m => m.Name == "web01").DefaultCredentialId.Should().Be(cred.Id);

        // Workflow definition: inline apiKey decrypted back to plaintext; GUID refs remapped
        // (identity here, K13).
        var def = (JsonObject)JsonNode.Parse(dst.Workflows.Single(w => w.Name == "wf1").DefinitionJson)!;
        var data = def["nodes"]![0]!["data"]!;
        data["config"]!["apiKey"]!.GetValue<string>().Should().Be("super-secret-key");
        data["targetMachineId"]!.GetValue<string>().Should().Be(machineId.ToString());
        data["credentialId"]!.GetValue<string>().Should().Be(credId.ToString());

        // Secret global rewrapped + decryptable; non-secret stays plain.
        var token = dst.GlobalVariables.Single(v => v.Name == "API_TOKEN");
        _atRest.Unprotect(Convert.FromBase64String(token.Value)).Should().Be("tok-123");
        dst.GlobalVariables.Single(v => v.Name == "API_URL").Value.Should().Be("https://api.local");
    }

    [Fact]
    public async Task UserRoundTrip_PreservesCanonicalExternalIdentityAndDirectoryMetadata()
    {
        using var src = TestDbFactory.Create();
        src.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin,
            PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true,
        });
        var alice = new User
        {
            Id = Guid.NewGuid(), Username = "alice@firma.de", Provider = AuthProvider.Ldap,
            ExternalId = "S-1-5-21-1-2-3-1001", Role = UserRole.Viewer, IsActive = true,
            LastDirectorySyncAt = new DateTime(2026, 7, 12, 8, 30, 0, DateTimeKind.Utc),
            DirectorySyncStatus = "Current",
        };
        src.Users.Add(alice);
        src.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = alice.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority,
            Subject = alice.ExternalId,
            CreatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            LastSeenAt = new DateTime(2026, 7, 12, 8, 30, 0, DateTimeKind.Utc),
        });
        src.DirectoryMemberships.Add(new DirectoryMembership
        {
            UserId = alice.Id,
            Authority = ExternalIdentity.ActiveDirectoryAuthority,
            GroupKey = "S-1-5-21-1-2-3-2001",
            LastSeenAt = new DateTime(2026, 7, 12, 8, 30, 0, DateTimeKind.Utc),
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Users]);

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        var restored = dst.Users.Single(u => u.Username == alice.Username);
        restored.LastDirectorySyncAt.Should().Be(alice.LastDirectorySyncAt);
        restored.DirectorySyncStatus.Should().Be("Current");
        dst.ExternalIdentities.Should().ContainSingle(i =>
            i.UserId == restored.Id
            && i.Authority == ExternalIdentity.ActiveDirectoryAuthority
            && i.Subject == alice.ExternalId);
        dst.DirectoryMemberships.Should().ContainSingle(m =>
            m.UserId == restored.Id
            && m.Authority == ExternalIdentity.ActiveDirectoryAuthority
            && m.GroupKey == "S-1-5-21-1-2-3-2001");
        dst.Users.Single(u => u.Username == "admin").IsBreakGlass.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserOverwrite_ChangedMembershipSnapshotInvalidatesSession(bool freshnessOnly)
    {
        var ct = TestContext.Current.CancellationToken;
        using var src = TestDbFactory.Create();
        var userId = Guid.NewGuid();
        var observed = DateTime.UtcNow.AddMinutes(-20);
        const string authority = "https://identity.example.test";
        src.Users.AddRange(
            new User { Id = Guid.NewGuid(), Username = "recovery", Role = UserRole.Admin,
                PasswordHash = "hash", IsActive = true, IsBreakGlass = true },
            new User { Id = userId, Username = "viewer", Role = UserRole.Viewer,
                Provider = AuthProvider.Oidc, IsActive = true, ExternalId = "viewer-subject" });
        src.ExternalIdentities.Add(new ExternalIdentity
            { Id = Guid.NewGuid(), UserId = userId, Authority = authority, Subject = "viewer-subject" });
        src.DirectoryMemberships.Add(new DirectoryMembership
            { UserId = userId, Authority = authority, GroupKey = "old-group", LastSeenAt = observed });
        await src.SaveChangesAsync(ct);
        var backup = await ExportAsync(src, [BackupSections.Users]);
        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, ct);
        var membership = await dst.DirectoryMemberships.SingleAsync(m => m.UserId == userId, ct);
        if (freshnessOnly) membership.LastSeenAt = DateTime.UtcNow;
        else
        {
            dst.DirectoryMemberships.Remove(membership);
            dst.DirectoryMemberships.Add(new DirectoryMembership
                { UserId = userId, Authority = authority, GroupKey = "current-group", LastSeenAt = observed });
        }
        await dst.SaveChangesAsync(ct);
        var oldStamp = (await dst.Users.SingleAsync(u => u.Id == userId, ct)).SecurityStamp;

        await Restore(dst).RestoreAsync(backup, Passphrase,
            new Dictionary<string, RestoreConflictPolicy> { [BackupSections.Users] = RestoreConflictPolicy.Overwrite },
            RestoreActor, ct);

        dst.ChangeTracker.Clear();
        (await dst.Users.SingleAsync(u => u.Id == userId, ct)).SecurityStamp.Should().Be(oldStamp + 1);
    }

    [Fact]
    public async Task FolderGrantRoundTrip_PreservesOidcAuthorityNamespace()
    {
        const string issuer = "https://issuer.example.test/tenant";
        using var src = TestDbFactory.Create();
        src.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin,
            Provider = AuthProvider.Local, PasswordHash = "hash", IsActive = true,
            IsBreakGlass = true,
        });
        var folder = new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = SharedWorkflowFolder.RootFolderId,
            Name = "OIDC", Path = "/OIDC", Depth = 1,
        };
        src.SharedWorkflowFolders.Add(folder);
        src.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = folder.Id,
            PrincipalType = FolderPrincipalType.Group,
            PrincipalAuthority = issuer,
            PrincipalKey = "finance-team",
            Role = SharedFolderRole.FolderOperator,
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Folders]);

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        dst.SharedFolderPermissions.Should().ContainSingle(permission =>
            permission.PrincipalAuthority == issuer
            && permission.PrincipalKey == "finance-team"
            && permission.Role == SharedFolderRole.FolderOperator);
    }

    [Fact]
    public async Task GlobalVariableFolders_RoundTrip_PreservesTreeAndMembership()
    {
        using var src = TestDbFactory.Create();
        // K11 needs an active admin to restore.
        src.Users.Add(new User { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true });
        var env = new GlobalVariableFolder { Id = Guid.NewGuid(), ParentFolderId = GlobalVariableFolder.RootFolderId, Name = "Environment", Path = "/Environment", Depth = 1 };
        var prod = new GlobalVariableFolder { Id = Guid.NewGuid(), ParentFolderId = env.Id, Name = "Prod", Path = "/Environment/Prod", Depth = 2 };
        src.GlobalVariableFolders.AddRange(env, prod);
        src.GlobalVariables.Add(new GlobalVariable { Id = Guid.NewGuid(), Name = "DB_HOST", Value = "db.prod", IsSecret = false, FolderId = prod.Id });
        await src.SaveChangesAsync();

        var backup = await ExportAsync(src, new List<string>
        {
            BackupSections.Users, BackupSections.GlobalVariableFolders, BackupSections.GlobalVariables,
        });

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        // The nested tree is restored (a fresh DB reuses the backup source ids).
        var restoredProd = dst.GlobalVariableFolders.Single(f => f.Path == "/Environment/Prod");
        restoredProd.Depth.Should().Be(2);
        restoredProd.ParentFolderId.Should().Be(dst.GlobalVariableFolders.Single(f => f.Path == "/Environment").Id);
        // The variable lands back in its subfolder — folder membership survived the round-trip.
        dst.GlobalVariables.Single(v => v.Name == "DB_HOST").FolderId.Should().Be(restoredProd.Id);
    }

    [Fact]
    public async Task Restore_CreatedWorkflow_GetsTheRestoringUserAsRuntimePrincipal()
    {
        // Every automated dispatch resolves its principal from Workflow.PublishedByUserId, and a
        // restored workflow arrives enabled, so the fill in /enable never runs for it. Without a
        // principal here the disaster-recovery case breaks: restore succeeds, the workflow shows
        // as active, and every trigger fire is cancelled as "missing_effective_principal".
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        dst.Workflows.Single(w => w.Name == "wf1").PublishedByUserId.Should().Be(RestoreActor);
    }

    [Fact]
    public async Task Restore_OverwrittenWorkflow_AlsoGetsTheRestoringUserAsRuntimePrincipal()
    {
        // The overwrite branch re-arms IsEnabled from the backup, so a row that had no principal
        // needs one here too — otherwise the overwrite hands back a workflow that cannot fire.
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var sourceWorkflowId = src.Workflows.Single(w => w.Name == "wf1").Id;
        var backup = await ExportAsync(src, [BackupSections.Workflows]);

        using var dst = TestDbFactory.Create();
        dst.Workflows.Add(new Workflow
        {
            Id = sourceWorkflowId,
            Name = "wf1",
            DefinitionJson = "{\"nodes\":[],\"edges\":[]}",
            FolderId = SharedWorkflowFolder.RootFolderId,
            PublishedByUserId = null,
        });
        await dst.SaveChangesAsync();

        await Restore(dst).RestoreAsync(
            backup, Passphrase, Policy(BackupSections.Workflows, RestoreConflictPolicy.Overwrite),
            RestoreActor, CancellationToken.None);

        dst.Workflows.Single(w => w.Name == "wf1").PublishedByUserId.Should().Be(RestoreActor);
    }

    [Fact]
    public async Task Restore_WithoutAPrincipal_LeavesTheColumnNull()
    {
        // First-boot provisioning restores with no user acting. The workflow then needs one
        // Publish before its triggers can fire — but nothing is silently attributed to a guess.
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), null, CancellationToken.None);

        dst.Workflows.Single(w => w.Name == "wf1").PublishedByUserId.Should().BeNull();
    }

    [Fact]
    public async Task Restore_Twice_WithSkipPolicy_DoesNotDuplicate()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);
        var second = await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        second.Sections.Single(r => r.Section == BackupSections.Workflows).Skipped.Should().Be(1);
        dst.Workflows.Count(w => w.Name == "wf1").Should().Be(1);
        dst.Credentials.Count(c => c.Name == "svc").Should().Be(1);
    }

    // ---- workflow identity: id first, then name within the target folder ----
    // Workflow.Name has no unique index, so a backup can carry two distinct "Deploy" workflows.

    private static readonly List<string> FoldersUsersWorkflows =
        [BackupSections.Folders, BackupSections.Users, BackupSections.Workflows];

    private const string EmptyDefinition = "{\"nodes\":[],\"edges\":[]}";

    private static string Definition(string label) =>
        "{\"nodes\":[{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"log\",\"label\":\""
        + label + "\",\"config\":{}}}],\"edges\":[]}";

    private static SharedWorkflowFolder Folder(string name) => new()
    {
        Id = Guid.NewGuid(), ParentFolderId = SharedWorkflowFolder.RootFolderId, Name = name, Path = "/" + name, Depth = 1,
    };

    private static async Task<(Guid first, Guid second)> SeedSameNamedWorkflowsAsync(NodePilotDbContext db, bool sameFolder)
    {
        var team = Folder("team");
        var ops = Folder("ops");
        db.SharedWorkflowFolders.AddRange(team, ops);
        db.Users.Add(new User { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true });
        var first = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Deploy", DefinitionJson = Definition("first"), FolderId = team.Id,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var second = new Workflow
        {
            Id = Guid.NewGuid(), Name = "Deploy", DefinitionJson = Definition("second"), FolderId = sameFolder ? team.Id : ops.Id,
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        db.Workflows.AddRange(first, second);
        await db.SaveChangesAsync();
        return (first.Id, second.Id);
    }

    private static BackupPreviewSection WorkflowPreview(BackupPreviewResult preview) =>
        preview.Sections.Single(s => s.Section == BackupSections.Workflows);

    private static SectionRestoreResult WorkflowResult(BackupRestoreResult result) =>
        result.Sections.Single(r => r.Section == BackupSections.Workflows);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_SameNamedWorkflows_RestoresEveryOneWithItsId(bool sameFolder)
    {
        using var src = TestDbFactory.Create();
        var (first, second) = await SeedSameNamedWorkflowsAsync(src, sameFolder);
        var backup = await ExportAsync(src, FoldersUsersWorkflows);

        using var dst = TestDbFactory.Create();
        var preview = await Restore(dst).PreviewAsync(backup, Passphrase, CancellationToken.None);
        var result = await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        var section = WorkflowPreview(preview);
        (section.InBackup, section.New, section.Conflicts).Should().Be((2, 2, 0));
        WorkflowResult(result).Created.Should().Be(2);
        var restored = dst.Workflows.Where(w => w.Name == "Deploy").ToList();
        restored.Select(w => w.Id).Should().BeEquivalentTo([first, second]);
        restored.Single(w => w.Id == first).DefinitionJson.Should().Contain("first");
        restored.Single(w => w.Id == second).DefinitionJson.Should().Contain("second");
    }

    [Fact]
    public async Task Restore_SkipPolicy_MatchesATargetWorkflowByIdBeforeName()
    {
        using var src = TestDbFactory.Create();
        var (first, _) = await SeedSameNamedWorkflowsAsync(src, sameFolder: false);
        var backup = await ExportAsync(src, FoldersUsersWorkflows);

        using var dst = TestDbFactory.Create();
        // The same workflow, renamed and moved on the target.
        dst.Workflows.Add(new Workflow { Id = first, Name = "Deploy (renamed)", DefinitionJson = EmptyDefinition, FolderId = SharedWorkflowFolder.RootFolderId });
        await dst.SaveChangesAsync();

        var preview = await Restore(dst).PreviewAsync(backup, Passphrase, CancellationToken.None);
        var result = await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        WorkflowPreview(preview).Conflicts.Should().Be(1);
        var section = WorkflowResult(result);
        (section.Created, section.Skipped).Should().Be((1, 1));
        dst.Workflows.Single(w => w.Id == first).Name.Should().Be("Deploy (renamed)", "skip leaves the target row untouched");
        dst.Workflows.Count().Should().Be(2);
    }

    [Theory]
    [InlineData(RestoreConflictPolicy.Skip, false)]
    [InlineData(RestoreConflictPolicy.Skip, true)]
    [InlineData(RestoreConflictPolicy.Overwrite, false)]
    [InlineData(RestoreConflictPolicy.Overwrite, true)]
    [InlineData(RestoreConflictPolicy.Rename, false)]
    [InlineData(RestoreConflictPolicy.Rename, true)]
    public async Task Restore_SharedTargetConflict_PreservesEachBackupWorkflow(
        RestoreConflictPolicy policy, bool reverseOrder)
    {
        using var src = TestDbFactory.Create();
        var (first, second) = await SeedSameNamedWorkflowsAsync(src, sameFolder: true);
        var backup = await ExportAsync(src, FoldersUsersWorkflows);
        if (reverseOrder)
            backup = MutatePayload(backup, payload =>
            {
                var items = (JsonArray)payload["sections"]![BackupSections.Workflows]!["items"]!;
                var reversed = items.Reverse().Select(item => item!.DeepClone()).ToArray();
                items.Clear();
                foreach (var item in reversed) items.Add(item);
            });

        using var dst = TestDbFactory.Create();
        var team = Folder("team");
        dst.SharedWorkflowFolders.Add(team);
        dst.Workflows.Add(new Workflow { Id = first, Name = "Deploy", DefinitionJson = Definition("target"), FolderId = team.Id });
        await dst.SaveChangesAsync();

        var preview = await Restore(dst).PreviewAsync(backup, Passphrase, CancellationToken.None);
        WorkflowPreview(preview).Conflicts.Should().Be(1);
        var result = await Restore(dst).RestoreAsync(
            backup, Passphrase, Policy(BackupSections.Workflows, policy), RestoreActor, CancellationToken.None);

        var workflows = await dst.Workflows.ToListAsync();
        workflows.Should().HaveCount(policy == RestoreConflictPolicy.Rename ? 3 : 2);
        workflows.Single(w => w.Id == second).DefinitionJson.Should().Contain("second");
        workflows.Single(w => w.Id == first).DefinitionJson.Should().Contain(
            policy == RestoreConflictPolicy.Overwrite ? "first" : "target");
        if (policy == RestoreConflictPolicy.Rename)
        {
            workflows.Select(w => w.Name).Should().OnlyHaveUniqueItems();
            workflows.Should().Contain(w => w.Id != first && w.DefinitionJson.Contains("first"));
        }
        else
        {
            WorkflowResult(result).Created.Should().Be(1);
            (WorkflowResult(result).Skipped + WorkflowResult(result).Overwritten).Should().Be(1);
        }
    }

    [Fact]
    public async Task Restore_SkipPolicy_TreatsTheSameNameInAnotherFolderAsANewWorkflow()
    {
        using var src = TestDbFactory.Create();
        await SeedSameNamedWorkflowsAsync(src, sameFolder: false);
        var backup = await ExportAsync(src, FoldersUsersWorkflows);

        using var dst = TestDbFactory.Create();
        var unrelated = new Workflow { Id = Guid.NewGuid(), Name = "Deploy", DefinitionJson = EmptyDefinition, FolderId = SharedWorkflowFolder.RootFolderId };
        dst.Workflows.Add(unrelated);
        await dst.SaveChangesAsync();

        var preview = await Restore(dst).PreviewAsync(backup, Passphrase, CancellationToken.None);
        var result = await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        WorkflowPreview(preview).Conflicts.Should().Be(0);
        WorkflowResult(result).Created.Should().Be(2);
        dst.Workflows.Count(w => w.Name == "Deploy").Should().Be(3);
        dst.Workflows.Single(w => w.Id == unrelated.Id).DefinitionJson.Should().Be(EmptyDefinition);
    }

    [Fact]
    public async Task Restore_OverwritePolicy_UpdatesTheSameNamedWorkflowInTheSameFolder()
    {
        using var src = TestDbFactory.Create();
        await SeedSameNamedWorkflowsAsync(src, sameFolder: false);
        var backup = await ExportAsync(src, FoldersUsersWorkflows);

        using var dst = TestDbFactory.Create();
        // Same folder path as the backup's /team, fresh ids on both rows: matched by folder + name.
        var team = Folder("team");
        var existing = new Workflow { Id = Guid.NewGuid(), Name = "Deploy", DefinitionJson = EmptyDefinition, FolderId = team.Id };
        dst.SharedWorkflowFolders.Add(team);
        dst.Workflows.Add(existing);
        await dst.SaveChangesAsync();

        var preview = await Restore(dst).PreviewAsync(backup, Passphrase, CancellationToken.None);
        var result = await Restore(dst).RestoreAsync(
            backup, Passphrase, Policy(BackupSections.Workflows, RestoreConflictPolicy.Overwrite), RestoreActor, CancellationToken.None);

        WorkflowPreview(preview).Conflicts.Should().Be(1);
        var section = WorkflowResult(result);
        (section.Created, section.Overwritten).Should().Be((1, 1));
        var after = dst.Workflows.Single(w => w.Id == existing.Id);
        after.DefinitionJson.Should().Contain("first");
        after.FolderId.Should().Be(team.Id);
        dst.Workflows.Count(w => w.FolderId == team.Id).Should().Be(1);
    }

    [Fact]
    public async Task Restore_RenamePolicy_OnAnIdConflict_CreatesASuffixedCopyWithAFreshId()
    {
        using var src = TestDbFactory.Create();
        var (first, _) = await SeedSameNamedWorkflowsAsync(src, sameFolder: false);
        var backup = await ExportAsync(src, FoldersUsersWorkflows);

        using var dst = TestDbFactory.Create();
        var team = Folder("team");
        dst.SharedWorkflowFolders.Add(team);
        dst.Workflows.Add(new Workflow { Id = first, Name = "Deploy", DefinitionJson = EmptyDefinition, FolderId = team.Id });
        await dst.SaveChangesAsync();

        var result = await Restore(dst).RestoreAsync(
            backup, Passphrase, Policy(BackupSections.Workflows, RestoreConflictPolicy.Rename), RestoreActor, CancellationToken.None);

        var section = WorkflowResult(result);
        (section.Created, section.Renamed).Should().Be((1, 1));
        dst.Workflows.Single(w => w.Id == first).DefinitionJson.Should().Be(EmptyDefinition, "rename leaves the target row intact");
        var copy = dst.Workflows.Single(w => w.FolderId == team.Id && w.Id != first);
        copy.Name.Should().Be("Deploy (Restored 2)");
        copy.DefinitionJson.Should().Contain("first");
    }

    [Fact]
    public async Task Restore_Overwrite_UpdatesExistingAndBumpsUserSecurityStamp()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var sourceAdminId = src.Users.Single(user => user.Username == "admin").Id;
        var backup = await ExportAsync(src, [BackupSections.Users]);

        using var dst = TestDbFactory.Create();
        var existing = new User { Id = sourceAdminId, Username = "admin", Role = UserRole.Operator, PasswordHash = "old", IsActive = true, SecurityStamp = 5 };
        dst.Users.Add(existing);
        await dst.SaveChangesAsync();

        await Restore(dst).RestoreAsync(backup, Passphrase, Policy(BackupSections.Users, RestoreConflictPolicy.Overwrite), RestoreActor, CancellationToken.None);

        var after = dst.Users.Single(u => u.Username == "admin");
        after.Role.Should().Be(UserRole.Admin);              // overwritten from backup
        after.SecurityStamp.Should().Be(6, "role+hash change must invalidate live sessions (K16)");
        after.PasswordChangedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Restore_OverwriteLockedWorkflow_AbortsWithoutMutatingEditSession()
    {
        using var src = TestDbFactory.Create();
        src.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "restore-admin", Role = UserRole.Admin,
            PasswordHash = "h", IsActive = true, IsBreakGlass = true,
        });
        src.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "locked-wf",
            DefinitionJson = "{\"nodes\":[],\"edges\":[]}",
            FolderId = SharedWorkflowFolder.RootFolderId,
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Workflows]);

        using var dst = TestDbFactory.Create();
        var owner = Guid.NewGuid();
        var original = new Workflow
        {
            Id = Guid.NewGuid(), Name = "locked-wf", DefinitionJson = "{}",
            FolderId = SharedWorkflowFolder.RootFolderId,
            CheckedOutByUserId = owner, CheckedOutAt = DateTime.UtcNow,
            IsEnabled = false,
        };
        dst.Workflows.Add(original);
        await dst.SaveChangesAsync();

        var act = () => Restore(dst).RestoreAsync(
            backup, Passphrase,
            Policy(BackupSections.Workflows, RestoreConflictPolicy.Overwrite),
            RestoreActor, CancellationToken.None);

        await act.Should().ThrowAsync<BackupRestoreException>().WithMessage("*locked*");
        var after = await dst.Workflows.AsNoTracking().SingleAsync(w => w.Id == original.Id);
        after.DefinitionJson.Should().Be("{}");
        after.CheckedOutByUserId.Should().Be(owner);
    }

    [Fact]
    public async Task Restore_OverwriteWorkflow_SnapshotsEncryptedHistory_BumpsVersion_AndRecomputesMetadata()
    {
        const string restoredDefinition =
            """{"nodes":[{"id":"t","data":{"activityType":"scheduleTrigger","config":{"cron":"0 * * * *"}}},{"id":"a","data":{"activityType":"log","config":{}}}],"edges":[]}""";
        using var src = TestDbFactory.Create();
        src.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "restore-wf", DefinitionJson = restoredDefinition,
            FolderId = SharedWorkflowFolder.RootFolderId, Version = 2, IsEnabled = true,
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Workflows]);

        const string previousDefinition =
            """{"nodes":[{"id":"s","data":{"activityType":"runScript","config":{"script":"Write-Output 'historic-secret'"}}}],"edges":[]}""";
        using var dst = TestDbFactory.Create();
        var existing = new Workflow
        {
            Id = Guid.NewGuid(), Name = "restore-wf", DefinitionJson = previousDefinition,
            FolderId = SharedWorkflowFolder.RootFolderId, Version = 7, IsEnabled = false,
        };
        dst.Workflows.Add(existing);
        await dst.SaveChangesAsync();

        await Restore(dst).RestoreAsync(
            backup, Passphrase,
            Policy(BackupSections.Workflows, RestoreConflictPolicy.Overwrite),
            RestoreActor, CancellationToken.None);

        dst.ChangeTracker.Clear();
        var restored = await dst.Workflows.SingleAsync(w => w.Id == existing.Id);
        restored.Version.Should().Be(8, "overwrite restore is a new revision of the target workflow");
        restored.DefinitionJson.Should().Be(restoredDefinition);
        var expectedMetadata = WorkflowMetadata.Compute(restoredDefinition);
        restored.ActivityCount.Should().Be(expectedMetadata.ActivityCount);
        restored.TriggerTypesJson.Should().Be(expectedMetadata.TriggerTypesJson);

        var history = await dst.WorkflowVersions.SingleAsync(v => v.WorkflowId == existing.Id);
        history.Version.Should().Be(7);
        history.DefinitionJson.Should().NotContain("historic-secret");
        VersionProtector().Unprotect(history.DefinitionJson).Should().Be(previousDefinition);
    }

    [Fact]
    public async Task Restore_WouldLeaveNoActiveAdmin_Aborts()
    {
        // Source carries 'admin' as a Viewer; overwriting the target's only Admin would orphan the
        // system.
        using var src = TestDbFactory.Create();
        var sharedUserId = Guid.NewGuid();
        src.Users.Add(new User { Id = sharedUserId, Username = "admin", Role = UserRole.Viewer, PasswordHash = "h", IsActive = true });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Users]);

        using var dst = TestDbFactory.Create();
        dst.Users.Add(new User { Id = sharedUserId, Username = "admin", Role = UserRole.Admin, PasswordHash = "h", IsActive = true });
        await dst.SaveChangesAsync();

        var act = () => Restore(dst).RestoreAsync(backup, Passphrase, Policy(BackupSections.Users, RestoreConflictPolicy.Overwrite), RestoreActor, CancellationToken.None);
        await act.Should().ThrowAsync<BackupRestoreException>().WithMessage("*no active Admin*");
    }

    [Theory]
    [InlineData(RestoreConflictPolicy.Skip)]
    [InlineData(RestoreConflictPolicy.Overwrite)]
    public async Task Restore_UsernameCollisionAcrossLocalAndExternalIdentity_NeverMerges(
        RestoreConflictPolicy policy)
    {
        using var src = TestDbFactory.Create();
        src.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "source-recovery", Provider = AuthProvider.Local,
            Role = UserRole.Admin, PasswordHash = "hash", IsActive = true, IsBreakGlass = true,
        });
        var external = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Oidc,
            ExternalId = "subject-a", Role = UserRole.Viewer, IsActive = true,
        };
        src.Users.Add(external);
        src.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = external.Id,
            Authority = "https://issuer-a.example.test", Subject = "subject-a",
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Users]);

        using var dst = TestDbFactory.Create();
        dst.Users.AddRange(
            new User
            {
                Id = Guid.NewGuid(), Username = "target-recovery", Provider = AuthProvider.Local,
                Role = UserRole.Admin, PasswordHash = "hash", IsActive = true, IsBreakGlass = true,
            },
            new User
            {
                Id = Guid.NewGuid(), Username = external.Username, Provider = AuthProvider.Local,
                Role = UserRole.Viewer, PasswordHash = "hash", IsActive = true,
            });
        await dst.SaveChangesAsync();

        var act = () => Restore(dst).RestoreAsync(
            backup, Passphrase, Policy(BackupSections.Users, policy), RestoreActor, CancellationToken.None);

        await act.Should().ThrowAsync<BackupRestoreException>()
            .WithMessage("*different identity*never merged by username*");
    }

    [Fact]
    public async Task Restore_OidcIssuerMismatchWithSameUsername_RefusesOverwrite()
    {
        using var src = TestDbFactory.Create();
        src.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "source-recovery", Provider = AuthProvider.Local,
            Role = UserRole.Admin, PasswordHash = "hash", IsActive = true, IsBreakGlass = true,
        });
        var sourceUser = new User
        {
            Id = Guid.NewGuid(), Username = "alice@example.test", Provider = AuthProvider.Oidc,
            ExternalId = "same-subject", Role = UserRole.Viewer, IsActive = true,
        };
        src.Users.Add(sourceUser);
        src.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = sourceUser.Id,
            Authority = "https://issuer-a.example.test", Subject = "same-subject",
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Users]);

        using var dst = TestDbFactory.Create();
        dst.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "target-recovery", Provider = AuthProvider.Local,
            Role = UserRole.Admin, PasswordHash = "hash", IsActive = true, IsBreakGlass = true,
        });
        var targetUser = new User
        {
            Id = Guid.NewGuid(), Username = sourceUser.Username, Provider = AuthProvider.Oidc,
            ExternalId = "same-subject", Role = UserRole.Viewer, IsActive = true,
        };
        dst.Users.Add(targetUser);
        dst.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = targetUser.Id,
            Authority = "https://issuer-b.example.test", Subject = "same-subject",
        });
        await dst.SaveChangesAsync();

        var act = () => Restore(dst).RestoreAsync(
            backup, Passphrase,
            Policy(BackupSections.Users, RestoreConflictPolicy.Overwrite),
            RestoreActor, CancellationToken.None);

        await act.Should().ThrowAsync<BackupRestoreException>()
            .WithMessage("*different identity*never merged by username*");
    }

    [Theory]
    [InlineData(RestoreConflictPolicy.Skip)]
    [InlineData(RestoreConflictPolicy.Overwrite)]
    public async Task Restore_SameGuidAndOidcSubjectButDifferentIssuer_IsNeverTheSameIdentity(
        RestoreConflictPolicy policy)
    {
        var sharedUserId = Guid.NewGuid();
        using var src = TestDbFactory.Create();
        var sourceUser = new User
        {
            Id = sharedUserId, Username = "alice@issuer-a.example.test", Provider = AuthProvider.Oidc,
            ExternalId = "same-subject", Role = UserRole.Viewer, IsActive = true,
        };
        src.Users.Add(sourceUser);
        src.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = sharedUserId,
            Authority = "https://issuer-a.example.test", Subject = "same-subject",
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Users]);

        using var dst = TestDbFactory.Create();
        dst.Users.Add(new User
        {
            Id = Guid.NewGuid(), Username = "target-recovery", Provider = AuthProvider.Local,
            Role = UserRole.Admin, PasswordHash = "hash", IsActive = true, IsBreakGlass = true,
        });
        dst.Users.Add(new User
        {
            Id = sharedUserId, Username = "alice@issuer-b.example.test", Provider = AuthProvider.Oidc,
            ExternalId = "same-subject", Role = UserRole.Viewer, IsActive = true,
        });
        dst.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(), UserId = sharedUserId,
            Authority = "https://issuer-b.example.test", Subject = "same-subject",
        });
        await dst.SaveChangesAsync();

        var act = () => Restore(dst).RestoreAsync(
            backup, Passphrase, Policy(BackupSections.Users, policy), RestoreActor, CancellationToken.None);

        await act.Should().ThrowAsync<BackupRestoreException>()
            .WithMessage($"*source id {sharedUserId} belongs to a different target identity*");
        (await dst.ExternalIdentities.AsNoTracking().SingleAsync(identity => identity.UserId == sharedUserId))
            .Authority.Should().Be("https://issuer-b.example.test");
    }

    [Fact]
    public async Task Restore_UnresolvableWorkflowReference_Aborts()
    {
        using var src = TestDbFactory.Create();
        // Workflow references a machine GUID that is not a real machine row to absent from the
        // export.
        var bogus = Guid.NewGuid();
        var def = "{\"nodes\":[{\"id\":\"s1\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"targetMachineId\":\"" + bogus + "\",\"config\":{}}}],\"edges\":[]}";
        src.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = def, FolderId = SharedWorkflowFolder.RootFolderId });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Workflows]);

        using var dst = TestDbFactory.Create();
        var act = () => Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);
        await act.Should().ThrowAsync<BackupRestoreException>().WithMessage("*unresolvable*");
    }

    [Fact]
    public async Task Restore_TamperedCiphertext_FailsAuthentication()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        // Flip one ciphertext byte. AES-GCM authentication rejects the whole payload.
        var env = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(backup))!;
        var payload = Convert.FromBase64String(env["payload"]!.GetValue<string>());
        payload[payload.Length / 2] ^= 0x01;
        env["payload"] = Convert.ToBase64String(payload);
        var tampered = Encoding.UTF8.GetBytes(env.ToJsonString());

        using var dst = TestDbFactory.Create();
        var act = () => Restore(dst).RestoreAsync(tampered, Passphrase, Empty(), RestoreActor, CancellationToken.None);
        await act.Should().ThrowAsync<BackupFormatException>().WithMessage("*authentication*");
    }

    [Fact]
    public async Task Restore_WrongPassphrase_Aborts()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        var act = () => Restore(dst).RestoreAsync(backup, "wrong-passphrase-x", Empty(), RestoreActor, CancellationToken.None);
        await act.Should().ThrowAsync<BackupRestoreException>().WithMessage("*assphrase*");
    }

    [Fact]
    public async Task Restore_UnrecoverableSecretWarning_RollsBackTheCompleteRestore()
    {
        using var src = TestDbFactory.Create();
        src.Credentials.Add(new Credential
        {
            Id = Guid.NewGuid(), Name = "svc", Username = "svc",
            EncryptedPassword = _atRest.Protect("secret"),
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Credentials]);
        var incomplete = MutatePayload(backup, payload =>
            payload["sections"]![BackupSections.Credentials]!["items"]![0]!["password"] = null);

        using var dst = TestDbFactory.Create();
        var act = () => Restore(dst).RestoreAsync(
            incomplete, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        await act.Should().ThrowAsync<BackupRestoreException>()
            .WithMessage("*incomplete*");
        dst.ChangeTracker.Clear();
        (await dst.Credentials.AsNoTracking().CountAsync()).Should().Be(0,
            "a restore that cannot recover every selected secret must leave no partial rows");
    }

    [Fact]
    public async Task Restore_FolderConflict_RenamePolicy_CreatesUniqueSibling_NoIndexViolation()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src); // child folder "team" at /team under Root
        var backup = await ExportAsync(src, [BackupSections.Folders]);

        using var dst = TestDbFactory.Create();
        // Pre-existing folder with the SAME parent+name — restoring "team" with rename must not
        // violate unique(ParentFolderId, Name); it must produce a sibling-unique name instead.
        dst.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = SharedWorkflowFolder.RootFolderId, Name = "team", Path = "/team", Depth = 1,
        });
        await dst.SaveChangesAsync();

        var act = () => Restore(dst).RestoreAsync(backup, Passphrase, Policy(BackupSections.Folders, RestoreConflictPolicy.Rename), RestoreActor, CancellationToken.None);
        await act.Should().NotThrowAsync();

        var team = dst.SharedWorkflowFolders
            .Where(f => f.ParentFolderId == SharedWorkflowFolder.RootFolderId && f.Name.StartsWith("team"))
            .Select(f => f.Name).ToList();
        team.Should().HaveCount(2);
        team.Should().Contain("team (Restored 2)");
    }

    [Fact]
    public async Task Restore_FolderConflict_RenamePolicy_NestedChildPathFollowsRenamedParent()
    {
        using var src = TestDbFactory.Create();
        src.Users.Add(new User { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true });
        var team = new SharedWorkflowFolder { Id = Guid.NewGuid(), ParentFolderId = SharedWorkflowFolder.RootFolderId, Name = "team", Path = "/team", Depth = 1 };
        var sub = new SharedWorkflowFolder { Id = Guid.NewGuid(), ParentFolderId = team.Id, Name = "sub", Path = "/team/sub", Depth = 2 };
        src.SharedWorkflowFolders.AddRange(team, sub);
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Folders]);

        using var dst = TestDbFactory.Create();
        // Pre-existing /team forces the restored "team" to rename; the child "sub" must then derive
        // its
        // Path from the *renamed* parent, not the stale backup path "/team/sub".
        dst.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = SharedWorkflowFolder.RootFolderId, Name = "team", Path = "/team", Depth = 1,
        });
        await dst.SaveChangesAsync();

        await Restore(dst).RestoreAsync(backup, Passphrase, Policy(BackupSections.Folders, RestoreConflictPolicy.Rename), RestoreActor, CancellationToken.None);

        var renamedTeam = dst.SharedWorkflowFolders.Single(f => f.Name == "team (Restored 2)");
        renamedTeam.Path.Should().Be("/team (Restored 2)");
        var restoredSub = dst.SharedWorkflowFolders.Single(f => f.Name == "sub");
        restoredSub.Path.Should().Be("/team (Restored 2)/sub",
            "the child Path must follow the renamed parent, not the stale backup path");
        restoredSub.ParentFolderId.Should().Be(renamedTeam.Id);
    }

    [Fact]
    public async Task Restore_GlobalFolderConflict_RenamePolicy_NestedChildPathFollowsRenamedParent()
    {
        using var src = TestDbFactory.Create();
        src.Users.Add(new User { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true });
        var env = new GlobalVariableFolder { Id = Guid.NewGuid(), ParentFolderId = GlobalVariableFolder.RootFolderId, Name = "Environment", Path = "/Environment", Depth = 1 };
        var prod = new GlobalVariableFolder { Id = Guid.NewGuid(), ParentFolderId = env.Id, Name = "Prod", Path = "/Environment/Prod", Depth = 2 };
        src.GlobalVariableFolders.AddRange(env, prod);
        src.GlobalVariables.Add(new GlobalVariable { Id = Guid.NewGuid(), Name = "DB_HOST", Value = "db.prod", IsSecret = false, FolderId = prod.Id });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Users, BackupSections.GlobalVariableFolders, BackupSections.GlobalVariables]);

        using var dst = TestDbFactory.Create();
        // Pre-existing /Environment forces the restored "Environment" to rename; the child "Prod"
        // must
        // then derive its Path from the renamed parent, and the variable must stay in the restored
        // child.
        dst.GlobalVariableFolders.Add(new GlobalVariableFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = GlobalVariableFolder.RootFolderId, Name = "Environment", Path = "/Environment", Depth = 1,
        });
        await dst.SaveChangesAsync();

        await Restore(dst).RestoreAsync(backup, Passphrase,
            Policy(BackupSections.GlobalVariableFolders, RestoreConflictPolicy.Rename), RestoreActor, CancellationToken.None);

        var renamedEnv = dst.GlobalVariableFolders.Single(f => f.Name == "Environment (Restored 2)");
        renamedEnv.Path.Should().Be("/Environment (Restored 2)");
        var restoredProd = dst.GlobalVariableFolders.Single(f => f.Name == "Prod");
        restoredProd.Path.Should().Be("/Environment (Restored 2)/Prod",
            "the child Path must follow the renamed parent, not the stale backup path \"/Environment/Prod\"");
        restoredProd.ParentFolderId.Should().Be(renamedEnv.Id);
        dst.GlobalVariables.Single(v => v.Name == "DB_HOST").FolderId.Should().Be(restoredProd.Id,
            "variable folder membership must survive the rename into the restored child");
    }

    [Fact]
    public async Task Restore_Settings_ReplacesOverrides_RemovingKeysNotInBackup()
    {
        // Backup carries only a Smtp override.
        var srcWriter = new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance);
        srcWriter.MutateAndWrite(root => root["Smtp"] = new JsonObject { ["Port"] = 2525 });
        var backup = (await new BackupService([new SettingsBackupPart(srcWriter, _atRest)])
            .ExportAsync([BackupSections.Settings], Passphrase, "admin", CancellationToken.None)).Content;

        // Target has an extra override ("Foo") that is NOT in the backup.
        var dstWriter = new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance);
        dstWriter.MutateAndWrite(root => { root["Foo"] = new JsonObject { ["x"] = 1 }; root["Smtp"] = new JsonObject { ["Port"] = 25 }; });

        using var dst = TestDbFactory.Create();
        var restore = new BackupRestoreService(
            dst, _atRest, dstWriter, NullLogger<BackupRestoreService>.Instance, VersionProtector());
        await restore.RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        var after = dstWriter.ReadOrEmpty();
        after.ContainsKey("Foo").Should().BeFalse("an override absent from the backup must be removed (replace, not merge)");
        after["Smtp"]!["Port"]!.GetValue<int>().Should().Be(2525);
    }

    [Fact]
    public async Task Preview_WithoutPassphrase_IsRejectedBecausePayloadIsEncrypted()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        var act = () => Restore(dst).PreviewAsync(backup, passphrase: null, CancellationToken.None);
        await act.Should().ThrowAsync<BackupRestoreException>().WithMessage("*passphrase is required*");
    }

    private static Dictionary<string, RestoreConflictPolicy> Empty() => new(StringComparer.Ordinal);
    // -------------------------------------------------- concurrency limit round-trip

    [Fact]
    public async Task Restore_CreateBranch_RestoresTheConcurrencyLimit()
    {
        using var src = TestDbFactory.Create();
        src.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "limited", DefinitionJson = "{}",
            FolderId = SharedWorkflowFolder.RootFolderId, MaxConcurrentExecutions = 5,
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Workflows]);

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        (await dst.Workflows.SingleAsync()).MaxConcurrentExecutions.Should().Be(5);
    }

    [Fact]
    public async Task Restore_OverwriteBranch_RestoresTheConcurrencyLimit()
    {
        using var src = TestDbFactory.Create();
        src.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "limited", DefinitionJson = "{}",
            FolderId = SharedWorkflowFolder.RootFolderId, MaxConcurrentExecutions = 3,
        });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Workflows]);

        using var dst = TestDbFactory.Create();
        dst.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "limited", DefinitionJson = "{}",
            FolderId = SharedWorkflowFolder.RootFolderId, MaxConcurrentExecutions = 99,
        });
        await dst.SaveChangesAsync();

        await Restore(dst).RestoreAsync(
            backup, Passphrase,
            Policy(BackupSections.Workflows, RestoreConflictPolicy.Overwrite),
            RestoreActor, CancellationToken.None);

        dst.ChangeTracker.Clear();
        (await dst.Workflows.SingleAsync()).MaxConcurrentExecutions.Should().Be(3);
    }

    [Fact]
    public async Task Restore_LegacyBackupWithoutTheField_RestoresAsUnlimited()
    {
        using var src = TestDbFactory.Create();
        src.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "legacy", DefinitionJson = "{}",
            FolderId = SharedWorkflowFolder.RootFolderId, MaxConcurrentExecutions = 4,
        });
        await src.SaveChangesAsync();
        // A backup written before the column existed simply has no key.
        var backup = MutatePayload(await ExportAsync(src, [BackupSections.Workflows]), payload =>
        {
            foreach (var item in (JsonArray)payload["sections"]![BackupSections.Workflows]!["items"]!)
                ((JsonObject)item!).Remove("maxConcurrentExecutions");
        });

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        (await dst.Workflows.SingleAsync()).MaxConcurrentExecutions.Should().BeNull();
    }

    [Fact]
    public async Task Restore_OutOfRangeConcurrencyLimit_RestoresAsUnlimited()
    {
        // Restore writes the entity directly, so the range rule has to hold here too.
        using var src = TestDbFactory.Create();
        src.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), Name = "bogus", DefinitionJson = "{}",
            FolderId = SharedWorkflowFolder.RootFolderId, MaxConcurrentExecutions = 2,
        });
        await src.SaveChangesAsync();
        var backup = MutatePayload(await ExportAsync(src, [BackupSections.Workflows]), payload =>
        {
            foreach (var item in (JsonArray)payload["sections"]![BackupSections.Workflows]!["items"]!)
                ((JsonObject)item!)["maxConcurrentExecutions"] = 999999;
        });

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, CancellationToken.None);

        (await dst.Workflows.SingleAsync()).MaxConcurrentExecutions.Should().BeNull();
    }

    private static Dictionary<string, RestoreConflictPolicy> Policy(string section, RestoreConflictPolicy p)
        => new(StringComparer.Ordinal) { [section] = p };

    [Fact]
    public async Task Restore_CommitInProgress_HoldsBothTreeLocksUntilTransactionCompletes()
    {
        using var src = TestDbFactory.Create();
        var backup = await ExportAsync(src, [BackupSections.Folders]);
        var (connection, seed) = TestDbFactory.CreateWithConnection();
        using (connection)
        using (seed)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var interceptor = new PauseRestoreCommit();
            using var dst = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>()
                .UseSqlite(connection).AddInterceptors(interceptor).Options);
            var restore = Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, timeout.Token);
            Task<IDisposable>? shared = null;
            Task<IDisposable>? global = null;
            try
            {
                await interceptor.Committing.Task.WaitAsync(timeout.Token);
                shared = FolderTreeMutationLock.SharedWorkflowFolders.AcquireAsync(timeout.Token);
                global = FolderTreeMutationLock.GlobalVariableFolders.AcquireAsync(timeout.Token);
                shared.IsCompleted.Should().BeFalse();
                global.IsCompleted.Should().BeFalse();
            }
            finally
            {
                interceptor.Continue.TrySetResult();
                await restore;
                if (shared is not null) (await shared).Dispose();
                if (global is not null) (await global).Dispose();
            }
        }
    }

    private sealed class PauseRestoreCommit : DbTransactionInterceptor
    {
        public TaskCompletionSource Committing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Committing.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restore_ConcurrentFolderCreation_ReadsTreeOnlyAfterAcquiringLock(bool global)
    {
        using var src = TestDbFactory.Create();
        void AddFolder(NodePilotDbContext context)
        {
            if (global)
                context.GlobalVariableFolders.Add(new GlobalVariableFolder
                {
                    Id = Guid.NewGuid(), Name = "team", Path = "/team", Depth = 1,
                    ParentFolderId = GlobalVariableFolder.RootFolderId,
                });
            else context.SharedWorkflowFolders.Add(Folder("team"));
        }
        AddFolder(src);
        await src.SaveChangesAsync();
        var section = global ? BackupSections.GlobalVariableFolders : BackupSections.Folders;
        var backup = await ExportAsync(src, [section]);
        var (connection, dst) = TestDbFactory.CreateWithConnection();
        using (connection)
        using (dst)
        using (var writer = new NodePilotDbContext(new DbContextOptionsBuilder<NodePilotDbContext>().UseSqlite(connection).Options))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var gate = global ? FolderTreeMutationLock.GlobalVariableFolders : FolderTreeMutationLock.SharedWorkflowFolders;
            Task<BackupRestoreResult> restore;
            using (await gate.AcquireAsync(timeout.Token))
            {
                restore = Restore(dst).RestoreAsync(backup, Passphrase, Empty(), RestoreActor, timeout.Token);
                restore.IsCompleted.Should().BeFalse("restore must wait before reading the target tree");
                AddFolder(writer);
                await writer.SaveChangesAsync(timeout.Token);
            }
            var result = await restore;
            result.Sections.Single(s => s.Section == section).Created.Should().Be(0,
                "the folder committed while restore waited must be visible to conflict detection");
        }
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles) if (File.Exists(f)) File.Delete(f);
    }
}

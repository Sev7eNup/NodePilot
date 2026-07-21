using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Configuration;
using NodePilot.Api.Services.Backup;
using NodePilot.Api.Services.Backup.Parts;
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
/// backup (K12), and rejection of a backup file whose whole-file MAC (tamper-evidence
/// checksum) does not verify (K5).
/// </summary>
public sealed class BackupRestoreServiceTests : IDisposable
{
    private const string Passphrase = "a-strong-backup-pass";
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
        new WorkflowBackupPart(db), new SettingsBackupPart(new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance), _atRest),
    ];

    private BackupRestoreService Restore(NodePilotDbContext db) =>
        new(db, _atRest, new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance),
            NullLogger<BackupRestoreService>.Instance);

    private async Task<byte[]> ExportAsync(NodePilotDbContext db, List<string> sections)
        => (await new BackupService(Parts(db)).ExportAsync(sections, Passphrase, "admin", CancellationToken.None)).Content;

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

    [Fact]
    public async Task FullRoundTrip_IntoEmptyDb_RestoresEverythingWithFidelity()
    {
        using var src = TestDbFactory.Create();
        var (machineId, credId, _) = await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        var result = await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), CancellationToken.None);

        result.Sections.Should().Contain(r => r.Section == BackupSections.Workflows && r.Created == 1);

        // Credential password rewrapped under the target's at-rest protector → decrypts to original.
        var cred = dst.Credentials.Single(c => c.Name == "svc");
        _atRest.Unprotect(cred.EncryptedPassword).Should().Be("the-password");
        cred.Id.Should().Be(credId, "a fresh DB reuses the backup sourceId");
        cred.ExpiresAt.Should().Be(new DateTime(2026, 12, 31, 18, 0, 0, DateTimeKind.Utc),
            "the expiry date must round-trip or CredentialExpiring alerts go dark after DR");

        // Machine.DefaultCredentialId remapped onto the restored credential (K3/K4).
        dst.ManagedMachines.Single(m => m.Name == "web01").DefaultCredentialId.Should().Be(cred.Id);

        // Workflow definition: inline apiKey decrypted back to plaintext; GUID refs remapped (identity here, K13).
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
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), CancellationToken.None);

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
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), CancellationToken.None);

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
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), CancellationToken.None);

        // The nested tree is restored (a fresh DB reuses the backup source ids).
        var restoredProd = dst.GlobalVariableFolders.Single(f => f.Path == "/Environment/Prod");
        restoredProd.Depth.Should().Be(2);
        restoredProd.ParentFolderId.Should().Be(dst.GlobalVariableFolders.Single(f => f.Path == "/Environment").Id);
        // The variable lands back in its subfolder — folder membership survived the round-trip.
        dst.GlobalVariables.Single(v => v.Name == "DB_HOST").FolderId.Should().Be(restoredProd.Id);
    }

    [Fact]
    public async Task Restore_Twice_WithSkipPolicy_DoesNotDuplicate()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), CancellationToken.None);
        var second = await Restore(dst).RestoreAsync(backup, Passphrase, Empty(), CancellationToken.None);

        second.Sections.Single(r => r.Section == BackupSections.Workflows).Skipped.Should().Be(1);
        dst.Workflows.Count(w => w.Name == "wf1").Should().Be(1);
        dst.Credentials.Count(c => c.Name == "svc").Should().Be(1);
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

        await Restore(dst).RestoreAsync(backup, Passphrase, Policy(BackupSections.Users, RestoreConflictPolicy.Overwrite), CancellationToken.None);

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
            CancellationToken.None);

        await act.Should().ThrowAsync<BackupRestoreException>().WithMessage("*locked*");
        var after = await dst.Workflows.AsNoTracking().SingleAsync(w => w.Id == original.Id);
        after.DefinitionJson.Should().Be("{}");
        after.CheckedOutByUserId.Should().Be(owner);
    }

    [Fact]
    public async Task Restore_WouldLeaveNoActiveAdmin_Aborts()
    {
        // Source carries 'admin' as a Viewer; overwriting the target's only Admin would orphan the system.
        using var src = TestDbFactory.Create();
        var sharedUserId = Guid.NewGuid();
        src.Users.Add(new User { Id = sharedUserId, Username = "admin", Role = UserRole.Viewer, PasswordHash = "h", IsActive = true });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Users]);

        using var dst = TestDbFactory.Create();
        dst.Users.Add(new User { Id = sharedUserId, Username = "admin", Role = UserRole.Admin, PasswordHash = "h", IsActive = true });
        await dst.SaveChangesAsync();

        var act = () => Restore(dst).RestoreAsync(backup, Passphrase, Policy(BackupSections.Users, RestoreConflictPolicy.Overwrite), CancellationToken.None);
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
            backup, Passphrase, Policy(BackupSections.Users, policy), CancellationToken.None);

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
            CancellationToken.None);

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
            backup, Passphrase, Policy(BackupSections.Users, policy), CancellationToken.None);

        await act.Should().ThrowAsync<BackupRestoreException>()
            .WithMessage($"*source id {sharedUserId} belongs to a different target identity*");
        (await dst.ExternalIdentities.AsNoTracking().SingleAsync(identity => identity.UserId == sharedUserId))
            .Authority.Should().Be("https://issuer-b.example.test");
    }

    [Fact]
    public async Task Restore_UnresolvableWorkflowReference_Aborts()
    {
        using var src = TestDbFactory.Create();
        // Workflow references a machine GUID that is not a real machine row → absent from the export.
        var bogus = Guid.NewGuid();
        var def = "{\"nodes\":[{\"id\":\"s1\",\"type\":\"activity\",\"data\":{\"activityType\":\"runScript\",\"targetMachineId\":\"" + bogus + "\",\"config\":{}}}],\"edges\":[]}";
        src.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "wf", DefinitionJson = def, FolderId = SharedWorkflowFolder.RootFolderId });
        await src.SaveChangesAsync();
        var backup = await ExportAsync(src, [BackupSections.Workflows]);

        using var dst = TestDbFactory.Create();
        var act = () => Restore(dst).RestoreAsync(backup, Passphrase, Empty(), CancellationToken.None);
        await act.Should().ThrowAsync<BackupRestoreException>().WithMessage("*unresolvable*");
    }

    [Fact]
    public async Task Restore_TamperedFile_FailsMac()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        // Flip a plaintext value without recomputing the MAC.
        var env = (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(backup))!;
        env["sections"]!["machines"]!["items"]![0]!["hostname"] = "evil.local";
        var tampered = Encoding.UTF8.GetBytes(env.ToJsonString());

        using var dst = TestDbFactory.Create();
        var act = () => Restore(dst).RestoreAsync(tampered, Passphrase, Empty(), CancellationToken.None);
        await act.Should().ThrowAsync<BackupRestoreException>().WithMessage("*MAC*");
    }

    [Fact]
    public async Task Restore_WrongPassphrase_Aborts()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        var act = () => Restore(dst).RestoreAsync(backup, "wrong-passphrase-x", Empty(), CancellationToken.None);
        await act.Should().ThrowAsync<BackupRestoreException>().WithMessage("*assphrase*");
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

        var act = () => Restore(dst).RestoreAsync(backup, Passphrase, Policy(BackupSections.Folders, RestoreConflictPolicy.Rename), CancellationToken.None);
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
        // Pre-existing /team forces the restored "team" to rename; the child "sub" must then derive its
        // Path from the *renamed* parent, not the stale backup path "/team/sub".
        dst.SharedWorkflowFolders.Add(new SharedWorkflowFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = SharedWorkflowFolder.RootFolderId, Name = "team", Path = "/team", Depth = 1,
        });
        await dst.SaveChangesAsync();

        await Restore(dst).RestoreAsync(backup, Passphrase, Policy(BackupSections.Folders, RestoreConflictPolicy.Rename), CancellationToken.None);

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
        // Pre-existing /Environment forces the restored "Environment" to rename; the child "Prod" must
        // then derive its Path from the renamed parent, and the variable must stay in the restored child.
        dst.GlobalVariableFolders.Add(new GlobalVariableFolder
        {
            Id = Guid.NewGuid(), ParentFolderId = GlobalVariableFolder.RootFolderId, Name = "Environment", Path = "/Environment", Depth = 1,
        });
        await dst.SaveChangesAsync();

        await Restore(dst).RestoreAsync(backup, Passphrase,
            Policy(BackupSections.GlobalVariableFolders, RestoreConflictPolicy.Rename), CancellationToken.None);

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
        var restore = new BackupRestoreService(dst, _atRest, dstWriter, NullLogger<BackupRestoreService>.Instance);
        await restore.RestoreAsync(backup, Passphrase, Empty(), CancellationToken.None);

        var after = dstWriter.ReadOrEmpty();
        after.ContainsKey("Foo").Should().BeFalse("an override absent from the backup must be removed (replace, not merge)");
        after["Smtp"]!["Port"]!.GetValue<int>().Should().Be(2525);
    }

    [Fact]
    public async Task Preview_WithoutPassphrase_ReportsIntegrityUnverified()
    {
        using var src = TestDbFactory.Create();
        await SeedFullAsync(src);
        var backup = await ExportAsync(src, AllSections);

        using var dst = TestDbFactory.Create();
        var preview = await Restore(dst).PreviewAsync(backup, passphrase: null, CancellationToken.None);

        preview.IntegrityVerified.Should().BeFalse();
        preview.Sections.Single(s => s.Section == BackupSections.Workflows).New.Should().Be(1);
    }

    private static Dictionary<string, RestoreConflictPolicy> Empty() => new(StringComparer.Ordinal);
    private static Dictionary<string, RestoreConflictPolicy> Policy(string section, RestoreConflictPolicy p)
        => new(StringComparer.Ordinal) { [section] = p };

    public void Dispose()
    {
        foreach (var f in _tempFiles) if (File.Exists(f)) File.Delete(f);
    }
}

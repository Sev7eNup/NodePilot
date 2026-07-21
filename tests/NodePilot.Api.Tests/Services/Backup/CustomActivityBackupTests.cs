using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Configuration;
using NodePilot.Api.Services.Backup;
using NodePilot.Api.Services.Backup.Parts;
using NodePilot.Core.Enums;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Api.Tests.Services.Backup;

/// <summary>
/// System-config backup (ADR 0001) integration for custom activities: round-trip fidelity (incl.
/// enabled state) and the <c>config.__customDefinitionId</c> remap on overwrite-merge restore.
/// </summary>
public sealed class CustomActivityBackupTests : IDisposable
{
    private const string Passphrase = "a-strong-backup-pass";
    private static readonly List<string> Sections = [BackupSections.CustomActivities, BackupSections.Workflows];
    private readonly AesGcmSecretProtector _atRest = new(Key());
    private readonly List<string> _tempFiles = [];

    private static byte[] Key() { var k = new byte[32]; for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 7); return k; }
    private string TempPath() { var p = Path.Combine(Path.GetTempPath(), "np-ca-bk-" + Guid.NewGuid().ToString("N") + ".json"); _tempFiles.Add(p); return p; }

    // Full part set so the Workflows dependency closure (folders/credentials/machines/globals) resolves.
    private IBackupPart[] Parts(NodePilotDbContext db) =>
    [
        new FolderBackupPart(db), new UserBackupPart(db), new CredentialBackupPart(db, _atRest),
        new MachineBackupPart(db), new GlobalVariableFolderBackupPart(db), new GlobalVariableBackupPart(new GlobalVariableStore(db, _atRest)),
        new CustomActivityBackupPart(new CustomActivityDefinitionStore(db)),
        new WorkflowBackupPart(db),
    ];

    private BackupRestoreService Restore(NodePilotDbContext db) =>
        new(db, _atRest, new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance),
            NullLogger<BackupRestoreService>.Instance);

    private async Task<byte[]> ExportAsync(NodePilotDbContext db) =>
        (await new BackupService(Parts(db)).ExportAsync(Sections, Passphrase, "admin", CancellationToken.None)).Content;

    private static async Task<CustomActivityDefinition> SeedAsync(NodePilotDbContext db, bool enabled)
    {
        var store = new CustomActivityDefinitionStore(db);
        var def = await store.CreateAsync(new CustomActivityDefinitionInput
        {
            Key = "disk_check", Name = "Disk Check", ScriptTemplate = "Get-PSDrive C",
            OutputParametersJson = "[{\"name\":\"status\",\"type\":\"string\"}]",
        }, "alice", CancellationToken.None);
        if (enabled) await store.SetEnabledAsync(def.Id, true, "admin", CancellationToken.None);
        return (await store.GetByIdAsync(def.Id, CancellationToken.None))!;
    }

    // The Workflows dependency closure pulls in the Users section; a restore must not leave the
    // system without an admin (K11), so the source carries one.
    private static Guid SeedAdmin(NodePilotDbContext db, Guid? id = null)
    {
        var adminId = id ?? Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = adminId, Username = "admin", Role = UserRole.Admin,
            PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true,
        });
        db.SaveChanges();
        return adminId;
    }

    private static void SeedWorkflow(NodePilotDbContext db, Guid defId)
    {
        var def = "{\"nodes\":[{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"custom:disk_check\","
            + "\"config\":{\"__customDefinitionId\":\"" + defId + "\",\"__customKey\":\"disk_check\"}}}],\"edges\":[]}";
        db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "wf1", DefinitionJson = def, FolderId = SharedWorkflowFolder.RootFolderId, IsEnabled = false });
        db.SaveChanges();
    }

    private static string CustomDefIdInFirstNode(string definitionJson)
    {
        using var doc = JsonDocument.Parse(definitionJson);
        return doc.RootElement.GetProperty("nodes")[0].GetProperty("data").GetProperty("config")
            .GetProperty("__customDefinitionId").GetString()!;
    }

    [Fact]
    public async Task RoundTrip_IntoEmptyDb_PreservesDefinitionAndReference()
    {
        using var src = TestDbFactory.Create();
        SeedAdmin(src);
        var def = await SeedAsync(src, enabled: true);
        SeedWorkflow(src, def.Id);
        var backup = await ExportAsync(src);

        using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(backup, Passphrase, new Dictionary<string, RestoreConflictPolicy>(), CancellationToken.None);

        var restored = await new CustomActivityDefinitionStore(dst).GetByKeyAsync("disk_check", CancellationToken.None);
        restored.Should().NotBeNull();
        restored!.Id.Should().Be(def.Id, "source id is preserved on a clean restore");
        restored.IsEnabled.Should().BeTrue("system-backup restores the enabled state faithfully (unlike .npca import)");
        restored.ScriptTemplate.Should().Be("Get-PSDrive C");

        var wf = dst.Workflows.Single();
        CustomDefIdInFirstNode(wf.DefinitionJson).Should().Be(def.Id.ToString(), "the workflow reference still resolves");
    }

    [Fact]
    public async Task OverwriteMerge_RemapsWorkflowReferenceToExistingDefinitionId()
    {
        using var src = TestDbFactory.Create();
        var adminId = SeedAdmin(src);
        var srcDef = await SeedAsync(src, enabled: true);
        SeedWorkflow(src, srcDef.Id);
        var backup = await ExportAsync(src);

        // Destination already has the same key under a DIFFERENT id (separate store → fresh GUID).
        using var dst = TestDbFactory.Create();
        SeedAdmin(dst, adminId);
        var dstDef = await SeedAsync(dst, enabled: true);
        dstDef.Id.Should().NotBe(srcDef.Id);

        await Restore(dst).RestoreAsync(backup, Passphrase, new Dictionary<string, RestoreConflictPolicy>
        {
            [BackupSections.CustomActivities] = RestoreConflictPolicy.Overwrite,
            [BackupSections.Workflows] = RestoreConflictPolicy.Overwrite,
        }, CancellationToken.None);

        var wf = dst.Workflows.Single();
        CustomDefIdInFirstNode(wf.DefinitionJson).Should().Be(dstDef.Id.ToString(),
            "the restored workflow's __customDefinitionId is remapped to the destination's existing definition id");
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles) { try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ } }
    }
}

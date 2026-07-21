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
/// Backup schema v2 (ADR 0008): alerting rules + system policies round-trip through export/restore with
/// route-secret passphrase-rewrap and scope-target remap; the envelope advertises v2 and both schemas import.
/// </summary>
public sealed class BackupAlertingTests : IDisposable
{
    private const string Passphrase = "a-strong-backup-pass";
    private readonly AesGcmSecretProtector _atRest = new(Key());
    private readonly List<string> _tempFiles = [];

    private static byte[] Key()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 13);
        return k;
    }

    private string TempPath()
    {
        var p = Path.Combine(Path.GetTempPath(), "np-alert-backup-" + Guid.NewGuid().ToString("N") + ".json");
        _tempFiles.Add(p);
        return p;
    }

    // Alerting DependsOn Folders + Workflows, whose closure pulls Users/Credentials/Machines/globals — the
    // export needs a part registered for every section in that closure.
    private IBackupPart[] Parts(NodePilotDbContext db) =>
    [
        new FolderBackupPart(db), new UserBackupPart(db), new CredentialBackupPart(db, _atRest),
        new MachineBackupPart(db), new GlobalVariableFolderBackupPart(db), new GlobalVariableBackupPart(new GlobalVariableStore(db, _atRest)),
        new WorkflowBackupPart(db), new AlertingBackupPart(db, _atRest),
    ];

    private async Task<byte[]> ExportAsync(NodePilotDbContext db, List<string> sections)
        => (await new BackupService(Parts(db)).ExportAsync(sections, Passphrase, "admin", CancellationToken.None)).Content;

    private BackupRestoreService Restore(NodePilotDbContext db) =>
        new(db, _atRest, new RuntimeOverridesWriter(TempPath(), NullLogger<RuntimeOverridesWriter>.Instance),
            NullLogger<BackupRestoreService>.Instance);

    private static Dictionary<string, RestoreConflictPolicy> AllSkip() =>
        new(StringComparer.Ordinal) { [BackupSections.Folders] = RestoreConflictPolicy.Skip, [BackupSections.Workflows] = RestoreConflictPolicy.Skip, [BackupSections.Alerting] = RestoreConflictPolicy.Skip };

    [Fact]
    public async Task Export_AdvertisesSchemaV2()
    {
        await using var db = TestDbFactory.Create();
        var bytes = await ExportAsync(db, [BackupSections.Alerting]);
        var reader = BackupFileReader.Parse(bytes);
        reader.Schema.Should().Be(BackupSections.SchemaV2);
        BackupSections.SupportedSchemas.Should().Contain(BackupSections.Schema).And.Contain(BackupSections.SchemaV2);
    }

    [Fact]
    public async Task SystemPolicy_RoundTrips_WithRewrappedRouteSecret()
    {
        Guid workflowId;
        byte[] bytes;
        await using (var src = TestDbFactory.Create())
        {
            var wf = new Workflow { Id = Guid.NewGuid(), Name = "Deploy", DefinitionJson = "{}" };
            src.Workflows.Add(wf);
            src.Users.Add(new User
            {
                Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin,
                PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true,
            });
            workflowId = wf.Id;
            var ruleId = Guid.NewGuid();
            src.NotificationRules.Add(new NotificationRule
            {
                Id = ruleId, Name = "backlog-critical", EventTypes = "SystemAlert",
                Kind = NotificationRuleKind.System, SystemSourceId = "backlog", SystemPresetId = "high",
                SourceParametersJson = "{\"x\":1}", SustainForSeconds = 120, SeverityOverride = NotificationSeverity.Critical,
                FilterExpressionJson = "{\"type\":\"comparison\"}", ScopeKind = NotificationScopeKind.Workflows, IsEnabled = true,
                Routes = [new NotificationRoute { Id = Guid.NewGuid(), NotificationRuleId = ruleId, Channel = NotificationChannel.GenericWebhook, Target = "https://hook", Secret = Convert.ToBase64String(_atRest.Protect("webhook-secret")), Order = 0 }],
                Targets = [new NotificationRuleTarget { Id = Guid.NewGuid(), NotificationRuleId = ruleId, TargetKind = NotificationTargetKind.Workflow, TargetId = wf.Id }],
            });
            await src.SaveChangesAsync();
            bytes = await ExportAsync(src, [BackupSections.Alerting]);
        }

        await using var dst = TestDbFactory.Create();
        // Fresh DB must contain the referenced workflow so the target remaps (the export's DependsOn pulled it in).
        var result = await Restore(dst).RestoreAsync(bytes, Passphrase, AllSkip(), CancellationToken.None);

        result.Sections.Should().Contain(r => r.Section == BackupSections.Alerting && r.Created == 1);
        var restored = await dst.NotificationRules.Include(r => r.Routes).Include(r => r.Targets)
            .SingleAsync(r => r.Name == "backlog-critical");
        restored.Kind.Should().Be(NotificationRuleKind.System);
        restored.SystemSourceId.Should().Be("backlog");
        restored.SustainForSeconds.Should().Be(120);
        restored.SeverityOverride.Should().Be(NotificationSeverity.Critical);
        restored.ActivatedAt.Should().NotBeNull("a restored enabled System policy gets a fresh activation watermark");

        var route = restored.Routes.Should().ContainSingle().Subject;
        _atRest.Unprotect(Convert.FromBase64String(route.Secret!)).Should().Be("webhook-secret", "route secret survives the passphrase rewrap");

        restored.Targets.Should().ContainSingle().Which.TargetId.Should().Be(workflowId, "workflow scope target remapped");
    }

    [Fact]
    public async Task CustomRule_AlsoRoundTrips()
    {
        byte[] bytes;
        await using (var src = TestDbFactory.Create())
        {
            var id = Guid.NewGuid();
            src.Users.Add(new User
            {
                Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin,
                PasswordHash = "$2a$hash", IsActive = true, IsBreakGlass = true,
            });
            src.NotificationRules.Add(new NotificationRule
            {
                Id = id, Name = "prod-failures", EventTypes = "ExecutionFailed", Kind = NotificationRuleKind.Custom, IsEnabled = true,
                Routes = [new NotificationRoute { Id = Guid.NewGuid(), NotificationRuleId = id, Channel = NotificationChannel.Email, Target = "ops@x", Order = 0 }],
            });
            await src.SaveChangesAsync();
            bytes = await ExportAsync(src, [BackupSections.Alerting]);
        }

        await using var dst = TestDbFactory.Create();
        await Restore(dst).RestoreAsync(bytes, Passphrase, AllSkip(), CancellationToken.None);

        var restored = await dst.NotificationRules.Include(r => r.Routes).SingleAsync(r => r.Name == "prod-failures");
        restored.Kind.Should().Be(NotificationRuleKind.Custom);
        restored.Routes.Should().ContainSingle().Which.Target.Should().Be("ops@x");
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles) { try { File.Delete(f); } catch { /* best effort */ } }
    }
}

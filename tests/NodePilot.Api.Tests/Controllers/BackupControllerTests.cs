using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Api.Configuration;
using NodePilot.Api.Controllers;
using NodePilot.Api.Dtos;
using NodePilot.Api.Services.Backup;
using NodePilot.Api.Services.Backup.Parts;
using NodePilot.Core.Audit;
using NodePilot.Core.Enums;
using NodePilot.Core.Models;
using NodePilot.Data;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using NodePilot.Api.Tests.TestSupport;
using Xunit;

namespace NodePilot.Api.Tests.Controllers;

/// <summary>
/// HTTP contract of <see cref="BackupController"/> — the ADR-0001 system-configuration backup.
/// Covers the argument guards, the mapping of the service's exception taxonomy onto 400/409,
/// the file response, and the audit rows. The export/restore semantics themselves live in
/// Services/Backup.
/// </summary>
public sealed class BackupControllerTests : IDisposable
{
    private const string Passphrase = "a-strong-backup-pass";

    private readonly NodePilotDbContext _db;
    private readonly AesGcmSecretProtector _atRest;
    private readonly string _runtimePath;
    private readonly BackupService _backup;
    private readonly BackupRestoreService _restore;

    public BackupControllerTests()
    {
        _db = TestDbFactory.Create();
        _atRest = new AesGcmSecretProtector(DeterministicKey());
        _runtimePath = Path.Combine(Path.GetTempPath(), "np-backup-ctl-" + Guid.NewGuid().ToString("N") + ".json");
        var overrides = new RuntimeOverridesWriter(_runtimePath, NullLogger<RuntimeOverridesWriter>.Instance);
        var globals = new GlobalVariableStore(_db, _atRest);

        _backup = new BackupService(
        [
            new FolderBackupPart(_db),
            new UserBackupPart(_db),
            new CredentialBackupPart(_db, _atRest),
            new MachineBackupPart(_db),
            new GlobalVariableFolderBackupPart(_db),
            new GlobalVariableBackupPart(globals),
            new CustomActivityBackupPart(new CustomActivityDefinitionStore(_db)),
            new WorkflowBackupPart(_db),
            new SettingsBackupPart(overrides, _atRest),
        ]);
        _restore = new BackupRestoreService(
            _db, _atRest, overrides, NullLogger<BackupRestoreService>.Instance,
            new NodePilot.Api.Services.WorkflowVersionDefinitionProtector(
                _atRest, NullLogger<NodePilot.Api.Services.WorkflowVersionDefinitionProtector>.Instance));

        _db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Username = "admin",
            Role = UserRole.Admin,
            PasswordHash = "$2a$bcryptedhash",
            IsActive = true,
        });
        _db.ManagedMachines.Add(new ManagedMachine
        {
            Id = Guid.NewGuid(), Name = "srv-01", Hostname = "srv-01.contoso.test", WinRmPort = 5985,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_runtimePath)) File.Delete(_runtimePath);
    }

    // ---------------------------------------------------------------- manifest

    [Fact]
    public async Task Manifest_ReportsARowCountPerSection()
    {
        var result = await Controller().Manifest(TestContext.Current.CancellationToken);

        var manifest = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BackupManifestResponse>().Subject;
        manifest.Sections.Should().NotBeEmpty();
        manifest.Sections.Should().Contain(section => section.Section == BackupSections.Machines && section.Count == 1);
    }

    // ---------------------------------------------------------------- export

    [Fact]
    public async Task Export_WithoutSections_Returns400()
    {
        var result = await Controller().Export(
            new BackupExportRequest(null!, Passphrase),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Export_ShortPassphrase_Returns400()
    {
        var result = await Controller().Export(
            new BackupExportRequest([BackupSections.Machines], "short"),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>(
            "the service rejects weak passphrases with ArgumentException, which must not surface as a 500");
    }

    [Fact]
    public async Task Export_UnknownSection_Returns400()
    {
        var result = await Controller().Export(
            new BackupExportRequest(["NotASection"], Passphrase),
            TestContext.Current.CancellationToken);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Export_ValidRequest_ReturnsSealedFileAndWritesAudit()
    {
        var audit = new CapturingAuditWriter();

        var result = await Controller(audit).Export(
            new BackupExportRequest([BackupSections.Machines], Passphrase),
            TestContext.Current.CancellationToken);

        var file = result.Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("application/octet-stream");
        file.FileDownloadName.Should().EndWith(".npbackup");
        file.FileContents.Should().NotBeEmpty();
        audit.Calls.Should().ContainSingle().Which.Action.Should().Be(AuditActions.BackupExported);
    }

    // ---------------------------------------------------------------- preview

    [Fact]
    public async Task Preview_WithoutFile_Returns400()
    {
        var result = await Controller().Preview(
            EmptyFile(), Passphrase, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Preview_GarbageContent_Returns400InsteadOf500()
    {
        var result = await Controller().Preview(
            FileOf(Encoding.UTF8.GetBytes("this is not a backup")),
            Passphrase,
            TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Preview_ValidBackup_ReportsSectionCounts()
    {
        var backup = await ExportBytesAsync();

        var result = await Controller().Preview(
            FileOf(backup), Passphrase, TestContext.Current.CancellationToken);

        var preview = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<BackupPreviewResult>().Subject;
        preview.IntegrityVerified.Should().BeTrue();
        preview.Sections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Preview_WithoutPassphrase_StillParsesButLeavesIntegrityUnverified()
    {
        var backup = await ExportBytesAsync();

        var result = await Controller().Preview(
            FileOf(backup), passphrase: null, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<BackupPreviewResult>()
            .Which.IntegrityVerified.Should().BeFalse();
    }

    // ---------------------------------------------------------------- restore

    [Fact]
    public async Task Restore_WithoutFile_Returns400()
    {
        var result = await Controller().Restore(
            EmptyFile(), Passphrase, policy: null, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Restore_WithoutPassphrase_Returns400()
    {
        var backup = await ExportBytesAsync();

        var result = await Controller().Restore(
            FileOf(backup), passphrase: "", policy: null, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Restore_WrongPassphrase_Returns409()
    {
        var backup = await ExportBytesAsync();

        var result = await Controller().Restore(
            FileOf(backup), "definitely-the-wrong-pass", policy: null, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<ConflictObjectResult>(
            "a wrong passphrase fails the whole-file MAC and is a conflict, not a server fault");
    }

    [Fact]
    public async Task Restore_GarbageContent_Returns400()
    {
        var result = await Controller().Restore(
            FileOf(Encoding.UTF8.GetBytes("not a backup at all")),
            Passphrase,
            policy: null,
            TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Restore_ValidBackup_AppliesAndWritesAudit()
    {
        var backup = await ExportBytesAsync();
        var audit = new CapturingAuditWriter();

        var result = await Controller(audit).Restore(
            FileOf(backup), Passphrase, policy: null, TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<BackupRestoreResult>();
        audit.Calls.Should().ContainSingle().Which.Action.Should().Be(AuditActions.BackupRestored);
    }

    [Fact]
    public async Task Restore_GlobalPolicyToken_AppliesToEverySection()
    {
        var backup = await ExportBytesAsync();
        var audit = new CapturingAuditWriter();

        var result = await Controller(audit).Restore(
            FileOf(backup), Passphrase, policy: "overwrite", TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<OkObjectResult>();
        audit.Calls.Single().Details.Should().Contain("Overwrite",
            "the audit row records the effective per-section policy");
    }

    [Fact]
    public async Task Restore_PerSectionPolicy_IsParsedFromTheFormField()
    {
        var backup = await ExportBytesAsync();
        var audit = new CapturingAuditWriter();

        var result = await Controller(audit).Restore(
            FileOf(backup),
            Passphrase,
            policy: $"{BackupSections.Machines}=Rename",
            TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<OkObjectResult>();
        audit.Calls.Single().Details.Should().Contain("Rename");
    }

    [Fact]
    public async Task Restore_UnparsablePolicyToken_FallsBackToSkip()
    {
        var backup = await ExportBytesAsync();
        var audit = new CapturingAuditWriter();

        var result = await Controller(audit).Restore(
            FileOf(backup), Passphrase, policy: "Machines=NotAPolicy,,", TestContext.Current.CancellationToken);

        result.Result.Should().BeOfType<OkObjectResult>();
        audit.Calls.Single().Details.Should().Contain("Skip");
    }

    // ---------------------------------------------------------------- helpers

    private async Task<byte[]> ExportBytesAsync()
    {
        var export = await _backup.ExportAsync(
            [BackupSections.Machines], Passphrase, "admin", TestContext.Current.CancellationToken);
        return export.Content;
    }

    private static IFormFile FileOf(byte[] content) =>
        new FormFile(new MemoryStream(content), 0, content.Length, "file", "backup.npbackup");

    private static IFormFile EmptyFile() =>
        new FormFile(new MemoryStream([]), 0, 0, "file", "empty.npbackup");

    private BackupController Controller(IAuditWriter? audit = null)
    {
        var controller = new BackupController(
            _backup, _restore, audit ?? NoopAuditWriter.Instance, NullLogger<BackupController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static byte[] DeterministicKey()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 7);
        return key;
    }
}

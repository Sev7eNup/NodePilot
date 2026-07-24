using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
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
/// End-to-end export behaviour of <see cref="BackupService"/> (ADR 0001 Phase 1 — the
/// system-configuration backup feature): envelope shape, per-field secret encryption,
/// GUID-reference preservation, automatically pulling in sections a selection depends on
/// (K12), and the whole-file MAC / tamper-evidence checksum (K5). Uses an in-memory SQLite DB
/// and a deterministic at-rest AES-GCM protector.
/// </summary>
public sealed class BackupServiceExportTests : IDisposable
{
    private const string Passphrase = "a-strong-backup-pass";
    private readonly NodePilotDbContext _db;
    private readonly AesGcmSecretProtector _atRest;
    private readonly string _runtimePath;
    private readonly BackupService _service;
    private readonly Guid _machineId = Guid.NewGuid();

    public BackupServiceExportTests()
    {
        _db = TestDbFactory.Create();
        _atRest = new AesGcmSecretProtector(DeterministicKey());
        _runtimePath = Path.Combine(Path.GetTempPath(), "np-backup-test-" + Guid.NewGuid().ToString("N") + ".json");
        var overrides = new RuntimeOverridesWriter(_runtimePath, NullLogger<RuntimeOverridesWriter>.Instance);
        var globals = new GlobalVariableStore(_db, _atRest);

        var parts = new IBackupPart[]
        {
            new FolderBackupPart(_db),
            new UserBackupPart(_db),
            new CredentialBackupPart(_db, _atRest),
            new MachineBackupPart(_db),
            new GlobalVariableFolderBackupPart(_db),
            new GlobalVariableBackupPart(globals),
            new WorkflowBackupPart(_db),
            new SettingsBackupPart(overrides, _atRest),
        };
        _service = new BackupService(parts);
    }

    private static byte[] DeterministicKey()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 7);
        return k;
    }

    private async Task SeedAsync()
    {
        // Root is seeded via HasData — only add the child folder.
        var child = new SharedWorkflowFolder { Id = Guid.NewGuid(), ParentFolderId = SharedWorkflowFolder.RootFolderId, Name = "team", Path = "/team", Depth = 1 };
        _db.SharedWorkflowFolders.Add(child);

        var user = new User { Id = Guid.NewGuid(), Username = "admin", Role = UserRole.Admin, PasswordHash = "$2a$bcryptedhash", IsActive = true };
        _db.Users.Add(user);
        _db.SharedFolderPermissions.Add(new SharedFolderPermission
        {
            Id = Guid.NewGuid(), FolderId = child.Id, PrincipalType = FolderPrincipalType.User,
            PrincipalKey = user.Id.ToString(), Role = SharedFolderRole.FolderEditor, GrantedByUserId = user.Id,
        });

        var cred = new Credential { Id = Guid.NewGuid(), Name = "svc", Username = "svc-acct", EncryptedPassword = _atRest.Protect("the-password"), Domain = "CONTOSO" };
        _db.Credentials.Add(cred);
        _db.ManagedMachines.Add(new ManagedMachine { Id = _machineId, Name = "web01", Hostname = "web01.local", DefaultCredentialId = cred.Id });

        _db.GlobalVariables.Add(new GlobalVariable { Id = Guid.NewGuid(), Name = "API_URL", Value = "https://api.local", IsSecret = false });
        _db.GlobalVariables.Add(new GlobalVariable { Id = Guid.NewGuid(), Name = "API_TOKEN", Value = Convert.ToBase64String(_atRest.Protect("tok-123")), IsSecret = true });

        var definition =
            "{\"nodes\":[{\"id\":\"step-1\",\"type\":\"activity\",\"data\":{\"activityType\":\"restApi\","
            + "\"targetMachineId\":\"" + _machineId + "\",\"config\":{\"apiKey\":\"super-secret-key\","
            + "\"url\":\"https://x\"}}}],\"edges\":[]}";
        _db.Workflows.Add(new Workflow { Id = Guid.NewGuid(), Name = "wf1", DefinitionJson = definition, FolderId = child.Id });

        await _db.SaveChangesAsync();
    }

    private static JsonObject Parse(byte[] content) =>
        (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(content))!;

    [Fact]
    public async Task Export_Workflows_AutoIncludesHardDependencies()
    {
        await SeedAsync();

        var result = await _service.ExportAsync([BackupSections.Workflows], Passphrase, "admin", CancellationToken.None);

        // K12: selecting workflows pulls folders, machines, credentials (folders pulls users).
        result.IncludedSections.Should().Contain(new[]
        {
            BackupSections.Workflows, BackupSections.Machines, BackupSections.Credentials,
            BackupSections.Folders, BackupSections.Users,
        });
        result.AutoIncludedSections.Should().Contain(BackupSections.Credentials);
        result.Counts.Should().Contain(count =>
            count.Section == BackupSections.Workflows && count.Count == 1);
        result.Counts.Select(count => count.Section)
            .Should().BeEquivalentTo(result.IncludedSections);
    }

    [Fact]
    public async Task Export_EncryptsInlineWorkflowSecret_ButKeepsMachineGuidVerbatim()
    {
        await SeedAsync();
        var result = await _service.ExportAsync([BackupSections.Workflows], Passphrase, "admin", CancellationToken.None);

        var env = Parse(result.Content);
        var config = env["sections"]!["workflows"]!["items"]![0]!["definition"]!["nodes"]![0]!["data"]!;
        // apiKey was rewritten to an {"$enc": ...} object.
        config["config"]!["apiKey"]!["$enc"].Should().NotBeNull();
        // targetMachineId is a GUID reference — left verbatim for restore-time remap (K13).
        config["targetMachineId"]!.GetValue<string>().Should().Be(_machineId.ToString());
    }

    [Fact]
    public async Task Export_CredentialPassword_RewrapsUnderPassphrase()
    {
        await SeedAsync();
        var result = await _service.ExportAsync([BackupSections.Credentials], Passphrase, "admin", CancellationToken.None);

        var env = Parse(result.Content);
        var encB64 = env["sections"]!["credentials"]!["items"]![0]!["password"]!["$enc"]!.GetValue<string>();

        // Re-derive the passphrase protector from the file's salt and decrypt — must equal the original.
        var salt = Convert.FromBase64String(env["crypto"]!["salt"]!.GetValue<string>());
        var protector = PassphraseSecretProtector.Derive(Passphrase, salt);
        protector.Unprotect(Convert.FromBase64String(encB64)).Should().Be("the-password");
    }

    [Fact]
    public async Task Export_SealsWithWholeFileMac_AndDetectsTampering()
    {
        await SeedAsync();
        var result = await _service.ExportAsync(
            [BackupSections.Workflows, BackupSections.Users], Passphrase, "admin", CancellationToken.None);

        var env = Parse(result.Content);
        var salt = Convert.FromBase64String(env["crypto"]!["salt"]!.GetValue<string>());
        var protector = PassphraseSecretProtector.Derive(Passphrase, salt);

        var storedMac = Convert.FromBase64String(env["mac"]!.GetValue<string>());
        var canonical = BackupCanonicalJson.Canonicalize(env, excludeKey: "mac");
        protector.VerifyMac(canonical, storedMac).Should().BeTrue("the embedded MAC must validate over the canonical envelope");

        // Tamper with a plaintext (non-encrypted) field — the MAC must catch it.
        env["sections"]!["users"]!["items"]![0]!["role"] = "Viewer";
        var tampered = BackupCanonicalJson.Canonicalize(env, excludeKey: "mac");
        protector.VerifyMac(tampered, storedMac).Should().BeFalse("flipping a plaintext field must break the whole-file MAC");
    }

    [Fact]
    public async Task Export_NonSecretGlobalStaysPlain_SecretGlobalIsEncrypted()
    {
        await SeedAsync();
        var result = await _service.ExportAsync([BackupSections.GlobalVariables], Passphrase, "admin", CancellationToken.None);

        var items = (JsonArray)Parse(result.Content)["sections"]!["globalVariables"]!["items"]!;
        var apiUrl = items.First(n => n!["name"]!.GetValue<string>() == "API_URL")!;
        var apiToken = items.First(n => n!["name"]!.GetValue<string>() == "API_TOKEN")!;

        apiUrl["value"]!.GetValue<string>().Should().Be("https://api.local");
        apiToken["value"]!["$enc"].Should().NotBeNull();
    }

    [Fact]
    public async Task Export_SettingsOnly_NoSecrets_ReportsContainsSecretsFalse()
    {
        await SeedAsync();
        var result = await _service.ExportAsync([BackupSections.Settings], Passphrase, "admin", CancellationToken.None);
        result.ContainsSecrets.Should().BeFalse("an empty settings-only export seals no $enc fields");
    }

    [Fact]
    public async Task Export_Credentials_ReportsContainsSecretsTrue()
    {
        await SeedAsync();
        var result = await _service.ExportAsync([BackupSections.Credentials], Passphrase, "admin", CancellationToken.None);
        result.ContainsSecrets.Should().BeTrue("the credential password is sealed as a $enc field");
    }

    [Fact]
    public async Task Export_ShortPassphrase_Throws()
    {
        await SeedAsync();
        var act = () => _service.ExportAsync([BackupSections.Users], "short", "admin", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_runtimePath)) File.Delete(_runtimePath);
    }
}

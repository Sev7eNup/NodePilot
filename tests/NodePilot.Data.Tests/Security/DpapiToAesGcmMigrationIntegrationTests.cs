using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Interfaces;
using NodePilot.Core.Models;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests.Security;

/// <summary>
/// End-to-end integration test for the DPAPI → AES-GCM migration scenario.
/// Plays out the operator's actual switch path:
///   1. Operator runs NodePilot with default DPAPI, creates credentials and secret globals.
///   2. Cluster setup demands cross-host portable secrets, so the operator switches
///      <c>Secrets:Provider</c> to <c>AesGcm</c> and provides a master key.
///   3. Without re-encrypting the existing rows, every credential decrypt fails loudly.
///   4. The operator either runs a migration script (TODO, V2) or re-enters the secrets manually.
///
/// These tests use a real <see cref="Data.NodePilotDbContext"/> (SQLite in-memory) and
/// the real <see cref="CredentialStore"/> + <see cref="GlobalVariableStore"/> classes —
/// so the whole encrypt → DB → decrypt path runs the same way it does in production.
/// SQLite is used instead of Postgres only because tests must be CI-portable; the
/// byte-wise wire format and the CryptographicException semantics are provider-agnostic.
/// </summary>
public class DpapiToAesGcmMigrationIntegrationTests
{
    private static byte[] DeterministicAesKey()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 1);
        return k;
    }

    [Fact]
    public async Task Credential_WrittenUnderDpapi_FailsToDecrypt_AfterSwitchToAesGcm()
    {
        // Step 1: Operator runs default DPAPI mode, creates a credential.
        var dpapi = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
        await using var db = TestDbFactory.Create();
        var dpapiStore = new CredentialStore(db, dpapi, NullLogger<CredentialStore>.Instance);
        var cred = await dpapiStore.CreateAsync("svc-account", "DOMAIN\\svc", "MyPwd!", null, null, CancellationToken.None);
        dpapiStore.DecryptPassword(cred).Should().Be("MyPwd!", "round-trip under DPAPI works");

        // Step 2: Operator switches to AES-GCM (Secrets:Provider=AesGcm + MasterKey).
        // Without re-encrypting, the row in DB is still DPAPI-bytes.
        var aesGcm = new AesGcmSecretProtector(DeterministicAesKey());
        var newStore = new CredentialStore(db, aesGcm, NullLogger<CredentialStore>.Instance);

        // Step 3: First decrypt attempt with the new provider must fail loudly.
        var rehydratedCred = await newStore.GetAsync(cred.Id, CancellationToken.None);
        var act = () => newStore.DecryptPassword(rehydratedCred);
        act.Should().Throw<CryptographicException>(
            "AES-GCM cannot parse DPAPI ciphertext as a valid envelope — operator MUST re-enter the credential or run a migration script");
    }

    [Fact]
    public async Task GlobalVariable_WrittenUnderDpapi_DecryptFailureIsContainedToFailingRow()
    {
        // Step 1: Operator creates two secret globals + one non-secret under DPAPI.
        var dpapi = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
        await using var db = TestDbFactory.Create();
        var dpapiStore = new GlobalVariableStore(db, dpapi, NullLogger<GlobalVariableStore>.Instance);
        await dpapiStore.CreateAsync("API_TOKEN", "secret-1", isSecret: true, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);
        await dpapiStore.CreateAsync("DB_PASS", "secret-2", isSecret: true, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);
        await dpapiStore.CreateAsync("ENV", "production", isSecret: false, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);

        // Sanity: under DPAPI all three resolve.
        var resolved = await dpapiStore.GetAllResolvedAsync(CancellationToken.None);
        resolved.Should().HaveCount(3);

        // Step 2: Switch to AES-GCM.
        var aesGcm = new AesGcmSecretProtector(DeterministicAesKey());
        var newStore = new GlobalVariableStore(db, aesGcm, NullLogger<GlobalVariableStore>.Instance);

        // Step 3: Non-secret rows still resolve. Secret rows are missing from the dictionary
        // because the decrypt threw and the failure was logged + skipped — workflows that
        // reference {{globals.API_TOKEN}} fail loudly at use, not silently with empty string.
        var afterSwitch = await newStore.GetAllResolvedAsync(CancellationToken.None);
        afterSwitch.Should().NotContainKey("API_TOKEN");
        afterSwitch.Should().NotContainKey("DB_PASS");
        afterSwitch.Should().ContainKey("ENV").WhoseValue.Should().Be("production",
            "non-secret rows are unaffected by the provider switch — they were never encrypted");
    }

    [Fact]
    public async Task Credential_ReencryptedUnderAesGcm_RoundTripsCorrectly_PostMigration()
    {
        // The "happy path" of the migration: operator decrypted via DPAPI (e.g. via the
        // future migration script), then writes the new ciphertext via AES-GCM. Subsequent
        // reads work end-to-end. This is the contract a migration script must satisfy.
        await using var db = TestDbFactory.Create();
        var dpapi = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
        var dpapiStore = new CredentialStore(db, dpapi, NullLogger<CredentialStore>.Instance);
        var cred = await dpapiStore.CreateAsync("svc", "u", "OriginalPwd", null, null, CancellationToken.None);
        var plaintext = dpapiStore.DecryptPassword(cred);

        // Migration step (manual or scripted): re-encrypt with AES-GCM, write back.
        var aesGcm = new AesGcmSecretProtector(DeterministicAesKey());
        cred.EncryptedPassword = aesGcm.Protect(plaintext);
        await db.SaveChangesAsync();

        // Post-migration: a fresh CredentialStore wired with the AES-GCM provider reads OK.
        var newStore = new CredentialStore(db, aesGcm, NullLogger<CredentialStore>.Instance);
        var rehydrated = await newStore.GetAsync(cred.Id, CancellationToken.None);
        newStore.DecryptPassword(rehydrated).Should().Be("OriginalPwd",
            "after the operator (or migration script) re-encrypts every row, the new provider can decrypt them");
    }

    /// <summary>
    /// Bulk re-encrypt sweep: transitions every secret row from the legacy provider
    /// to the active one in a single admin operation. After the sweep the legacy
    /// fallback is no longer needed and can be removed from config.
    /// </summary>
    [Fact]
    public async Task ReencryptAll_MigratesEveryRowFromLegacyToActive()
    {
        var dpapi = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
        var aesGcm = new AesGcmSecretProtector(DeterministicAesKey());

        // Step 1: write 3 credentials + 2 secret globals + 1 non-secret global under DPAPI.
        await using var db = TestDbFactory.Create();
        var dpapiCreds = new CredentialStore(db, dpapi, NullLogger<CredentialStore>.Instance);
        await dpapiCreds.CreateAsync("svc-a", "u", "pw-a", null, null, CancellationToken.None);
        await dpapiCreds.CreateAsync("svc-b", "u", "pw-b", null, null, CancellationToken.None);
        await dpapiCreds.CreateAsync("svc-c", "u", "pw-c", null, null, CancellationToken.None);
        var dpapiGlobals = new GlobalVariableStore(db, dpapi, NullLogger<GlobalVariableStore>.Instance);
        await dpapiGlobals.CreateAsync("API_TOKEN", "secret-1", isSecret: true, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);
        await dpapiGlobals.CreateAsync("DB_PASS", "secret-2", isSecret: true, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);
        await dpapiGlobals.CreateAsync("ENV", "production", isSecret: false, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);

        // Step 2: switch to a MigratingSecretProtector wrapping AES-GCM (active) +
        // DPAPI (legacy). Bulk-rewrite via the new ReencryptAll* methods.
        var migrating = new MigratingSecretProtector(aesGcm, dpapi);
        var migCreds = new CredentialStore(db, migrating, NullLogger<CredentialStore>.Instance);
        var migGlobals = new GlobalVariableStore(db, migrating, NullLogger<GlobalVariableStore>.Instance);

        var credsResult = await migCreds.ReencryptAllCredentialsAsync(CancellationToken.None);
        var globalsResult = await migGlobals.ReencryptAllSecretsAsync(CancellationToken.None);

        credsResult.Rewritten.Should().Be(3, "every credential was DPAPI-encrypted, all 3 must move to AES-GCM");
        credsResult.Skipped.Should().Be(0, "no broken rows in this scenario");
        credsResult.SkippedDetails.Should().BeEmpty();
        globalsResult.Rewritten.Should().Be(2, "only secret globals are rewritten; the plaintext ENV row is skipped");
        globalsResult.Skipped.Should().Be(0);

        // Step 3: drop the legacy fallback — pure-AES-GCM stores must now read every row.
        var pureAes = new CredentialStore(db, aesGcm, NullLogger<CredentialStore>.Instance);
        var allCreds = await pureAes.GetAllAsync(CancellationToken.None);
        foreach (var c in allCreds)
            pureAes.DecryptPassword(c).Should().StartWith("pw-",
                "after the sweep, the legacy protector is no longer required");

        var pureAesGlobals = new GlobalVariableStore(db, aesGcm, NullLogger<GlobalVariableStore>.Instance);
        var resolved = await pureAesGlobals.GetAllResolvedAsync(CancellationToken.None);
        resolved.Should().Contain("API_TOKEN", "secret-1");
        resolved.Should().Contain("DB_PASS", "secret-2");
        resolved.Should().Contain("ENV", "production");
    }

    /// <summary>
    /// MigratingSecretProtector unit-level: Protect always uses active, Unprotect tries
    /// active first then legacy. A row written by the legacy protector decrypts cleanly
    /// through the migrating wrapper without the caller knowing which path served it.
    /// </summary>
    [Fact]
    public void MigratingProtector_RoundTripsLegacyAndActiveBlobs()
    {
        var dpapi = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
        var aesGcm = new AesGcmSecretProtector(DeterministicAesKey());
        var migrating = new MigratingSecretProtector(aesGcm, dpapi);

        // Legacy-format blob: written by DPAPI directly.
        var legacyBlob = dpapi.Protect("legacy-secret");
        // Active-format blob: written by the migrating wrapper (which delegates to AES-GCM).
        var activeBlob = migrating.Protect("new-secret");

        migrating.Unprotect(legacyBlob).Should().Be("legacy-secret",
            "active fails to read DPAPI bytes → fallback to legacy succeeds");
        migrating.Unprotect(activeBlob).Should().Be("new-secret",
            "active reads its own bytes directly, fallback never invoked");
    }

    /// <summary>
    /// Regression test: when the sweep encounters a row whose ciphertext can't be decrypted
    /// under any configured protector, the result must surface the row in SkippedDetails so
    /// the operator sees it. The previous version silently logged + dropped the row,
    /// leaving 200 OK with an inflated "everything fine" reading.
    /// </summary>
    [Fact]
    public async Task ReencryptAll_BrokenRow_AppearsInSkippedDetails()
    {
        await using var db = TestDbFactory.Create();
        var dpapi = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
        var aesGcm = new AesGcmSecretProtector(DeterministicAesKey());

        // 1. Write a credential with DPAPI.
        var dpapiStore = new CredentialStore(db, dpapi, NullLogger<CredentialStore>.Instance);
        var goodCred = await dpapiStore.CreateAsync("good", "u", "pw-good", null, null, CancellationToken.None);

        // 2. Inject a "broken" credential by raw INSERT — bytes that neither protector
        //    can interpret (random non-DPAPI, non-AES-GCM blob).
        var brokenCred = new NodePilot.Core.Models.Credential
        {
            Id = Guid.NewGuid(),
            Name = "broken",
            Username = "u",
            EncryptedPassword = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB },
        };
        db.Credentials.Add(brokenCred);
        await db.SaveChangesAsync();

        // 3. Sweep with a MigratingSecretProtector (AES-GCM active, DPAPI legacy).
        var migrating = new MigratingSecretProtector(aesGcm, dpapi);
        var migStore = new CredentialStore(db, migrating, NullLogger<CredentialStore>.Instance);
        var result = await migStore.ReencryptAllCredentialsAsync(CancellationToken.None);

        result.Rewritten.Should().Be(1, "the good DPAPI row converted cleanly");
        result.Skipped.Should().Be(1, "the broken row could not be decrypted under either protector");
        result.SkippedDetails.Should().ContainSingle()
            .Which.Should().Match<ReencryptionSkip>(s =>
                s.Id == brokenCred.Id && s.Name == "broken");
    }

    [Fact]
    public void MigratingProtector_BothFail_ThrowsCombinedDiagnostic()
    {
        var dpapi = new DpapiSecretProtector(DataProtectionScope.CurrentUser);
        var aesGcm = new AesGcmSecretProtector(DeterministicAesKey());
        var migrating = new MigratingSecretProtector(aesGcm, dpapi);

        // Garbage bytes — neither protector can read.
        var bogus = new byte[] { 0x42, 0x42, 0x42, 0x42 };
        var act = () => migrating.Unprotect(bogus);
        act.Should().Throw<CryptographicException>()
            .WithMessage("*both protectors*",
                "the operator must see both errors, not just whichever ran second");
    }
}

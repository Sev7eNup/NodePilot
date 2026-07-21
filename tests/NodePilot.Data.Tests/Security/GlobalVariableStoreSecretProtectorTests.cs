using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Models;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests.Security;

/// <summary>
/// Confirms that swapping the registered <see cref="ISecretProtector"/> from DPAPI to
/// AES-GCM is invisible to <see cref="GlobalVariableStore"/> — round-trip works for both
/// secret and non-secret values, and switching providers between encrypt and decrypt
/// reproduces the operator's "I forgot to migrate" failure mode.
/// </summary>
public class GlobalVariableStoreSecretProtectorTests
{
    private static byte[] DeterministicKey()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 1);
        return k;
    }

    [Fact]
    public async Task SecretValue_RoundTrip_Through_AesGcmProtector()
    {
        var protector = new AesGcmSecretProtector(DeterministicKey());
        await using var db = TestDbFactory.Create();
        var store = new GlobalVariableStore(db, protector, NullLogger<GlobalVariableStore>.Instance);

        await store.CreateAsync("API_TOKEN", "topsecretvalue", isSecret: true,
            description: null, folderId: GlobalVariableFolder.RootFolderId, updatedBy: null, ct: CancellationToken.None);

        var value = await store.GetValueAsync("API_TOKEN", CancellationToken.None);
        value.Should().Be("topsecretvalue");
    }

    [Fact]
    public async Task NonSecretValue_StoredPlainText_NotEncrypted()
    {
        var protector = new AesGcmSecretProtector(DeterministicKey());
        await using var db = TestDbFactory.Create();
        var store = new GlobalVariableStore(db, protector, NullLogger<GlobalVariableStore>.Instance);

        await store.CreateAsync("ENV", "production", isSecret: false,
            description: null, folderId: GlobalVariableFolder.RootFolderId, updatedBy: null, ct: CancellationToken.None);

        // The persisted Value column for a non-secret entry must be plaintext.
        var rows = await store.GetAllAsync(CancellationToken.None);
        rows.Should().ContainSingle(r => r.Name == "ENV" && r.Value == "production");
    }

    [Fact]
    public async Task GetAllResolvedAsync_DecryptFailureOnOneEntry_DoesNotBreakOthers()
    {
        // Mimic the operator failure mode: rows were encrypted under one provider, then
        // the operator switched providers without running the migration. Decrypting
        // those rows must fail gracefully (warning logged, entry skipped) without
        // taking down every workflow that uses globals.
        var providerA = new AesGcmSecretProtector(DeterministicKey());
        await using var db = TestDbFactory.Create();
        var storeA = new GlobalVariableStore(db, providerA, NullLogger<GlobalVariableStore>.Instance);
        await storeA.CreateAsync("A", "alpha-secret", isSecret: true, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);
        await storeA.CreateAsync("B", "beta-plain", isSecret: false, null, GlobalVariableFolder.RootFolderId, null, CancellationToken.None);

        // Switch protectors mid-life — A's stored ciphertext can no longer be decrypted.
        var keyB = DeterministicKey();
        keyB[0] = 0xFF;
        var providerB = new AesGcmSecretProtector(keyB);
        var storeB = new GlobalVariableStore(db, providerB, NullLogger<GlobalVariableStore>.Instance);

        var resolved = await storeB.GetAllResolvedAsync(CancellationToken.None);
        resolved.Should().NotContainKey("A", "AES-GCM with the wrong key must fail to decrypt");
        resolved.Should().ContainKey("B").WhoseValue.Should().Be("beta-plain",
            "non-secret rows are unaffected by the provider switch");
    }
}

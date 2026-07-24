using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Audit;
using NodePilot.Data.Security;
using NodePilot.TestCommons;
using Xunit;

namespace NodePilot.Data.Tests.Security;

/// <summary>
/// Verifies <see cref="CredentialStore"/> respects the registered
/// <see cref="ISecretProtector"/>. Same shape as
/// <see cref="GlobalVariableStoreSecretProtectorTests"/> — round-trip via AES-GCM, plus
/// the "wrong provider after restore" failure mode.
/// </summary>
public class CredentialStoreSecretProtectorTests
{
    private static byte[] DeterministicKey()
    {
        var k = new byte[32];
        for (var i = 0; i < k.Length; i++) k[i] = (byte)(i + 1);
        return k;
    }

    [Fact]
    public async Task RoundTrip_Through_AesGcmProtector()
    {
        var protector = new AesGcmSecretProtector(DeterministicKey());
        await using var db = TestDbFactory.Create();
        var store = new CredentialStore(db, protector, NullLogger<CredentialStore>.Instance);

        var cred = await store.CreateAsync("svc", "username", "p4ss!", null, null, CancellationToken.None);
        var decoded = store.DecryptPassword(cred);

        decoded.Should().Be("p4ss!");
    }

    [Fact]
    public async Task DecryptWithDifferentKey_ThrowsCryptographicException_AndLogsClearly()
    {
        var providerA = new AesGcmSecretProtector(DeterministicKey());
        await using var db = TestDbFactory.Create();
        var storeA = new CredentialStore(db, providerA, NullLogger<CredentialStore>.Instance);
        var cred = await storeA.CreateAsync("svc", "u", "secret", null, null, CancellationToken.None);

        var keyB = DeterministicKey();
        keyB[0] = 0xFF;
        var providerB = new AesGcmSecretProtector(keyB);
        var storeB = new CredentialStore(db, providerB, NullLogger<CredentialStore>.Instance);

        // Re-fetch the credential through the new store to get the original ciphertext.
        var cred2 = await storeB.GetAsync(cred.Id, CancellationToken.None);
        var act = () => storeB.DecryptPassword(cred2);
        act.Should().Throw<CryptographicException>(
            "decrypting with the wrong AES-GCM key must surface a clean cryptographic failure, not a silent corruption");

        var audit = db.AuditLog.Single(entry =>
            entry.Action == AuditActions.CredentialDecryptFailed
            && entry.ResourceId == cred.Id);
        audit.Details.Should().Contain("\"provider\":\"AesGcm\"");
        audit.Details.Should().Contain("\"errorClass\":\"AuthenticationTagMismatchException\"");
        audit.Details.Should().NotContain("secret");
    }

    [Fact]
    public async Task ProviderName_Surfaces_Through_AuditDetails_Indirectly()
    {
        // We don't read AuditLog here (the audit append is fire-and-forget on a different
        // scope), but the contract-test is: ProviderName is exposed on ISecretProtector
        // and CredentialStore reads it for audit details. So if the audit row appears in
        // production it will carry the provider tag.
        var protector = new AesGcmSecretProtector(DeterministicKey());
        protector.ProviderName.Should().Be("AesGcm");
    }
}

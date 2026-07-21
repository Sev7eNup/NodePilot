using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Api.Configuration;
using NodePilot.Core.Interfaces;
using Xunit;

namespace NodePilot.Api.Tests.Configuration;

/// <summary>
/// Round-trip + failure-mode tests for the encrypting JSON configuration provider.
/// Validates that <c>enc:v1:</c>-prefixed values are decrypted transparently while
/// plain values pass through, and that any decrypt failure surfaces as a clear
/// startup error rather than ciphertext-as-plaintext leaking into <c>IOptions</c>.
/// </summary>
public sealed class EncryptingJsonConfigurationProviderTests : IDisposable
{
    private readonly string _tempDir;

    public EncryptingJsonConfigurationProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "np-enc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Reversible cipher used in tests: prefixes every plaintext byte with a fixed
    /// "magic" byte and base64s the result. Lets the test deterministically verify
    /// "the provider asked the protector to decrypt this exact blob" without dragging
    /// in a real DPAPI/AES-GCM dependency.
    /// </summary>
    private sealed class FakeProtector : ISecretProtector
    {
        public string ProviderName => "Fake";
        public bool ShouldFailDecrypt { get; set; }

        public byte[] Protect(string plaintext)
        {
            var raw = Encoding.UTF8.GetBytes(plaintext);
            var blob = new byte[raw.Length + 1];
            blob[0] = 0x42;
            Buffer.BlockCopy(raw, 0, blob, 1, raw.Length);
            return blob;
        }

        public string Unprotect(byte[] blob)
        {
            if (ShouldFailDecrypt) throw new InvalidOperationException("FakeProtector simulated decrypt failure.");
            if (blob is null || blob.Length < 1 || blob[0] != 0x42)
                throw new InvalidOperationException("FakeProtector does not recognise this blob.");
            return Encoding.UTF8.GetString(blob, 1, blob.Length - 1);
        }
    }

    private string WritePayload(string json)
    {
        var path = Path.Combine(_tempDir, "appsettings.runtime.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static IConfigurationRoot BuildWith(EncryptingJsonConfigurationSource source) =>
        new ConfigurationBuilder().Add(source).Build();

    [Fact]
    public void Load_PlainValue_PassesThroughUntouched()
    {
        var path = WritePayload("""{"Smtp":{"Host":"mail.example.com"}}""");
        var source = new EncryptingJsonConfigurationSource(path, new FakeProtector(), optional: false, reloadOnChange: false);
        var cfg = BuildWith(source);
        cfg["Smtp:Host"].Should().Be("mail.example.com");
    }

    [Fact]
    public void Load_EncryptedValue_Decrypted()
    {
        var protector = new FakeProtector();
        var ciphertext = EncryptingJsonConfigurationProvider.EncryptForPersist("super-secret", protector);
        var json = "{\"Smtp\":{\"Password\":\"" + ciphertext + "\"}}";
        var path = WritePayload(json);
        var source = new EncryptingJsonConfigurationSource(path, protector, optional: false, reloadOnChange: false);
        var cfg = BuildWith(source);
        cfg["Smtp:Password"].Should().Be("super-secret",
            "consumers must never see the enc: prefix — the provider strips it transparently during load");
    }

    [Fact]
    public void Load_MixedValues_PartialDecrypt()
    {
        var protector = new FakeProtector();
        var ciphertext = EncryptingJsonConfigurationProvider.EncryptForPersist("hunter2", protector);
        var json = "{\"Smtp\":{\"Host\":\"mail.example.com\",\"Password\":\"" + ciphertext + "\"}}";
        var path = WritePayload(json);
        var source = new EncryptingJsonConfigurationSource(path, protector, optional: false, reloadOnChange: false);
        var cfg = BuildWith(source);
        cfg["Smtp:Host"].Should().Be("mail.example.com");
        cfg["Smtp:Password"].Should().Be("hunter2");
    }

    [Fact]
    public void Load_MalformedBase64_ThrowsClearError()
    {
        var json = """{"Smtp":{"Password":"enc:v1:!!!not-base64!!!"}}""";
        var path = WritePayload(json);
        var source = new EncryptingJsonConfigurationSource(path, new FakeProtector(), optional: false, reloadOnChange: false);
        var act = () => BuildWith(source);
        // FileConfigurationProvider wraps our exception in an InvalidDataException — but the
        // actionable message we authored lives on the inner exception, which is what the
        // operator sees in the boot log. Assert against the inner type so a future change to
        // the wrapping behaviour doesn't silently neutralise the contract.
        act.Should().Throw<System.IO.InvalidDataException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Smtp:Password*",
                "the error must name the offending key so the operator can fix it without reading the override file by hand");
    }

    [Fact]
    public void Load_ProtectorRejectsCiphertext_ThrowsActionableError()
    {
        var protector = new FakeProtector { ShouldFailDecrypt = true };
        // The blob itself is structurally valid base64 of a 1-byte payload, just one the
        // (failing) protector refuses to decrypt — mirrors the "AES-GCM key rotated"
        // scenario in production.
        var ciphertext = "enc:v1:" + Convert.ToBase64String(new byte[] { 0x42, 0xAA });
        var json = "{\"Smtp\":{\"Password\":\"" + ciphertext + "\"}}";
        var path = WritePayload(json);
        var source = new EncryptingJsonConfigurationSource(path, protector, optional: false, reloadOnChange: false);
        var act = () => BuildWith(source);
        act.Should().Throw<System.IO.InvalidDataException>()
            .WithInnerException<InvalidOperationException>()
            .Where(e => e.Message.Contains("Smtp:Password") && e.Message.Contains("Fake"),
                "the error must surface both the offending key and the protector name so an operator " +
                "knows whether to suspect a key rotation, a scope change, or a stale override file");
    }

    [Fact]
    public void LooksEncrypted_PrefixDetection()
    {
        EncryptingJsonConfigurationProvider.LooksEncrypted("enc:v1:abc").Should().BeTrue();
        EncryptingJsonConfigurationProvider.LooksEncrypted("plain").Should().BeFalse();
        EncryptingJsonConfigurationProvider.LooksEncrypted(null).Should().BeFalse();
        EncryptingJsonConfigurationProvider.LooksEncrypted("").Should().BeFalse();
        EncryptingJsonConfigurationProvider.LooksEncrypted("enc:v0:something").Should().BeFalse(
            "older envelope versions must NOT be recognised as the current v1 — silently treating " +
            "an unknown version as enc:v1 would risk feeding garbage into the protector and erroring " +
            "out on something that was actually a different envelope");
    }

    [Fact]
    public void EncryptForPersist_RoundTrip()
    {
        var protector = new FakeProtector();
        var ciphertext = EncryptingJsonConfigurationProvider.EncryptForPersist("plaintext", protector);
        EncryptingJsonConfigurationProvider.LooksEncrypted(ciphertext).Should().BeTrue();

        // Decrypt through the provider to confirm the encoding is the one the provider expects.
        var path = WritePayload("{\"X\":{\"V\":\"" + ciphertext + "\"}}");
        var source = new EncryptingJsonConfigurationSource(path, protector, optional: false, reloadOnChange: false);
        var cfg = BuildWith(source);
        cfg["X:V"].Should().Be("plaintext");
    }
}

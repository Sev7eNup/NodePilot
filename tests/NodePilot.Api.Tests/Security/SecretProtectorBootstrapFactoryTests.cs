using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NodePilot.Data.Security;
using Xunit;

namespace NodePilot.Api.Tests.Security;

/// <summary>
/// Behaviour parity with <see cref="SecretProtectorRegistry"/> — the bootstrap factory
/// has to apply identical validation (no silent fall-through on typos, no DPAPI in
/// cluster mode) because it's invoked before DI exists and an operator-error would
/// otherwise produce a configuration provider that silently de-/re-encrypts under the
/// wrong key.
/// </summary>
public class SecretProtectorBootstrapFactoryTests
{
    private static IConfiguration Build(Dictionary<string, string?> kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public void Default_PicksDpapi()
    {
        var protector = SecretProtectorBootstrapFactory.FromConfigSnapshot(Build(new()));
        protector.ProviderName.Should().Be("Dpapi");
    }

    [Fact]
    public void ExplicitDpapi_PicksDpapi()
    {
        var protector = SecretProtectorBootstrapFactory.FromConfigSnapshot(Build(new()
        {
            ["Secrets:Provider"] = "Dpapi"
        }));
        protector.ProviderName.Should().Be("Dpapi");
    }

    [Fact]
    public void AesGcm_WithValidKey_PicksAesGcm()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        var protector = SecretProtectorBootstrapFactory.FromConfigSnapshot(Build(new()
        {
            ["Secrets:Provider"] = "AesGcm",
            ["Secrets:MasterKey"] = Convert.ToBase64String(key)
        }));
        protector.ProviderName.Should().Be("AesGcm");
    }

    [Fact]
    public void AesGcm_WithMasterKeyFile_PicksAesGcm()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var path = Path.Combine(Path.GetTempPath(), "nodepilot-masterkey-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, key);
            var protector = SecretProtectorBootstrapFactory.FromConfigSnapshot(Build(new()
            {
                ["Secrets:Provider"] = "AesGcm",
                ["Secrets:MasterKeyFile"] = path
            }));

            protector.ProviderName.Should().Be("AesGcm");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void UnknownProvider_Throws_WithActionableMessage()
    {
        var act = () => SecretProtectorBootstrapFactory.FromConfigSnapshot(Build(new()
        {
            ["Secrets:Provider"] = "AesGCMm"  // typo
        }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'AesGCMm'*",
                "the error must echo the offending value so a typo in YAML/ENV is obvious");
    }

    [Fact]
    public void Cluster_PlusDpapi_Throws()
    {
        var act = () => SecretProtectorBootstrapFactory.FromConfigSnapshot(Build(new()
        {
            ["Cluster:Enabled"] = "true",
            ["Secrets:Provider"] = "Dpapi"
        }));
        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("Cluster", StringComparison.OrdinalIgnoreCase)
                     && e.Message.Contains("Dpapi", StringComparison.OrdinalIgnoreCase)
                     && e.Message.Contains("AesGcm", StringComparison.OrdinalIgnoreCase),
                "the error must explain both the conflict and the recommended fix in one go");
    }

    [Fact]
    public void Cluster_PlusAesGcm_OK()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var protector = SecretProtectorBootstrapFactory.FromConfigSnapshot(Build(new()
        {
            ["Cluster:Enabled"] = "true",
            ["Secrets:Provider"] = "AesGcm",
            ["Secrets:MasterKey"] = key
        }));
        protector.ProviderName.Should().Be("AesGcm");
    }

    [Fact]
    public void AesGcm_MissingKey_Throws()
    {
        var act = () => SecretProtectorBootstrapFactory.FromConfigSnapshot(Build(new()
        {
            ["Secrets:Provider"] = "AesGcm"
            // No Secrets:MasterKey set
        }));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Secrets:MasterKey*Secrets:MasterKeyFile*");
    }

    [Fact]
    public void NullSnapshot_Throws()
    {
        var act = () => SecretProtectorBootstrapFactory.FromConfigSnapshot(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Interfaces;
using NodePilot.Data.Security;
using Xunit;

namespace NodePilot.Data.Tests.Security;

/// <summary>
/// Drives the legacy-fallback registration and the private <c>BuildProtector</c> factory in
/// <see cref="SecretProtectorRegistry"/>. The active-provider path is covered by
/// <see cref="SecretProtectorDiResolutionTests"/>; here we exercise the branches that only
/// fire when <c>Secrets:LegacyProvider</c> is set — i.e. the DPAPI↔AES-GCM rotation window
/// where the active protector is wrapped in a <see cref="MigratingSecretProtector"/>.
/// </summary>
public class SecretProtectorRegistryTests
{
    private static string ValidKeyB64()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        return Convert.ToBase64String(key);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
    {
        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in pairs) dict[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void LegacyAesGcm_WrapsActiveInMigratingProtector()
    {
        // Active = default DPAPI, legacy = AES-GCM → BuildProtector(AesGcm) path + the
        // MigratingSecretProtector registration branch.
        var config = Config(
            ("Secrets:LegacyProvider", "AesGcm"),
            ("Secrets:LegacyMasterKey", ValidKeyB64()));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNodePilotSecretProtector(config);

        using var sp = services.BuildServiceProvider();
        var protector = sp.GetRequiredService<ISecretProtector>();

        protector.Should().BeOfType<MigratingSecretProtector>();
        protector.ProviderName.Should().Be("Dpapi+AesGcm-fallback");

        // The migrating branch also registers a distinct startup-log line; resolving and
        // invoking it exercises the second AddSingleton lambda.
        var startup = sp.GetRequiredService<SecretProtectorRegistry.IStartupLogger>();
        var act = () => startup.Log();
        act.Should().NotThrow();
    }

    [Fact]
    public void LegacyAesGcm_MasterKeyFile_WrapsActiveInMigratingProtector()
    {
        var path = Path.Combine(Path.GetTempPath(), "nodepilot-legacy-masterkey-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(path, ValidKeyB64());
            var config = Config(
                ("Secrets:LegacyProvider", "AesGcm"),
                ("Secrets:LegacyMasterKeyFile", path));

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddNodePilotSecretProtector(config);

            using var sp = services.BuildServiceProvider();
            sp.GetRequiredService<ISecretProtector>().ProviderName.Should().Be("Dpapi+AesGcm-fallback");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void LegacyDpapi_WrapsActiveInMigratingProtector()
    {
        // Active = default DPAPI, legacy = DPAPI with an explicit valid scope → the
        // BuildProtector(Dpapi) return branch.
        var config = Config(
            ("Secrets:LegacyProvider", "Dpapi"),
            ("Secrets:LegacyDpapiScope", "CurrentUser"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNodePilotSecretProtector(config);

        using var sp = services.BuildServiceProvider();
        var protector = sp.GetRequiredService<ISecretProtector>();

        protector.Should().BeOfType<MigratingSecretProtector>();
        protector.ProviderName.Should().Be("Dpapi+Dpapi-fallback");
    }

    [Fact]
    public void LegacyAesGcm_MissingMasterKey_ThrowsAtStartup()
    {
        var config = Config(("Secrets:LegacyProvider", "AesGcm")); // no LegacyMasterKey

        var services = new ServiceCollection();
        var act = () => services.AddNodePilotSecretProtector(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Secrets:LegacyMasterKey*Secrets:LegacyMasterKeyFile*");
    }

    [Fact]
    public void LegacyProvider_UnknownValue_ThrowsAtStartup()
    {
        var config = Config(("Secrets:LegacyProvider", "Bogus"));

        var services = new ServiceCollection();
        var act = () => services.AddNodePilotSecretProtector(config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Secrets:LegacyProvider has unknown value 'Bogus'*");
    }

    [Fact]
    public void NoLegacyProvider_RegistersActiveDirectly_NotMigrating()
    {
        // Sanity anchor: without Secrets:LegacyProvider the else-branch registers the bare
        // active protector (no migrating wrapper).
        var config = Config(("Secrets:Provider", "AesGcm"), ("Secrets:MasterKey", ValidKeyB64()));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNodePilotSecretProtector(config);

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISecretProtector>().Should().BeOfType<AesGcmSecretProtector>();
        var act = () => sp.GetRequiredService<SecretProtectorRegistry.IStartupLogger>().Log();
        act.Should().NotThrow();
    }
}

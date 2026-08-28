using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodePilot.Core.Interfaces;
using NodePilot.Data.Security;
using Xunit;

namespace NodePilot.Data.Tests.Security;

/// <summary>
/// Pins the DI shape for <see cref="CredentialStore"/> and <see cref="GlobalVariableStore"/>:
/// each must expose exactly one constructor that .NET DI can resolve, otherwise the
/// activator throws <c>AmbiguousMatchException</c> at runtime instead of at boot.
/// <para>
/// Builds a real <see cref="ServiceProvider"/> with the same registrations as production
/// and resolves via <c>GetRequiredService</c>, so a reintroduced ambiguous constructor
/// fails at CI time.
/// </para>
/// </summary>
public class SecretProtectorDiResolutionTests
{
    private static ServiceProvider BuildProvider(string? secretsProvider = null, string? masterKey = null)
    {
        var services = new ServiceCollection();
        var configDict = new Dictionary<string, string?>
        {
            ["Secrets:Provider"] = secretsProvider,
            ["Secrets:MasterKey"] = masterKey,
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddDbContext<NodePilotDbContext>(opts => opts.UseSqlite("DataSource=:memory:"));
        services.AddNodePilotSecretProtector(config);
        services.AddScoped<ICredentialStore, CredentialStore>();
        services.AddScoped<IGlobalVariableStore, GlobalVariableStore>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void CredentialStore_ResolvesUnambiguously_WithDpapiProvider()
    {
        using var sp = BuildProvider(secretsProvider: null);
        using var scope = sp.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICredentialStore>();
        store.Should().BeOfType<CredentialStore>(
            "DI must pick the [ActivatorUtilitiesConstructor] ctor — the previous Vault PR " +
            "kept both ISecretProtector and IConfiguration ctors public, which made the " +
            "activator throw AmbiguousMatchException at first resolve");
    }

    [Fact]
    public void GlobalVariableStore_ResolvesUnambiguously_WithDpapiProvider()
    {
        using var sp = BuildProvider(secretsProvider: null);
        using var scope = sp.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGlobalVariableStore>();
        store.Should().BeOfType<GlobalVariableStore>();
    }

    [Fact]
    public void Stores_Resolve_WithAesGcmProvider_TooSizeKey()
    {
        // 32-byte all-non-zero key.
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        var keyB64 = Convert.ToBase64String(key);

        using var sp = BuildProvider(secretsProvider: "AesGcm", masterKey: keyB64);
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICredentialStore>().Should().BeOfType<CredentialStore>();
        scope.ServiceProvider.GetRequiredService<IGlobalVariableStore>().Should().BeOfType<GlobalVariableStore>();
    }

    [Fact]
    public void StoresShareTheSameProtectorInstance_WithinAScope()
    {
        // Sanity-check the singleton-protector contract: both stores in the same scope
        // resolve the same protector instance — important for AES-GCM where the master
        // key must be loaded exactly once at process start.
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        var p1 = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var p2 = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        p1.Should().BeSameAs(p2, "ISecretProtector is registered as a singleton");
    }

    /// <summary>Unknown provider values must hard-fail at startup, not silently fall back to
    /// DPAPI. A typo like "AesGCMm" would otherwise produce a machine-bound deployment
    /// that breaks on first failover.</summary>
    [Fact]
    public void Registry_UnknownProviderValue_ThrowsAtStartup()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:Provider"] = "AesGCMm",  // intentional typo
        }).Build();

        var act = () => services.AddNodePilotSecretProtector(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unknown value 'AesGCMm'*");
    }

    /// <summary>Cluster:Enabled=true with DPAPI must hard-fail: DPAPI is machine-bound, so the
    /// standby node cannot decrypt what the leader wrote.</summary>
    [Fact]
    public void Registry_ClusteredAndDpapi_ThrowsAtStartup()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Secrets:Provider"] = "Dpapi",
        }).Build();

        var act = () => services.AddNodePilotSecretProtector(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cluster:Enabled=true is incompatible with Secrets:Provider=Dpapi*");
    }

    [Fact]
    public void Registry_ClusteredAndDpapi_DefaultProvider_AlsoThrows()
    {
        // No explicit Secrets:Provider -> defaults to DPAPI -> must still fail when clustered.
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
        }).Build();

        var act = () => services.AddNodePilotSecretProtector(config);
        act.Should().Throw<InvalidOperationException>(
            "an operator who simply turned on clustering without touching Secrets:* must " +
            "see the conflict immediately, not at first failover");
    }

    /// <summary>
    /// Secrets:LegacyDpapiScope must hard-fail on a typo instead of silently falling back to
    /// CurrentUser: an operator who intended LocalMachine but mistyped the value would
    /// otherwise get a legacy fallback that can't decrypt rows the leader actually wrote.
    /// </summary>
    [Fact]
    public void Registry_LegacyDpapiScope_Typo_ThrowsAtStartup()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:Provider"] = "AesGcm",
            ["Secrets:MasterKey"] = Convert.ToBase64String(key),
            ["Secrets:LegacyProvider"] = "Dpapi",
            ["Secrets:LegacyDpapiScope"] = "Local_Machine",  // typo
        }).Build();

        var act = () => services.AddNodePilotSecretProtector(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Secrets:LegacyDpapiScope value 'Local_Machine'*not recognized*");
    }

    [Fact]
    public void Registry_ClusteredAndAesGcm_BootsCleanly()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cluster:Enabled"] = "true",
            ["Secrets:Provider"] = "AesGcm",
            ["Secrets:MasterKey"] = Convert.ToBase64String(key),
        }).Build();

        services.AddNodePilotSecretProtector(config);
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISecretProtector>().Should().BeOfType<AesGcmSecretProtector>();
    }
}

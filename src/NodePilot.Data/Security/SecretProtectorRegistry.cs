using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;

namespace NodePilot.Data.Security;

/// <summary>
/// DI registration for <see cref="ISecretProtector"/>. Picks the implementation from
/// <c>Secrets:Provider</c>: <c>"Dpapi"</c> (default, identical to pre-abstraction
/// behaviour) or <c>"AesGcm"</c> (cross-host portable, required for active/passive HA).
/// <para>
/// Reads <c>Credentials:DpapiScope</c> for the legacy DPAPI path so existing deployments
/// keep working without config changes. Registered as a singleton because the protectors
/// are stateless and the AES-GCM key only needs to be parsed once at startup.
/// </para>
/// </summary>
public static class SecretProtectorRegistry
{
    public static IServiceCollection AddNodePilotSecretProtector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Active-provider selection + cluster/DPAPI conflict check live in the factory
        // so the encrypting JSON configuration provider (which loads before DI exists)
        // gets identical semantics. The DI registration only adds the migrating-
        // fallback wrapper and the boot-log line on top of that.
        var active = SecretProtectorBootstrapFactory.FromConfigSnapshot(configuration);

        // Optional legacy-provider configuration: when set, the active protector is
        // wrapped in a MigratingSecretProtector that falls back to the legacy on read.
        // Lets a deployment rotate from DPAPI→AES-GCM (or vice versa) without manual
        // re-entry: existing rows decrypt via the legacy fallback, the bulk re-encrypt
        // endpoint then sweeps them into the active format and the legacy config can
        // be removed in a follow-up restart.
        var legacyName = (configuration["Secrets:LegacyProvider"] ?? string.Empty).Trim();
        var hasLegacy = !string.IsNullOrEmpty(legacyName);
        ISecretProtector? legacyProtector = null;
        if (hasLegacy)
        {
            legacyProtector = BuildProtector(legacyName, configuration, isLegacy: true);
        }

        if (legacyProtector is not null)
        {
            services.AddSingleton<ISecretProtector>(sp => new MigratingSecretProtector(
                active, legacyProtector,
                sp.GetService<ILoggerFactory>()?.CreateLogger<MigratingSecretProtector>()));
            services.AddSingleton<IStartupLogger>(sp => new StartupLogger(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Secrets"),
                $"Migrating secret protector enabled: active={active.ProviderName}, " +
                $"legacy={legacyProtector.ProviderName}. Run POST /api/secrets/reencrypt then " +
                "remove Secrets:LegacyProvider once the legacy_reads counter is zero."));
        }
        else
        {
            services.AddSingleton<ISecretProtector>(_ => active);
            services.AddSingleton<IStartupLogger>(sp => new StartupLogger(
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Secrets"),
                $"Secret protector enabled. Provider: {active.ProviderName}."));
        }
        return services;
    }

    /// <summary>
    /// Builds a single protector instance from configuration, used both for the active
    /// provider path and (when set) the legacy-fallback path. Legacy keys live under
    /// <c>Secrets:Legacy*</c> so an operator can run both side-by-side during a rotation.
    /// </summary>
    private static ISecretProtector BuildProtector(string providerName, IConfiguration configuration, bool isLegacy)
    {
        var prefix = isLegacy ? "Secrets:Legacy" : "Secrets:";
        if (string.Equals(providerName, "AesGcm", StringComparison.OrdinalIgnoreCase))
        {
            var keyB64 = SecretProtectorBootstrapFactory.ReadAesGcmMasterKeyMaterial(configuration, prefix);
            return new AesGcmSecretProtector(AesGcmSecretProtector.DecodeMasterKey(keyB64));
        }
        if (string.Equals(providerName, "Dpapi", StringComparison.OrdinalIgnoreCase))
        {
            // Hard-validate the scope value: a typo in Secrets:LegacyDpapiScope would
            // silently fall to CurrentUser otherwise, leading to the same "I encrypted
            // under LocalMachine but my standby reads as CurrentUser" silent breakage
            // we already fixed for the canonical Credentials:DpapiScope key.
            var scopeKey = $"{prefix}DpapiScope";
            var scope = DpapiScopeResolver.Parse(configuration[scopeKey], scopeKey);
            return new DpapiSecretProtector(scope);
        }
        throw new InvalidOperationException(
            $"{prefix}Provider has unknown value '{providerName}'. Allowed: 'Dpapi' or 'AesGcm'.");
    }

    /// <summary>
    /// Tiny helper to surface the active provider in the boot log so an operator
    /// reviewing logs can confirm which protector is in use without grepping config.
    /// </summary>
    public interface IStartupLogger
    {
        void Log();
    }

    private sealed class StartupLogger : IStartupLogger
    {
        private readonly ILogger _logger;
        private readonly string _message;
        public StartupLogger(ILogger logger, string message) { _logger = logger; _message = message; }
        public void Log() => _logger.LogInformation("{Message}", _message);
    }
}

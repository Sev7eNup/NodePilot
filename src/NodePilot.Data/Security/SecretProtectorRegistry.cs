using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Interfaces;

namespace NodePilot.Data.Security;

/// <summary>
/// DI registration for <see cref="ISecretProtector"/>. Picks the implementation from
/// <c>Secrets:Provider</c>: <c>"Dpapi"</c> (default) or <c>"AesGcm"</c> (cross-host
/// portable, required for active/passive HA).
/// <para>
/// Reads <c>Credentials:DpapiScope</c> for the DPAPI path so existing deployments work
/// without config changes. Registered as a singleton since protectors are stateless.
/// </para>
/// </summary>
public static class SecretProtectorRegistry
{
    public static IServiceCollection AddNodePilotSecretProtector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Active-provider selection and the cluster/DPAPI conflict check live in the factory,
        // so the encrypting JSON configuration provider (which loads before DI exists) gets
        // identical semantics. This registration only adds the migrating-fallback wrapper.
        var active = SecretProtectorBootstrapFactory.FromConfigSnapshot(configuration);

        // Optional legacy-provider config: when set, the active protector is wrapped in a
        // MigratingSecretProtector that falls back to the legacy provider on read. This lets
        // a deployment rotate providers without manual re-entry of every secret.
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
            // Validates the scope value explicitly: a typo in Secrets:LegacyDpapiScope would
            // otherwise silently fall back to CurrentUser, causing decryption to succeed
            // with the wrong scope instead of failing loudly.
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

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
        // Bootstrap and DI must read the same old ciphertext during the rotation window.
        // Build now so invalid configuration still fails at registration/startup.
        var protector = SecretProtectorBootstrapFactory.FromConfigSnapshot(configuration);
        services.AddSingleton<ISecretProtector>(sp => protector is MigratingSecretProtector migrating
            ? migrating.WithLogger(sp.GetService<ILoggerFactory>()?.CreateLogger<MigratingSecretProtector>())
            : protector);
        var message = $"Secret protector enabled. Provider: {protector.ProviderName}.";
        if (protector is MigratingSecretProtector)
            message += " Run POST /api/secrets/reencrypt and resolve every skip before removing Secrets:LegacyProvider.";
        services.AddSingleton<IStartupLogger>(sp => new StartupLogger(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Secrets"), message));
        return services;
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

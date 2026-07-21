using NodePilot.Core.Interfaces;
using Serilog;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Picks the WinRM session factory (default) or the NoOp test double based on
/// <c>Remote:Provider</c>. NoOp is gated behind an explicit acknowledgement env var
/// because it fakes every script as Success — leaving it on accidentally turns every
/// scheduled "delete old logs" workflow into a year of false-positive runs.
/// </summary>
public static class RemoteExecutionSetup
{
    public static IServiceCollection AddNodePilotRemoteExecution(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var remoteProvider = (configuration["Remote:Provider"] ?? "winrm").ToLowerInvariant();
        if (remoteProvider == "noop")
        {
            // M9: NoOp returns Success=true for every script regardless of what was
            // submitted, which is ruinous if it ever gets left on accidentally (a nightly
            // "delete old logs" workflow reports success on every target for a year…).
            // Fail startup unless the operator has explicitly acknowledged via
            // NODEPILOT_ALLOW_NOOP_REMOTE=1. Also emit a CRITICAL-level banner on every
            // start so it shows up even in a busy console.
            var ack = Environment.GetEnvironmentVariable("NODEPILOT_ALLOW_NOOP_REMOTE") == "1"
                || configuration.GetValue<bool>("Remote:AllowNoop");
            if (!ack)
            {
                throw new InvalidOperationException(
                    "Remote:Provider is set to 'noop' but the deployment has not acknowledged this mode. " +
                    "NoOp fakes every script as Success — never use it in production. Set environment variable " +
                    "NODEPILOT_ALLOW_NOOP_REMOTE=1 or appsettings key Remote:AllowNoop=true to confirm.");
            }
            Log.Warning("REMOTE PROVIDER = NOOP. Every workflow script will report Success regardless of content. " +
                        "This mode is intended for load testing ONLY — if you are seeing this in production, stop and fix config.");
            var noopOptions = new NodePilot.Remote.NoOpRemoteOptions
            {
                MinLatencyMs = configuration.GetValue<int?>("Remote:Noop:MinLatencyMs") ?? 0,
                MaxLatencyMs = configuration.GetValue<int?>("Remote:Noop:MaxLatencyMs") ?? 0,
                SimulateFailures = configuration.GetValue<bool?>("Remote:Noop:SimulateFailures") ?? false,
                FailureRate = configuration.GetValue<double?>("Remote:Noop:FailureRate") ?? 0.0,
            };
            services.AddSingleton(noopOptions);
            services.AddScoped<IRemoteSessionFactory, NodePilot.Remote.NoOpSessionFactory>();
        }
        else
        {
            // The concrete factory is scoped — it needs the scoped ICredentialStore (DbContext)
            // to decrypt credentials. The pool creates a short-lived scope per "create new" call
            // and resolves this instance from it.
            services.AddScoped<NodePilot.Remote.WinRmSessionFactory>();
            // The pool must outlive individual request scopes (otherwise every step would get a
            // fresh, empty pool — no session reuse at all). Registered as a singleton behind the
            // IRemoteSessionFactory interface so every activity shares the same pool.
            services.AddSingleton<NodePilot.Remote.WinRmSessionPool>();
            services.AddSingleton<IRemoteSessionFactory>(sp =>
                sp.GetRequiredService<NodePilot.Remote.WinRmSessionPool>());
        }
        return services;
    }
}

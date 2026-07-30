using Microsoft.Extensions.Primitives;
using NodePilot.Core.Configuration;
using System.Diagnostics.CodeAnalysis;

namespace NodePilot.Api.Hosting;

/// <summary>
/// Hot-reload companion to the cold-start <c>ThreadPool.SetMinThreads</c> prewarm in
/// <c>Program.cs</c>. The boot call tunes the pool once from the bootstrap config so burst
/// workloads don't stall before this service even runs; this hosted service additionally
/// re-applies <c>Threading:MinWorkerThreads</c> / <c>Threading:MinIoCompletionThreads</c>
/// from the <em>live</em> app configuration on start and whenever the config reloads (an
/// Admin-Settings-UI save writes <c>appsettings.runtime.json</c> with
/// <c>reloadOnChange:true</c>). That lets an operator re-tune the ThreadPool floor without a
/// service restart.
/// <para>
/// <c>SetMinThreads</c> is one-shot per call and process-global; this service does NOT poll —
/// it reacts to the config change token. The <c>IChangeToken</c> subscription is disposed on
/// <see cref="Dispose"/>. If the injected <see cref="IConfiguration"/> is not an
/// <see cref="IConfigurationRoot"/> (tests), only the one-shot start apply runs.
/// </para>
/// </summary>
// Coverage: every effect of this service is a process-global ThreadPool.SetMinThreads call.
// Asserting it in-process would mutate the ThreadPool for the whole test run and make other
// tests order-dependent, so the observable behaviour is deliberately left unasserted.
[ExcludeFromCodeCoverage(Justification = "Mutates process-global ThreadPool state; unsafe to assert in-process.")]
internal sealed class ThreadPoolTuningService : IHostedService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly PerformancePlan _plan;
    private readonly ILogger<ThreadPoolTuningService> _logger;
    private IDisposable? _changeSubscription;
    // Last successfully applied floor — logged for context on a rejected re-tune so ops sees
    // what the pool is actually still using.
    private int _appliedMinWorkers;
    private int _appliedMinIoc;

    public ThreadPoolTuningService(
        IConfiguration configuration,
        PerformancePlan plan,
        ILogger<ThreadPoolTuningService> logger)
    {
        _configuration = configuration;
        _plan = plan;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply();
        if (_configuration is IConfigurationRoot root)
            _changeSubscription = ChangeToken.OnChange(root.GetReloadToken, Apply);
        else
            _logger.LogInformation("ThreadPoolTuningService: config is not an IConfigurationRoot — one-shot apply only (no live reload).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _changeSubscription?.Dispose();

    private void Apply()
    {
        // Follow the mode the process actually BOOTED in, not whatever the live configuration
        // says right now. Switching Performance:ManualTuning only takes effect on restart because
        // the runspace pool and the dispatch queue are built once at startup; honouring a live
        // toggle here would re-tune the ThreadPool for a mode the rest of the process is not in.
        // The Settings UI surfaces that difference as a restart hint instead.
        var minWorkers = _plan.MinWorkerThreads.Value;
        var minIoc = _plan.MinIoCompletionThreads.Value;

        // Under manual tuning an operator may re-tune the floor live — the ThreadPool is the one
        // consumer that can honour that without a restart, and doing so is the documented reason
        // this section is hot-reloadable at all.
        if (_plan.ManualTuning)
        {
            minWorkers = _configuration.GetValue<int?>(PerformanceSizing.ConfigKeys.MinWorkerThreads) ?? minWorkers;
            minIoc = _configuration.GetValue<int?>(PerformanceSizing.ConfigKeys.MinIoCompletionThreads) ?? minIoc;
        }

        if (minWorkers <= 0 || minIoc <= 0) return;

        if (!ThreadPool.SetMinThreads(minWorkers, minIoc))
        {
            _logger.LogWarning(
                "ThreadPool.SetMinThreads rejected (workers={Workers}, io={Ioc}) — keeping previous floor (workers={PrevWorkers}, io={PrevIoc}).",
                minWorkers, minIoc, _appliedMinWorkers, _appliedMinIoc);
            return;
        }

        _appliedMinWorkers = minWorkers;
        _appliedMinIoc = minIoc;
        _logger.LogInformation("ThreadPool MinThreads tuned: workers={Workers}, io={Ioc}.", minWorkers, minIoc);
    }
}
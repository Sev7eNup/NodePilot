using NodePilot.EngineSwitcher.Models;

namespace NodePilot.EngineSwitcher.Services;

internal interface IServiceControlGateway
{
    ServiceSnapshot? TryGetService(string serviceName);
    Task SetStartModeAsync(string serviceName, ServiceStartMode mode, bool delayedAutoStart, CancellationToken cancellationToken);
    Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken);
    Task StopAsync(string serviceName, TimeSpan gracefulTimeout, TimeSpan forcedTimeout, CancellationToken cancellationToken);
    Task ForceStopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken);
}

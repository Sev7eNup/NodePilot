using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Core.Configuration;
using NodePilot.Engine.PowerShell;

namespace NodePilot.Engine.Tests.Helpers;

internal static class EngineFactories
{
    /// <summary>A factory as the service builds it, with experimental mode on or off.</summary>
    public static PowerShellEngineFactory Create(bool experimentalMode, int processorCount = 0)
    {
        var plan = PerformanceSizing.Create(
            new DetectedResources(processorCount > 0 ? processorCount : Environment.ProcessorCount, 8L * 1024 * 1024 * 1024, IsDesktop: false),
            manualTuning: false,
            new Dictionary<string, int?>()) with { ExperimentalMode = experimentalMode };
        return new PowerShellEngineFactory(NullLoggerFactory.Instance, configuration: null, plan);
    }
}

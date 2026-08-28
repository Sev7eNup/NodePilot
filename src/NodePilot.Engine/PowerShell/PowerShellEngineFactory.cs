using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodePilot.Core.Configuration;

namespace NodePilot.Engine.PowerShell;

/// <summary>
/// Factory that resolves the correct PowerShell execution engine based on the requested type.
/// "auto" prefers pwsh.exe (PS7), falls back to powershell.exe (PS5.1).
/// </summary>
public class PowerShellEngineFactory
{
    // Interface-typed so the internal test ctor can inject fakes with controlled IsAvailable
    // (host-independent "pwsh missing" / fallback assertions). Production still wires the
    // concrete ProcessExecutionEngine / RunspaceExecutionEngine instances below.
    private readonly IPowerShellExecutionEngine _pwsh;
    private readonly IPowerShellExecutionEngine _windowsPowerShell;
    private readonly IPowerShellExecutionEngine _runspace;

    /// <summary>Test seam: inject engines with controlled availability. Not for production
    /// use.</summary>
    internal PowerShellEngineFactory(
        IPowerShellExecutionEngine pwsh,
        IPowerShellExecutionEngine windowsPowerShell,
        IPowerShellExecutionEngine runspace)
    {
        _pwsh = pwsh;
        _windowsPowerShell = windowsPowerShell;
        _runspace = runspace;
    }

    public PowerShellEngineFactory(
        ILoggerFactory loggerFactory,
        IConfiguration? configuration = null,
        PerformancePlan? performancePlan = null)
    {
        var logger = loggerFactory.CreateLogger<PowerShellEngineFactory>();

        // Bounds the isolated stdout/stderr drain after the root process and its job tree exit,
        // so a leaked inherited pipe handle in another process cannot hold the write end open
        // and hang the step (see ProcessSpawnCoordinator). 0 or negative falls back to default.
        var drainGraceSeconds = configuration?.GetValue<int?>("Engine:IsolatedDrainGraceSeconds") ?? 5;
        var isolatedDrainGrace = drainGraceSeconds > 0 ? TimeSpan.FromSeconds(drainGraceSeconds) : (TimeSpan?)null;

        _pwsh = ProcessExecutionEngine.CreatePwsh(logger, isolatedDrainGrace);
        _windowsPowerShell = ProcessExecutionEngine.CreateWindowsPowerShell(logger, isolatedDrainGrace);

        // Runspace pool sizing belongs to the process-wide PerformancePlan (hardware-derived,
        // or operator-set under manual tuning). The fallback here covers only hosts that build
        // the factory without a plan (tests, CLI tooling) and mirrors the plan's auto formula.
        var plan = performancePlan ?? PerformanceSizing.Create(
            new DetectedResources(Environment.ProcessorCount, null, IsDesktop: false),
            manualTuning: false,
            new Dictionary<string, int?>());
        var minRunspaces = plan.MinRunspaces.Value;
        var maxRunspaces = plan.MaxRunspaces.Value;
        _runspace = new RunspaceExecutionEngine(logger, minRunspaces, maxRunspaces);

        logger.LogInformation("PowerShell engines: pwsh={PwshAvailable}, powershell={PSAvailable}, runspace=true (min={Min}, max={Max})",
            _pwsh.IsAvailable, _windowsPowerShell.IsAvailable, minRunspaces, maxRunspaces);
    }

    public IPowerShellExecutionEngine GetEngine(string engineType)
    {
        return engineType.ToLowerInvariant() switch
        {
            "pwsh" => _pwsh.IsAvailable ? _pwsh : throw new InvalidOperationException("pwsh.exe (PowerShell 7) is not installed"),
            "powershell" => _windowsPowerShell.IsAvailable ? _windowsPowerShell : throw new InvalidOperationException("powershell.exe is not available"),
            "runspace" => _runspace,
            // "auto" prefers the in-process runspace pool over spawning pwsh.exe or
            // powershell.exe. Runspace is PS5.1 (in-process SDK); workflows needing PS7-only
            // features (Foreach-Object -Parallel, ternary, …) must opt in via engine: "pwsh".
            "auto" => _runspace.IsAvailable ? _runspace
                : (_pwsh.IsAvailable ? _pwsh : _windowsPowerShell),
            _ => _runspace.IsAvailable ? _runspace
                : (_pwsh.IsAvailable ? _pwsh : _windowsPowerShell),
        };
    }

    /// <summary>
    /// Resolves an engine for an optionally process-isolated request. False
    /// <paramref name="isolated"/> delegates to the legacy overload. True requires an
    /// out-of-process host (the runspace pool cannot isolate a crash), so it throws instead of
    /// silently degrading to the un-isolated pool when no pwsh/powershell host is available.
    /// </summary>
    public IPowerShellExecutionEngine GetEngine(string engineType, bool isolated)
    {
        if (!isolated) return GetEngine(engineType);

        if (string.Equals(engineType, "powershell", StringComparison.OrdinalIgnoreCase))
            return _windowsPowerShell.IsAvailable
                ? _windowsPowerShell
                : throw new InvalidOperationException("powershell.exe is not available for isolated execution.");

        if (string.Equals(engineType, "pwsh", StringComparison.OrdinalIgnoreCase))
            return _pwsh.IsAvailable
                ? _pwsh
                : throw new InvalidOperationException("pwsh.exe (PowerShell 7) is not available for isolated execution.");

        // auto, runspace, and unknown all force a process engine, never the in-process pool.
        if (_pwsh.IsAvailable) return _pwsh;
        if (_windowsPowerShell.IsAvailable) return _windowsPowerShell;
        throw new InvalidOperationException(
            "Process-isolated execution requested but no PowerShell host (pwsh.exe / powershell.exe) is available.");
    }
}

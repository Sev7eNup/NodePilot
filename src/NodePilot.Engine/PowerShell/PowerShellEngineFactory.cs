using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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

    /// <summary>Test seam: inject engines with controlled availability. Not for production use.</summary>
    internal PowerShellEngineFactory(
        IPowerShellExecutionEngine pwsh,
        IPowerShellExecutionEngine windowsPowerShell,
        IPowerShellExecutionEngine runspace)
    {
        _pwsh = pwsh;
        _windowsPowerShell = windowsPowerShell;
        _runspace = runspace;
    }

    public PowerShellEngineFactory(ILoggerFactory loggerFactory, IConfiguration? configuration = null)
    {
        var logger = loggerFactory.CreateLogger<PowerShellEngineFactory>();

        // Bound the isolated stdout/stderr drain that runs after the root process exits + the job
        // tree is terminated. A leaked inherited pipe handle in an unrelated process would otherwise
        // keep the pipe write-end open forever and hang the step (see ProcessSpawnCoordinator).
        // 0/negative falls back to the engine default (5s).
        var drainGraceSeconds = configuration?.GetValue<int?>("Engine:IsolatedDrainGraceSeconds") ?? 5;
        var isolatedDrainGrace = drainGraceSeconds > 0 ? TimeSpan.FromSeconds(drainGraceSeconds) : (TimeSpan?)null;

        _pwsh = ProcessExecutionEngine.CreatePwsh(logger, isolatedDrainGrace);
        _windowsPowerShell = ProcessExecutionEngine.CreateWindowsPowerShell(logger, isolatedDrainGrace);

        // Runspace pool sizing: process-spawn pwsh.exe per script costs ~50-200ms (process
        // start + temp file write + module load) and ~30 MB RAM. The runspace pool reuses
        // in-process runspaces at <5 ms per script. Default max scales with CPU count
        // capped at 64; overridable via Engine:Runspace:MinRunspaces / :MaxRunspaces.
        var defaultMax = Math.Min(64, Math.Max(8, Environment.ProcessorCount * 4));
        var minRunspaces = configuration?.GetValue<int?>("Engine:Runspace:MinRunspaces") ?? 1;
        var maxRunspaces = configuration?.GetValue<int?>("Engine:Runspace:MaxRunspaces") ?? defaultMax;
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
            // Perf: "auto" prefers the in-process runspace pool over spawning a fresh
            // pwsh.exe / powershell.exe process per script. Runspace = PS5.1 (in-process
            // SDK); workflows that need PS7-only features (Foreach-Object -Parallel,
            // ternary operator, …) must opt in explicitly via engine: "pwsh".
            "auto" => _runspace.IsAvailable ? _runspace
                : (_pwsh.IsAvailable ? _pwsh : _windowsPowerShell),
            _ => _runspace.IsAvailable ? _runspace
                : (_pwsh.IsAvailable ? _pwsh : _windowsPowerShell),
        };
    }

    /// <summary>
    /// Engine resolution for an optionally process-isolated request. When <paramref name="isolated"/>
    /// is false this delegates to the legacy overload. When true, isolation REQUIRES an out-of-process
    /// host (the in-process runspace pool cannot contain a crash/leak), so the runspace pool is never
    /// returned: explicit pwsh/powershell must be available or it throws; auto/runspace/unknown prefer
    /// pwsh, then powershell, and throw if neither exists (rather than silently degrading to the
    /// un-isolated pool, which would void the opt-in security guarantee).
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

        // auto / runspace / unknown → force a process engine, never the in-process pool.
        if (_pwsh.IsAvailable) return _pwsh;
        if (_windowsPowerShell.IsAvailable) return _windowsPowerShell;
        throw new InvalidOperationException(
            "Process-isolated execution requested but no PowerShell host (pwsh.exe / powershell.exe) is available.");
    }

    public IReadOnlyList<string> GetAvailableEngines()
    {
        var engines = new List<string> { "auto" };
        if (_pwsh.IsAvailable) engines.Add("pwsh");
        if (_windowsPowerShell.IsAvailable) engines.Add("powershell");
        engines.Add("runspace");
        return engines;
    }
}

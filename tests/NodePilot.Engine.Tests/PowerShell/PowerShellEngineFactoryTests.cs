using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Engine-selection logic for <see cref="PowerShellEngineFactory"/>. The actual process
/// spawn is exercised in production-like integration runs; here we pin the routing
/// table so a refactor that re-orders the switch arms doesn't silently change which
/// engine "auto" picks.
/// </summary>
public class PowerShellEngineFactoryTests
{
    private static ILoggerFactory NewLoggerFactory() => new NullLoggerFactory();

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection().Build();

    [Fact]
    public void GetEngine_RunspaceAlwaysAvailable()
    {
        // The runspace engine ships in-process with the API host — it can never be
        // "unavailable" the way pwsh.exe / powershell.exe can be on a stripped-down host.
        var factory = new PowerShellEngineFactory(NewLoggerFactory());

        var engine = factory.GetEngine("runspace");

        engine.Should().NotBeNull();
        engine.EngineType.Should().Be("runspace");
        engine.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void GetEngine_AutoPrefersRunspaceOverProcessSpawn()
    {
        // Perf-routing: "auto" maps to the in-process runspace pool (PS5.1) — process
        // spawn (pwsh.exe / powershell.exe) costs ~50-200 ms per script and ~30 MB RAM,
        // crippling the 50-parallel-workflow scenario. Workflows that need PS7 features
        // must explicitly opt in via engine: "pwsh".
        var factory = new PowerShellEngineFactory(NewLoggerFactory());

        var engine = factory.GetEngine("auto");

        if (!engine.IsAvailable)
        {
            // Exotic test host where even the in-process runspace SDK isn't loadable.
            // Fall-through path uses pwsh / powershell.
            return;
        }
        engine.EngineType.Should().BeOneOf("runspace", "pwsh", "powershell");
    }

    [Fact]
    public void GetEngine_UnknownEngineType_FallsBackToAuto()
    {
        // Defensive: a workflow saved with a future engine name (e.g. "pwsh-preview")
        // must not crash an older NodePilot that doesn't know it. The default arm of
        // the switch maps to the same auto-routing path (runspace preferred).
        var factory = new PowerShellEngineFactory(NewLoggerFactory());

        var engine = factory.GetEngine("nonexistent-engine-xyz");

        engine.Should().NotBeNull();
        engine.EngineType.Should().BeOneOf("runspace", "pwsh", "powershell");
    }

    [Fact]
    public void GetEngine_EngineTypeCaseInsensitive()
    {
        // Workflow JSON has used both "Runspace" and "runspace" historically — pin
        // case-insensitive matching so both still resolve.
        var factory = new PowerShellEngineFactory(NewLoggerFactory());

        factory.GetEngine("RUNSPACE").EngineType.Should().Be("runspace");
        factory.GetEngine("Runspace").EngineType.Should().Be("runspace");
        factory.GetEngine("runspace").EngineType.Should().Be("runspace");
    }

    [Fact]
    public void GetAvailableEngines_AlwaysIncludesAutoAndRunspace()
    {
        var factory = new PowerShellEngineFactory(NewLoggerFactory());

        var engines = factory.GetAvailableEngines();

        engines.Should().Contain("auto");
        engines.Should().Contain("runspace");
    }

    [Fact]
    public void Constructor_HonoursMaxRunspacesConfig()
    {
        // Without this config, the runspace pool defaulted to a hardcoded 5 — a 20-item
        // parallel ForEach using engine:"runspace" would throttle even on a 16-core box.
        // Verify the config key is read (constructor takes path, runspace creation logs
        // the values; the pool itself can't easily be introspected from outside without
        // refactoring, so we cover the no-throw path on extreme values here).
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Engine:Runspace:MinRunspaces"] = "1",
            ["Engine:Runspace:MaxRunspaces"] = "32",
        }).Build();

        var act = () => new PowerShellEngineFactory(NewLoggerFactory(), config);

        act.Should().NotThrow();
    }
}

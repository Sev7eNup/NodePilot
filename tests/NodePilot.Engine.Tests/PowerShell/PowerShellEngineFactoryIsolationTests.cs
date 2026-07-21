using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Routing logic for the isolated overload <c>GetEngine(engineType, isolated)</c>. Uses the
/// internal test ctor with fake engines so "pwsh missing" / fallback behaviour can be asserted
/// independently of what is installed on the test host. Isolation must NEVER resolve to the
/// in-process runspace pool (which cannot contain a crash/leak) and must fail loudly rather than
/// silently degrade when no process host exists.
/// </summary>
public class PowerShellEngineFactoryIsolationTests
{
    private sealed class FakeEngine(string engineType, bool available) : IPowerShellExecutionEngine
    {
        public string EngineType => engineType;
        public bool IsAvailable => available;
        public Task<PowerShellExecutionResult> ExecuteAsync(PowerShellExecutionRequest request, CancellationToken ct)
            => Task.FromResult(new PowerShellExecutionResult { Success = true });
    }

    private static PowerShellEngineFactory Factory(bool pwsh = true, bool windows = true, bool runspace = true)
        => new(
            new FakeEngine("pwsh", pwsh),
            new FakeEngine("powershell", windows),
            new FakeEngine("runspace", runspace));

    [Fact]
    public void GetEngine_IsolatedWithRunspaceRequest_ReturnsProcessEngineNotRunspace()
    {
        // engine:"runspace" + isolated is a category error (runspace is in-process). Isolation
        // wins → a process engine, never the pool.
        var engine = Factory().GetEngine("runspace", isolated: true);

        engine.EngineType.Should().Be("pwsh");
        engine.EngineType.Should().NotBe("runspace");
    }

    [Fact]
    public void GetEngine_IsolatedWithAuto_PrefersPwsh()
    {
        Factory().GetEngine("auto", isolated: true).EngineType.Should().Be("pwsh");
    }

    [Fact]
    public void GetEngine_IsolatedAutoPwshUnavailable_FallsBackToWindowsPowerShell()
    {
        Factory(pwsh: false).GetEngine("auto", isolated: true).EngineType.Should().Be("powershell");
    }

    [Fact]
    public void GetEngine_IsolatedExplicitPwsh_ReturnsPwsh()
    {
        Factory().GetEngine("pwsh", isolated: true).EngineType.Should().Be("pwsh");
    }

    [Fact]
    public void GetEngine_IsolatedExplicitPwshUnavailable_Throws()
    {
        var act = () => Factory(pwsh: false).GetEngine("pwsh", isolated: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*pwsh*");
    }

    [Fact]
    public void GetEngine_IsolatedNoProcessHostAvailable_Throws()
    {
        // Neither pwsh nor powershell present → fail loudly, do NOT degrade to the runspace pool
        // (that would void the opt-in isolation guarantee).
        var act = () => Factory(pwsh: false, windows: false).GetEngine("auto", isolated: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*no PowerShell host*");
    }

    [Fact]
    public void GetEngine_IsolatedFalse_DelegatesToLegacyRouting()
    {
        // Non-isolated keeps the fast in-process pool for auto/runspace.
        Factory().GetEngine("runspace", isolated: false).EngineType.Should().Be("runspace");
        Factory().GetEngine("auto", isolated: false).EngineType.Should().Be("runspace");
    }
}

using FluentAssertions;
using NodePilot.Engine.PowerShell;
using Xunit;
using NodePilot.Engine.Tests.Helpers;

namespace NodePilot.Engine.Tests.PowerShell;

/// <summary>
/// Engine routing of <c>GetEngine</c> (plain and isolated) and <c>GetBuiltInEngine</c>. Uses the
/// internal test ctor with fake engines so "pwsh missing" / fallback behaviour can be asserted
/// independently of what is installed on the test host. Isolation must NEVER resolve to the
/// in-process runspace pool (which cannot contain a crash/leak) and must fail loudly rather than
/// silently degrade when no process host exists.
/// </summary>
public class PowerShellEngineFactoryIsolationTests
{

    private static PowerShellEngineFactory Factory(bool pwsh = true, bool windows = true, bool runspace = true)
        => new(
            new FakeEngine("pwsh", pwsh),
            new FakeEngine("powershell", windows),
            new FakeEngine("runspace", runspace));

    [Fact]
    public void GetEngine_IsolatedWithRunspaceRequest_Throws()
    {
        // The in-process pool cannot be isolated. Picking another engine silently would run the
        // script under a PowerShell the author did not choose.
        var act = () => Factory().GetEngine("runspace", isolated: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*runspace*cannot run isolated*");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("somethingUnknown")]
    public void GetEngine_IsolatedWithAuto_PrefersWindowsPowerShell(string engineType)
    {
        Factory().GetEngine(engineType, isolated: true).EngineType.Should().Be("powershell");
    }

    [Fact]
    public void GetEngine_IsolatedAutoWindowsPowerShellUnavailable_FallsBackToPwsh()
    {
        Factory(windows: false).GetEngine("auto", isolated: true).EngineType.Should().Be("pwsh");
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
        // Neither pwsh nor powershell present -> fail loudly, do NOT degrade to the runspace pool
        // (that would void the opt-in isolation guarantee).
        var act = () => Factory(pwsh: false, windows: false).GetEngine("auto", isolated: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*no PowerShell host*");
    }

    [Fact]
    public void GetEngine_IsolatedFalse_DelegatesToPlainRouting()
    {
        Factory().GetEngine("runspace", isolated: false).EngineType.Should().Be("runspace");
        Factory().GetEngine("auto", isolated: false).EngineType.Should().Be("powershell");
    }

    // --- non-isolated routing ---------------------------------------------------------

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("somethingUnknown")]
    public void GetEngine_Auto_IsWindowsPowerShellLikeARemoteStep(string engineType)
    {
        // A remote step runs in Windows PowerShell 5.1 on the target; a local one must see the
        // same modules.
        Factory().GetEngine(engineType).EngineType.Should().Be("powershell");
    }

    [Fact]
    public void GetEngine_AutoWindowsPowerShellUnavailable_FallsBackToPwshThenRunspace()
    {
        Factory(windows: false).GetEngine("auto").EngineType.Should().Be("pwsh");
        Factory(windows: false, pwsh: false).GetEngine("auto").EngineType.Should().Be("runspace");
    }

    [Theory]
    [InlineData("pwsh", "pwsh")]
    [InlineData("powershell", "powershell")]
    [InlineData("runspace", "runspace")]
    public void GetEngine_ExplicitEngine_IsHonoured(string engineType, string expected)
    {
        Factory().GetEngine(engineType).EngineType.Should().Be(expected);
    }

    [Fact]
    public void GetBuiltInEngine_PrefersTheInProcessPool()
    {
        Factory().GetBuiltInEngine().EngineType.Should().Be("runspace");
    }

    [Fact]
    public void GetBuiltInEngine_PoolUnavailable_FallsBackToAProcess()
    {
        Factory(runspace: false).GetBuiltInEngine().EngineType.Should().Be("pwsh");
        Factory(runspace: false, pwsh: false).GetBuiltInEngine().EngineType.Should().Be("powershell");
    }
}
